using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfAdminSessionTests
{
    private static readonly Lazy<string> Verifier = new(() => AdminAuth.CreatePasswordVerifier(new string('s', 20)));
    [Fact]
    public async Task SqlitePasswordSessionsRetainTheirHashAfterTemporaryBuffersAreCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-login-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            Guid admin = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var seed = SqliteWallet.Command(tx, "INSERT INTO admins(id,login,authentication_mode,password_verifier) VALUES ($id,'ef-login','password',$verifier)",
                    ("$id", admin), ("$verifier", Verifier.Value));
                await seed.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            using var ticket = await sessions.RunEfAsync(true, null,
                (session, cancel) => AdminSessionStore.LoginAsync(session, "ef-login", new string('s', 20).AsMemory(), TimeSpan.FromMinutes(5), cancel), ct);
            Assert.Equal(DateTimeKind.Utc, ticket.ExpiresAt.Kind);
            Assert.Equal(admin, (await SqliteAdminSessionStore.AuthenticateAsync(sessions, ticket.Token, ct)).AdminId);
            await sessions.RunEfAsync(false, null, async (session, cancel) =>
            {
                var account = await session.Data.Set<Admin>().AsNoTracking().SingleAsync(cancel);
                var stored = await session.Data.Set<AdminSession>().AsNoTracking().SingleAsync(cancel);
                Assert.Equal(ticket.Token.Hash(), stored.TokenHash);
                Assert.Equal(account.LastLoginAt!.Value.AddMinutes(5), stored.ExpiresAt);
                Assert.Equal(DateTimeKind.Utc, stored.ExpiresAt.Kind);
                Assert.InRange(stored.ExpiresAt - stored.IssuedAt, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
                return true;
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => sessions.RunEfAsync(true, null,
                (session, cancel) => AdminSessionStore.LoginAsync(session, "ef-login", new string('x', 20).AsMemory(), TimeSpan.FromMinutes(5), cancel), ct));
            await sessions.RunEfAsync(false, null, async (session, cancel) =>
            {
                Assert.Equal(1, await session.Data.Set<AdminSession>().CountAsync(cancel));
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
    [Theory]
    [InlineData("expires_at='2000-01-01T00:00:00.0000000Z',issued_at='1999-01-01T00:00:00.0000000Z'")]
    [InlineData("revoked_at=$now")]
    [InlineData("auth_version_at_issue=1")]
    public async Task SqliteLegacyReadersReauthenticateInsideTheirCurrentTransaction(string mutation)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-reauth-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            Guid admin = Guid.NewGuid(), ticket = Guid.NewGuid();
            using var token = AdminToken.Create();
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var seed = SqliteWallet.Command(tx, """
                    INSERT INTO admins(id,login,authentication_mode,external_subject) VALUES ($id,'ef-auth','external','ef-auth');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at) VALUES ($ticket,$id,$hash,0,$expires)
                    """, ("$id", admin), ("$ticket", ticket), ("$hash", token.Hash()), ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await seed.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct);
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, cancel);
                using var change = SqliteWallet.Command(tx, "UPDATE admin_sessions SET " + mutation + " WHERE id=$ticket",
                    ("$ticket", ticket), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
                await change.ExecuteNonQueryAsync(cancel);
                await Assert.ThrowsAsync<AdminAuthenticationException>(() => SqliteAdminSessionStore.RequireActiveAsync(tx, actor, cancel));
                return true;
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct));
        }
        finally { directory.Delete(recursive: true); }
    }
    [Fact]
    public async Task PostgresAuditStepsValidateReferencesAtNativeCommitAfterBusinessCreation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BrandFixture.CreateAsync(ct);
        using var token = AdminToken.Create();
        Guid admin = Guid.NewGuid(), ticket = Guid.NewGuid(), product = Guid.NewGuid(), command = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.admins(id,login,authentication_mode,external_subject) VALUES ($1,'ef-audit','external','ef-audit');
                INSERT INTO shop.admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at) VALUES ($2,$1,$3,0,clock_timestamp()+INTERVAL '1 hour')
                """, DbValue.Uuid(admin), DbValue.Uuid(ticket), DbValue.Binary(token.Hash())), ct);
            await seed.CommitAsync(ct);
        }
        AuthenticatedAdmin actor;
        await using (var read = await fixture.Sessions.BeginAsync(ct)) actor = await AdminSessionStore.AuthenticateAsync(read, token, ct);
        var audit = new AuditCommand(command, AuditAction.Create, AuditEntity.Product, product, null, AuditReason.ConfigurationChange, command, SHA256.HashData("ef-audit"u8));
        await using (var write = await fixture.Sessions.BeginAsync(ct))
        {
            Assert.True(await AdminAuditStore.AppendAsync(write, actor, audit, ct));
            await write.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,title,fulfillment_kind,availability_policy) VALUES ($1,'EF audit','manual','immediate')", DbValue.Uuid(product)), ct);
            await write.CommitAsync(ct);
            Assert.Equal(DbSessionState.Committed, write.State);
        }
        await using (var replay = await fixture.Sessions.BeginAsync(ct))
        {
            Assert.False(await AdminAuditStore.AppendAsync(replay, actor, audit, ct));
            await replay.CommitAsync(ct);
        }
        Guid unadmitted = Guid.NewGuid();
        await using (var write = await fixture.Sessions.ForBrandWriter(fixture.Contexts[0], 0).BeginAsync(ct))
        {
            Assert.True(await AdminAuditStore.AppendAsync(write, actor, new(unadmitted, AuditAction.Update, AuditEntity.Product, product, null,
                AuditReason.ConfigurationChange, unadmitted, SHA256.HashData("writer"u8)), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => write.CommitAsync(ct));
            Assert.Equal(DbSessionState.Aborted, write.State);
        }
        Guid invalid = Guid.NewGuid();
        await using (var write = await fixture.Sessions.BeginAsync(ct))
        {
            Assert.True(await AdminAuditStore.AppendAsync(write, actor, new(invalid, AuditAction.Create, AuditEntity.Product, Guid.NewGuid(), null,
                AuditReason.ConfigurationChange, invalid, SHA256.HashData("missing"u8)), ct));
            var failure = await Assert.ThrowsAsync<DbOperationException>(() => write.CommitAsync(ct));
            Assert.Equal(DbRecovery.Reject, failure.Failure.Recovery);
            Assert.Equal(DbSessionState.Aborted, write.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(ct));
        }
        await using var verify = await fixture.Sessions.BeginAsync(ct);
        Assert.Equal(1L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.audit_events WHERE id=$1 OR id=$2 OR id=$3", DbValue.Uuid(command), DbValue.Uuid(invalid), DbValue.Uuid(unadmitted)), row => row.GetInt64(0), ct));
    }
}
