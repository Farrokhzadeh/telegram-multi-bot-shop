using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfSessionTests
{
    [Fact]
    public async Task SqliteEfAdminAdmissionRechecksRevocationAndTheDatabaseClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-admin-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var token = AdminToken.Create();
            Guid admin = Guid.NewGuid(), sessionId = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var seed = SqliteWallet.Command(tx, """
                    INSERT INTO admins(id,login,authentication_mode,external_subject) VALUES ($admin,'ef-admin','external','ef-admin');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$admin", admin), ("$session", sessionId), ("$hash", token.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await seed.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct);
            await sessions.RunEfAsync(true, null, async (session, cancel) =>
            {
                await session.RequireAdminAsync(actor, null, cancel);
                return true;
            }, ct);
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var revoke = SqliteWallet.Command(tx,
                    "UPDATE admin_sessions SET revoked_at=$now WHERE id=$session",
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$session", sessionId));
                await revoke.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.RunEfAsync(true, null, async (session, cancel) =>
            {
                await session.RequireAdminAsync(actor, null, cancel);
                return true;
            }, ct));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task PostgresEfSessionsShareTheNativeTransactionAndValidateWriterAdmissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BrandFixture.CreateAsync(ct);
        var brand = fixture.Contexts[0];
        var writer = fixture.Sessions.ForBrandWriter(brand, 0);
        await writer.RunEfAsync(true, async (session, token) =>
        {
            Guid member = await session.RequireCustomerAsync(brand, 7001, token);
            var user = await session.Data.Set<User>().SingleAsync(value => value.Id == member, token);
            user.Language = "en";
            return true;
        }, ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.RunEfAsync(true, async (session, token) =>
            await session.RequireCustomerAsync(fixture.Contexts[1], 7001, token), ct));
        var shared = fixture.Sessions.ForSharedWriters(fixture.Contexts.Select(context => new BrandWriterRegistration(context, 0)));
        await shared.RunEfAsync(true, async (session, token) =>
        {
            await session.RequireSharedWritersAsync(token);
            return true;
        }, ct);
        await writer.RunEfAsync(false, async (session, token) =>
        {
            Guid member = await session.RequireCustomerAsync(brand, 7001, token);
            Assert.Equal("en", await session.Data.Set<User>().Where(value => value.Id == member).Select(value => value.Language).SingleAsync(token));
            return true;
        }, ct);
    }

    [Fact]
    public async Task SqliteEfUnitOfWorkRequiresWriterAndCustomerScopeAndRollsBackCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-session-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            Guid member = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,
                        bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'ef','EF','EF','international','en','ef_bot',1001,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    """, ("$brand", brand.BrandId), ("$member", member));
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, ct);
            var binding = new WriterBinding([new BrandWriterRegistration(brand, 0)], false);
            await sessions.RunEfAsync(true, binding, async (session, token) =>
            {
                Assert.Equal(member, await session.RequireCustomerAsync(brand, 7001, token));
                var user = await session.Data.Set<User>().SingleAsync(value => value.Id == member, token);
                user.Language = "fa";
                return true;
            }, ct);
            await sessions.RunEfAsync(false, binding, async (session, token) =>
            {
                Assert.Equal("fa", await session.Data.Set<User>().Where(value => value.Id == member).Select(value => value.Language).SingleAsync(token));
                return true;
            }, ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.RunEfAsync(true, binding,
                async (session, token) => await session.RequireCustomerAsync(other, 7001, token), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.RunEfAsync(true, binding,
                async (session, token) => await session.RequireCustomerAsync(brand, 7002, token), ct));
            var stale = new WriterBinding([new BrandWriterRegistration(brand, 1)], false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.RunEfAsync(true, stale,
                async (session, token) => await session.RequireCustomerAsync(brand, 7001, token), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sessions.RunEfAsync(true, binding, (session, _) =>
            {
                session.Data.Set<User>().Attach(new User { Id = member }).Entity.Language = "en";
                return Task.FromResult(true);
            }, ct));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.RunEfAsync(true, binding, async (session, token) =>
            {
                await session.RequireCustomerAsync(brand, 7001, token);
                await session.UpdateAsync<User>(value => value.Id == member,
                    setters => setters.SetProperty(value => value.Language, "en"), token);
                cancelled.Cancel();
                return true;
            }, cancelled.Token));
            await sessions.RunEfAsync(false, binding, async (session, token) =>
            {
                Assert.Equal("fa", await session.Data.Set<User>().Select(value => value.Language).SingleAsync(token));
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
    [Fact]
    public async Task EfWritesRetainUnknownCommitFencesAfterTheContextIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BrandFixture.CreateAsync(ct);
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
            ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new Npgsql.NpgsqlConnectionStringBuilder(fixture.Database.Data.ConnectionString).Database };
        await using var proxy = new DropCommitReplyProxy(connection.ConnectionString);
        await using var transport = proxy.CreateClientDataSource();
        var writer = new DbSessionFactory(transport).ForBrandWriter(fixture.Contexts[0], 0);
        await using (var native = await writer.BeginAsync(ct))
        {
            await native.RunEfAsync(true, async (session, token) =>
            {
                Guid member = await session.RequireCustomerAsync(fixture.Contexts[0], 7001, token);
                var user = await session.Data.Set<User>().SingleAsync(value => value.Id == member, token);
                user.Language = "en";
                return true;
            }, ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => native.CommitAsync(ct));
            await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(DbRecovery.ReconcileBeforeRetry, error.Failure.Recovery);
            Assert.Equal(DbSessionState.OutcomeUnknown, native.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => native.CommitAsync(ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => native.RollbackAsync(ct));
        }
        await fixture.Sessions.ForBrandWriter(fixture.Contexts[0], 0).RunEfAsync(false, async (session, token) =>
        {
            Assert.Equal("en", await session.Data.Set<User>().Where(value => value.Id == fixture.Users[0]).Select(value => value.Language).SingleAsync(token));
            return true;
        }, ct);
    }

    [Fact]
    public async Task EfAdmissionLockTimeoutAbortsOnlyTheWaitingCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BrandFixture.CreateAsync(ct);
        var brand = fixture.Contexts[0];
        await using var owner = await fixture.Sessions.ForBrandWriter(brand, 0).BeginAsync(ct);
        await owner.RunEfAsync(true, async (session, token) => await session.RequireCustomerAsync(brand, 7001, token), ct);
        var options = new DbSessionOptions { StatementTimeout = TimeSpan.FromSeconds(2), LockTimeout = TimeSpan.FromMilliseconds(100) };
        await using var waiter = await new DbSessionFactory(fixture.Database.Data, options).ForBrandWriter(brand, 0).BeginAsync(ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => waiter.RunEfAsync(true,
            async (session, token) => await session.RequireCustomerAsync(brand, 7001, token), ct));
        Assert.Equal("55P03", error.Failure.SqlState);
        Assert.Equal(DbRecovery.RetryWholeTransactionAfterRollback, error.Failure.Recovery);
        Assert.Equal(DbSessionState.Aborted, waiter.State);
        Assert.Equal(DbSessionState.Active, owner.State);
        await waiter.RollbackAsync(ct);
        await owner.RollbackAsync(ct);
    }

}
