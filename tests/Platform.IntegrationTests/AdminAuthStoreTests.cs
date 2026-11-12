using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AdminAuthStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Lazy<string> Verifier = new(() => AdminAuth.CreatePasswordVerifier(new string('s', 20)));

    [Fact]
    public async Task RealSessionsAuditIdempotencyTwoChannelBindingAndRevocationQualifyTogether()
    {
        await using var database = await CreateAsync();
        await AdminQualification.RunAsync(database.Data, Ct);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("external")]
    [InlineData("missing")]
    [InlineData("wrong")]
    public async Task DisabledExternalUnknownAndWrongCredentialsCannotIssueSessions(string mode)
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        await using (var seed = await sessions.BeginAsync(Ct))
        {
            if (mode == "external")
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,external_subject) VALUES ($1,'public-admin','external','public-subject')", DbValue.Uuid(Guid.NewGuid())), Ct);
            else if (mode != "missing")
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-admin','password',$2)", DbValue.Uuid(Guid.NewGuid()), DbValue.Text(Verifier.Value)), Ct);
            if (mode == "disabled") await seed.ExecuteAsync(new DbStatement("UPDATE shop.admins SET disabled_at=clock_timestamp()"), Ct);
            await seed.CommitAsync(Ct);
        }
        await using var denied = await sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => AdminSessionStore.LoginAsync(denied, "public-admin",
            new string(mode == "wrong" ? 'x' : 's', 20).AsMemory(), TimeSpan.FromHours(1), Ct));
        Assert.Equal(DbSessionState.Aborted, denied.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.CommitAsync(Ct));
    }

    [Fact]
    public async Task CachedAuthorizationCannotSurviveRevocationOrCommitPriorLocalWrites()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = await SeedAsync(sessions);
        using var ticket = await LoginAsync(sessions);
        AuthenticatedAdmin actor;
        await using (var resolve = await sessions.BeginAsync(Ct)) actor = await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
        await using (var revoke = await sessions.BeginAsync(Ct))
        {
            await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admins SET auth_version=auth_version+1 WHERE id=$1", DbValue.Uuid(admin)), Ct);
            await revoke.CommitAsync(Ct);
        }
        Guid product = Guid.NewGuid();
        await using (var stale = await sessions.BeginAsync(Ct))
        {
            await stale.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate','public auth rollback')", DbValue.Uuid(product)), Ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => AdminSessionStore.RequireActiveAsync(stale, actor, cancellationToken: Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.CommitAsync(Ct));
        }
        await using var verify = await sessions.BeginAsync(Ct);
        Assert.Equal(0L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(product)), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task ExpiredSessionsAndRandomBearerTokensHaveNoAuthority()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = await SeedAsync(sessions);
        using var token = AdminToken.Create();
        await using (var seed = await sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.admin_sessions(id,admin_id,token_hash,auth_version_at_issue,issued_at,expires_at)
                VALUES ($1,$2,$3,0,clock_timestamp()-INTERVAL '2 hours',clock_timestamp()-INTERVAL '1 hour')
                """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(admin), DbValue.Binary(token.Hash())), Ct);
            await seed.CommitAsync(Ct);
        }
        await using (var expired = await sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<AdminAuthenticationException>(() => AdminSessionStore.AuthenticateAsync(expired, token, Ct));
        using var random = AdminToken.Create();
        await using var unknown = await sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => AdminSessionStore.AuthenticateAsync(unknown, random, Ct));
    }

    [Fact]
    public async Task LogoutIsImmediateAndAuditRowsCannotBeChanged()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = await SeedAsync(sessions);
        using var ticket = await LoginAsync(sessions);
        AuthenticatedAdmin actor;
        await using (var resolve = await sessions.BeginAsync(Ct)) actor = await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
        Guid auditId = Guid.NewGuid();
        await using (var audit = await sessions.BeginAsync(Ct))
        {
            Assert.True(await AdminAuditStore.AppendAsync(audit, actor, new AuditCommand(auditId, AuditAction.Update, AuditEntity.Admin,
                admin, null, AuditReason.ConfigurationChange, Guid.NewGuid(), SHA256.HashData("public command"u8)), Ct));
            await audit.CommitAsync(Ct);
        }
        await using (var change = await sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => change.ExecuteAsync(new DbStatement("UPDATE shop.audit_events SET reason='changed' WHERE id=$1", DbValue.Uuid(auditId)), Ct));
        await using (var logout = await sessions.BeginAsync(Ct))
        {
            await AdminSessionStore.LogoutAsync(logout, actor, Ct);
            await logout.CommitAsync(Ct);
        }
        await using var denied = await sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<AdminAuthenticationException>(() => AdminSessionStore.AuthenticateAsync(denied, ticket.Token, Ct));
    }

    [Fact]
    public async Task EndpointGuardUsesDatabaseBearerAuthenticationAndClearsItsContext()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = await SeedAsync(sessions);
        using var ticket = await LoginAsync(sessions);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.SetEndpoint(new Microsoft.AspNetCore.Http.Endpoint(_ => Task.CompletedTask,
            new Microsoft.AspNetCore.Http.EndpointMetadataCollection(Platform.Api.AdministratorEndpointMetadata.Instance), "private-admin"));
        context.Request.Headers.Authorization = "Bearer " + ticket.Token.ExportForTransport();
        bool called = false;
        await new Platform.Api.AdminGuardMiddleware(current =>
        {
            called = true;
            Assert.Equal(admin, Platform.Api.AdministratorEndpointExtensions.GetRequiredAdministrator(current).AdminId);
            return Task.CompletedTask;
        }, async (token, cancellation) =>
        {
            await using var verify = await sessions.BeginAsync(cancellation);
            return await AdminSessionStore.AuthenticateAsync(verify, token, cancellation);
        }).InvokeAsync(context);
        Assert.True(called);
        Assert.Throws<InvalidOperationException>(() => Platform.Api.AdministratorEndpointExtensions.GetRequiredAdministrator(context));
    }

    [Fact]
    public async Task ExpiredOrRevokedBindingChallengesCannotBecomeStaffIdentity()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = await SeedAsync(sessions);
        using var ticket = await LoginAsync(sessions);
        AuthenticatedAdmin actor;
        await using (var resolve = await sessions.BeginAsync(Ct)) actor = await AdminSessionStore.AuthenticateAsync(resolve, ticket.Token, Ct);
        AdminToken nonce;
        await using (var begin = await sessions.BeginAsync(Ct))
        {
            nonce = (await AdminBindingStore.BeginAsync(begin, actor, 98765, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!;
            await begin.CommitAsync(Ct);
        }
        using (nonce)
        {
            var registry = new BotBrandRegistry([new("test-bot", "a", Guid.NewGuid(), 1001, Guid.NewGuid(), 1, true)]);
            Assert.True(registry.TryResolveAuthenticated("test-bot", new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "a")], "test-bot")), out var bot));
            Assert.Throws<AdminAuthenticationException>(() => PrivateTelegramProof.FromVerifiedIngress(bot!, 98765, -100, false, nonce));
            Assert.Throws<AdminAuthenticationException>(() => PrivateTelegramProof.FromVerifiedIngress(bot!, 98765, 98765, true, nonce));
            var proof = PrivateTelegramProof.FromVerifiedIngress(bot!, 98765, 98765, false, nonce);
            await using (var expire = await sessions.BeginAsync(Ct))
            {
                await expire.ExecuteAsync(new DbStatement("UPDATE shop.admins SET binding_expires_at=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(admin)), Ct);
                await expire.CommitAsync(Ct);
            }
            await using (var denied = await sessions.BeginAsync(Ct))
                Assert.False(await AdminBindingStore.ObservePrivateProofAsync(denied, admin, nonce, proof, Ct));
            await using (var epoch = await sessions.BeginAsync(Ct))
            {
                await epoch.ExecuteAsync(new DbStatement("UPDATE shop.admins SET auth_version=auth_version+1,binding_expires_at=clock_timestamp()+INTERVAL '1 minute' WHERE id=$1", DbValue.Uuid(admin)), Ct);
                await epoch.CommitAsync(Ct);
            }
            await using var obsolete = await sessions.BeginAsync(Ct);
            Assert.False(await AdminBindingStore.ObservePrivateProofAsync(obsolete, admin, nonce, proof, Ct));
            Assert.False(await AdminBindingStore.IsVerifiedStaffAsync(obsolete, 98765, Ct));
        }
    }

    private static async Task<Guid> SeedAsync(DbSessionFactory sessions)
    {
        Guid admin = Guid.NewGuid();
        await using var seed = await sessions.BeginAsync(Ct);
        await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-admin','password',$2)",
            DbValue.Uuid(admin), DbValue.Text(Verifier.Value)), Ct);
        await seed.CommitAsync(Ct);
        return admin;
    }
    private static async Task<IssuedAdminSession> LoginAsync(DbSessionFactory sessions)
    {
        await using var login = await sessions.BeginAsync(Ct);
        var ticket = await AdminSessionStore.LoginAsync(login, "public-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        try { await login.CommitAsync(Ct); return ticket; }
        catch { ticket.Dispose(); throw; }
    }
    private static async Task<DisposablePostgres> CreateAsync()
    {
        var database = await DisposablePostgres.CreateAsync(Ct);
        try { await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct); return database; }
        catch { await database.DisposeAsync(); throw; }
    }
}
