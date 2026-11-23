using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteRejectsIncompleteTrackedAndSetBasedWalletWrites(bool setBased)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            if (setBased) await session.UpdateAsync<Wallet>(value => value.UserId == fixture.Member,
                setters => setters.SetProperty(value => value.Balance, 7m).SetProperty(value => value.Version, 1L), token);
            else
            {
                var wallet = await session.Data.Set<Wallet>().SingleAsync(token);
                wallet.Balance = 7m;
                wallet.Version = 1;
            }
        }, ct));
        Assert.Contains("EF commit rejected: wallet_posting_balance", error.Message);
        Assert.Equal(0m, await fixture.BalanceAsync(ct));
    }

    [Fact]
    public async Task FlushedWritesRemainInScopeAndExactPostingsSurviveRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        const decimal opening = 12345678901234567890.12345678m;
        const decimal balance = 12345678901234567890.12345679m;
        await fixture.WriteAsync(async (session, token) =>
        {
            var wallet = await session.Data.Set<Wallet>().SingleAsync(token);
            wallet.Balance = opening;
            wallet.Version = 1;
            session.Data.Add(new WalletEntry
            {
                Id = Guid.NewGuid(),
                UserId = fixture.Member,
                SignedAmount = opening,
                BalanceAfter = opening,
                PostingVersion = 1,
                MovementKind = "opening_balance",
                IsPosting = true,
                IdempotencyKey = "ef-opening",
                LegacyImportBatch = Guid.NewGuid(),
                Reason = "Synthetic opening"
            });
            await session.Data.SaveChangesAsync(token);
            Assert.False(session.Data.ChangeTracker.HasChanges());
            Guid admin = Guid.NewGuid(), audit = Guid.NewGuid();
            session.Data.Add(new Admin { Id = admin, Login = "ef-adjustment", AuthenticationMode = "external", ExternalSubject = "ef-adjustment" });
            session.Data.Add(new AuditEvent
            {
                Id = audit,
                ActorKind = "admin",
                ActorAdminId = admin,
                BrandId = CheckoutFixture.ResolveContexts()[0].BrandId,
                Action = "wallet_adjustment",
                EntityKind = "wallet",
                EntityId = fixture.Member,
                Reason = "Synthetic adjustment",
                CorrelationId = Guid.NewGuid()
            });
            wallet.Balance = balance;
            wallet.Version = 2;
            session.Data.Add(new WalletEntry
            {
                Id = Guid.NewGuid(),
                UserId = fixture.Member,
                SignedAmount = 0.00000001m,
                BalanceAfter = balance,
                PostingVersion = 2,
                MovementKind = "admin_adjustment",
                IsPosting = true,
                IdempotencyKey = "ef-adjustment",
                AdminId = admin,
                AuditEventId = audit,
                Reason = "Synthetic adjustment"
            });
            await session.Data.SaveChangesAsync(token);
        }, ct);
        Assert.Equal(balance, await fixture.BalanceAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var entry = await session.Data.Set<WalletEntry>().OrderBy(value => value.PostingVersion).FirstAsync(token);
            entry.Reason = "Changed history";
        }, ct));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            await session.UpdateAsync<Wallet>(value => value.UserId == fixture.Member,
                setters => setters.SetProperty(value => value.Version, 3L), token);
            await session.Data.SaveChangesAsync(token);
        }, ct));
        Assert.Contains("EF commit rejected: wallet_posting_balance", error.Message);
        Assert.Equal(balance, await fixture.BalanceAsync(ct));
    }

    [Fact]
    public async Task APostingWithTheWrongExactPredecessorRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var wallet = await session.Data.Set<Wallet>().SingleAsync(token);
            wallet.Balance = 8m;
            wallet.Version = 1;
            session.Data.Add(new WalletEntry
            {
                Id = Guid.NewGuid(),
                UserId = fixture.Member,
                SignedAmount = 7m,
                BalanceAfter = 8m,
                PostingVersion = 1,
                MovementKind = "opening_balance",
                IsPosting = true,
                IdempotencyKey = "wrong-sum",
                LegacyImportBatch = Guid.NewGuid(),
                Reason = "Synthetic invalid sum"
            });
        }, ct));
        Assert.Contains("EF commit rejected: wallet_posting_chain", error.Message);
        Assert.Equal(0m, await fixture.BalanceAsync(ct));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("raw")]
    [InlineData("save")]
    [InlineData("sync")]
    public async Task UnscopedWritesPoisonTheCommandEvenWhenTheCallerCatchesTheError(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                switch (operation)
                {
                    case "update": await session.Data.Set<Wallet>().ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Balance, 7m), token); break;
                    case "delete": await session.Data.Set<Wallet>().ExecuteDeleteAsync(token); break;
                    case "raw": await session.Data.Database.ExecuteSqlRawAsync("UPDATE wallets SET balance='7'", token); break;
                    case "sync": session.Data.SaveChanges(); break;
                    case "save":
                        session.Data.Add(new WalletEntry { Id = Guid.NewGuid() });
                        await session.Data.SaveChangesAsync(token);
                        session.Data.ChangeTracker.Clear();
                        break;
                }
            });
        }, ct));
        Assert.Equal(0m, await fixture.BalanceAsync(ct));
    }

    [Fact]
    public async Task AuditValidationUsesTheAffectedCustomerBrandAndPreventsEarlySaveBypass()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            session.Data.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorKind = "customer",
                ActorUserId = fixture.Member,
                BrandId = null,
                Action = "create",
                EntityKind = "user",
                EntityId = fixture.Member,
                Reason = "Synthetic invalid tenant",
                CorrelationId = Guid.NewGuid()
            });
            await session.Data.SaveChangesAsync(token);
        }, ct));
        Assert.Contains("EF commit rejected: audit_customer_brand", error.Message);
    }

    [Fact]
    public async Task ReadOnlyAndUnadmittedSessionsCannotUseTheMutationAdapter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        foreach (bool write in new[] { false, true })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Sessions.RunEfAsync(write, fixture.Binding, async (session, token) =>
            {
                await session.UpdateAsync<User>(value => value.Id == fixture.Member,
                    setters => setters.SetProperty(value => value.Language, "fa"), token);
                return true;
            }, ct));
    }

    [Fact]
    public async Task PostgresRejectsTheIncompleteWalletBeforeTheNativeCommitPhase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BrandFixture.CreateAsync(ct);
        var writer = fixture.Sessions.ForBrandWriter(fixture.Contexts[0], 0);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => writer.RunEfAsync(true, async (session, token) =>
        {
            Guid member = await session.RequireCustomerAsync(fixture.Contexts[0], 7001, token);
            await session.UpdateAsync<Wallet>(value => value.UserId == member,
                setters => setters.SetProperty(value => value.Balance, 7m).SetProperty(value => value.Version, 1L), token);
            return true;
        }, ct));
        Assert.Equal(DbFailureKind.Unexpected, error.Failure.Kind);
        Assert.Equal(DbRecovery.Reject, error.Failure.Recovery);
    }

    [Fact]
    public async Task PostgresAcceptsExactScopedUpdatesWithTheirTrackedAuditAndPosting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CheckoutFixture.CreateAsync(users: 1, balance: 0, stock: 0, ct: ct);
        var actor = await fixture.AdminAsync(ct);
        const decimal balance = 12345678901234567890.12345678m;
        await fixture.Sessions.RunEfAsync(true, async (session, token) =>
        {
            await session.RequireAdminAsync(actor, null, token);
            Assert.Equal(1, await session.UpdateAsync<Wallet>(value => value.UserId == fixture.Users[0] && value.Version == 0,
                setters => setters.SetProperty(value => value.Balance, balance).SetProperty(value => value.Version, 1L), token));
            Guid audit = Guid.NewGuid();
            session.Data.Add(new AuditEvent
            {
                Id = audit,
                ActorKind = "admin",
                ActorAdminId = actor.AdminId,
                BrandId = fixture.Brands[0],
                Action = "wallet_adjustment",
                EntityKind = "wallet",
                EntityId = fixture.Users[0],
                Reason = "Synthetic exact adjustment",
                CorrelationId = Guid.NewGuid()
            });
            session.Data.Add(new WalletEntry
            {
                Id = Guid.NewGuid(),
                UserId = fixture.Users[0],
                SignedAmount = balance,
                BalanceAfter = balance,
                PostingVersion = 1,
                MovementKind = "admin_adjustment",
                IsPosting = true,
                IdempotencyKey = "ef-adjustment",
                AdminId = actor.AdminId,
                AuditEventId = audit,
                Reason = "Synthetic exact adjustment"
            });
            await session.Data.SaveChangesAsync(token);
            return true;
        }, ct);
        await fixture.Sessions.RunEfAsync(false, async (session, token) =>
        {
            Assert.Equal(balance, await session.Data.Set<Wallet>().Where(value => value.UserId == fixture.Users[0]).Select(value => value.Balance).SingleAsync(token));
            return true;
        }, ct);
    }

    [Fact]
    public async Task ARejectedCustomerAdmissionCannotBeCaughtToContinueAWritableCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.RequireCustomerAsync(CheckoutFixture.ResolveContexts()[1], 7001, token));
            session.Data.Attach(new User { Id = fixture.Member }).Entity.Language = "fa";
        }, ct));
        Assert.Equal("en", await fixture.Sessions.RunEfAsync(false, fixture.Binding,
            async (session, token) => await session.Data.Set<User>().Select(value => value.Language).SingleAsync(token), ct));
    }

    private sealed class Fixture(DirectoryInfo directory, SqliteSessionFactory sessions, BrandContext brand, Guid member) : IAsyncDisposable
    {
        internal Guid Member { get; } = member;
        internal SqliteSessionFactory Sessions { get; } = sessions;
        internal WriterBinding Binding { get; } = new([new BrandWriterRegistration(brand, 0)], false);
        internal static async Task<Fixture> CreateAsync(CancellationToken ct)
        {
            var directory = Directory.CreateTempSubdirectory("akanti-ef-integrity-");
            try
            {
                string path = Path.Combine(directory.FullName, "shop.db");
                await SqliteSchema.InitializeAsync(path, ct);
                var sessions = new SqliteSessionFactory(path);
                var brand = CheckoutFixture.ResolveContexts()[0];
                Guid member = Guid.NewGuid();
                await sessions.RunAsync(true, async (_, tx, token) =>
                {
                    using var seed = SqliteWallet.Command(tx, """
                        INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
                        VALUES($brand,'ef-integrity','EF','EF','international','en','ef_integrity_bot',$bot,x'01','fixture',1);
                        INSERT INTO users(id,brand_id,telegram_id,language) VALUES($member,$brand,7001,'en');
                        INSERT INTO wallets(user_id) VALUES($member);
                        """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member));
                    await seed.ExecuteNonQueryAsync(token);
                    return true;
                }, ct);
                return new(directory, sessions, brand, member);
            }
            catch { directory.Delete(true); throw; }
        }
        internal Task<bool> WriteAsync(Func<EfSession, CancellationToken, Task> operation, CancellationToken ct) =>
            Sessions.RunEfAsync(true, Binding, async (session, token) =>
            {
                await session.RequireCustomerAsync(brand, 7001, token);
                await operation(session, token);
                return true;
            }, ct);
        internal Task<decimal> BalanceAsync(CancellationToken ct) => Sessions.RunEfAsync(false, Binding,
            async (session, token) => await session.Data.Set<Wallet>().Select(value => value.Balance).SingleAsync(token), ct);
        public ValueTask DisposeAsync() { directory.Delete(true); return ValueTask.CompletedTask; }
    }
}
