using System.Security.Claims;
using System.Security.Cryptography;
using Npgsql;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class AdminQualification
{
    public static async Task RunAsync(NpgsqlDataSource data, CancellationToken cancellationToken = default)
    {
        var sessions = new DbSessionFactory(data);
        Guid admin = Guid.NewGuid();
        string password = new('s', 20);
        await using (var seed = await sessions.BeginAsync(cancellationToken))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-admin-qualification','password',$2)",
                DbValue.Uuid(admin), DbValue.Text(AdminAuth.CreatePasswordVerifier(password))), cancellationToken);
            await seed.CommitAsync(cancellationToken);
        }
        using var ticket = await LoginAsync(sessions, password, cancellationToken);
        var actor = await ResolveAsync(sessions, ticket.Token, cancellationToken);
        if (actor.AdminId != admin || actor.AuthVersion != 0 || ticket.ToString().Contains(ticket.Token.ExportForTransport(), StringComparison.Ordinal))
            throw new InvalidOperationException("Administrator session qualification failed.");
        await using (var verify = await sessions.BeginAsync(cancellationToken))
        {
            byte[] stored = await verify.QuerySingleAsync(new DbStatement("SELECT token_hash FROM shop.admin_sessions WHERE id=$1", DbValue.Uuid(ticket.Id)), row => row.GetBinary(0), cancellationToken);
            Check(CryptographicOperations.FixedTimeEquals(stored, ticket.Token.Hash()));
        }
        Guid auditId = Guid.NewGuid();
        byte[] commitment = SHA256.HashData("public reviewed configuration"u8);
        await using (var audit = await sessions.BeginAsync(cancellationToken))
        {
            Check(await AdminAuditStore.AppendAsync(audit, actor, Command(auditId, admin, commitment), cancellationToken));
            await audit.CommitAsync(cancellationToken);
        }
        await using (var replay = await sessions.BeginAsync(cancellationToken))
        {
            Check(!await AdminAuditStore.AppendAsync(replay, actor, Command(auditId, admin, commitment), cancellationToken));
            await replay.CommitAsync(cancellationToken);
        }
        await using (var collision = await sessions.BeginAsync(cancellationToken))
        {
            bool rejected = false;
            try { await AdminAuditStore.AppendAsync(collision, actor, Command(auditId, admin, SHA256.HashData("different command"u8)), cancellationToken); }
            catch (DbOperationException) { rejected = collision.State == DbSessionState.Aborted; }
            Check(rejected);
        }
        AdminToken challenge;
        await using (var begin = await sessions.BeginAsync(cancellationToken))
        {
            challenge = await AdminBindingStore.BeginAsync(begin, actor, 12345, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), cancellationToken)
                ?? throw new InvalidOperationException("Binding challenge was not issued.");
            await begin.CommitAsync(cancellationToken);
        }
        using (challenge)
        {
            Guid completion = Guid.NewGuid();
            await using (var premature = await sessions.BeginAsync(cancellationToken))
                await DeniedAsync(() => AdminBindingStore.CompleteAsync(premature, actor, 12345, 0, challenge, completion, Guid.NewGuid(), cancellationToken));
            var registry = new BotBrandRegistry([new("qualification-bot", "a", Guid.NewGuid(), 101, Guid.NewGuid(), 1, true)]);
            Check(registry.TryResolveAuthenticated("qualification-bot", new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "a")], "qualification-bot")), out var bot));
            await using (var wrong = await sessions.BeginAsync(cancellationToken))
            {
                var proof = PrivateTelegramProof.FromVerifiedIngress(bot!, 12346, 12346, false, challenge);
                Check(!await AdminBindingStore.ObservePrivateProofAsync(wrong, admin, challenge, proof, cancellationToken));
                await wrong.CommitAsync(cancellationToken);
            }
            await using (var observe = await sessions.BeginAsync(cancellationToken))
            {
                var proof = PrivateTelegramProof.FromVerifiedIngress(bot!, 12345, 12345, false, challenge);
                Check(await AdminBindingStore.ObservePrivateProofAsync(observe, admin, challenge, proof, cancellationToken));
                Check(!await AdminBindingStore.ObservePrivateProofAsync(observe, admin, challenge, proof, cancellationToken));
                await observe.CommitAsync(cancellationToken);
            }
            await using (var complete = await sessions.BeginAsync(cancellationToken))
            {
                Check(await AdminBindingStore.CompleteAsync(complete, actor, 12345, 0, challenge, completion, Guid.NewGuid(), cancellationToken));
                Check(await AdminBindingStore.IsVerifiedStaffAsync(complete, 12345, cancellationToken));
                await complete.CommitAsync(cancellationToken);
            }
            await using (var stale = await sessions.BeginAsync(cancellationToken))
                await DeniedAsync(() => AdminSessionStore.AuthenticateAsync(stale, ticket.Token, cancellationToken));
            using var fresh = await LoginAsync(sessions, password, cancellationToken);
            var renewedActor = await ResolveAsync(sessions, fresh.Token, cancellationToken);
            Check(renewedActor.AuthVersion == 1);
            await using (var replay = await sessions.BeginAsync(cancellationToken))
            {
                Check(!await AdminBindingStore.CompleteAsync(replay, renewedActor, 12345, 0, challenge, completion, Guid.NewGuid(), cancellationToken));
                await replay.CommitAsync(cancellationToken);
            }
            await using (var revoke = await sessions.BeginAsync(cancellationToken))
            {
                Check(await AdminRevocationStore.RevokeAsync(revoke, renewedActor, admin, 1, Guid.NewGuid(), Guid.NewGuid(),
                    revokeTelegram: true, cancellationToken: cancellationToken));
                Check(!await AdminBindingStore.IsVerifiedStaffAsync(revoke, 12345, cancellationToken));
                await revoke.CommitAsync(cancellationToken);
            }
            await using (var revoked = await sessions.BeginAsync(cancellationToken))
                await DeniedAsync(() => AdminSessionStore.AuthenticateAsync(revoked, fresh.Token, cancellationToken));
        }
    }

    private static AuditCommand Command(Guid id, Guid admin, byte[] digest) => new(id, AuditAction.Update, AuditEntity.Admin, admin, null,
        AuditReason.ConfigurationChange, Guid.NewGuid(), digest);
    private static async Task<IssuedAdminSession> LoginAsync(DbSessionFactory sessions, string password, CancellationToken cancellationToken)
    {
        await using var transaction = await sessions.BeginAsync(cancellationToken);
        var ticket = await AdminSessionStore.LoginAsync(transaction, "public-admin-qualification", password.AsMemory(), TimeSpan.FromHours(1), cancellationToken);
        try { await transaction.CommitAsync(cancellationToken); return ticket; }
        catch { ticket.Dispose(); throw; }
    }
    private static async Task<AuthenticatedAdmin> ResolveAsync(DbSessionFactory sessions, AdminToken token, CancellationToken cancellationToken)
    {
        await using var transaction = await sessions.BeginAsync(cancellationToken);
        return await AdminSessionStore.AuthenticateAsync(transaction, token, cancellationToken);
    }
    private static async Task DeniedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (AdminAuthenticationException error) when (error.InnerException is null) { return; }
        throw new InvalidOperationException("Unauthorized administrator operation accepted.");
    }
    private static void Check(bool valid)
    {
        if (!valid) throw new InvalidOperationException("Administrator qualification invariant failed.");
    }
}
