using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteWalletAdministrationTests
{
    [Fact]
    public async Task AuditedWalletAdjustmentPostsOnceRejectsChangedReplayAndPreventsOverdraw()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-admin-wallet-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            using var adminToken = AdminToken.Create();
            Guid actorId = Guid.NewGuid(), session = Guid.NewGuid(), member = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var seed = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01',1);
                    INSERT INTO users(id,brand_id,telegram_id,language)
                    VALUES ($member,$brand,7001,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'synthetic-wallet-admin','external','fixture');
                    INSERT INTO admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                    VALUES ($session,$admin,$hash,0,$expires);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$admin", actorId), ("$session", session), ("$hash", adminToken.Hash()),
                    ("$expires", SqliteSchema.UtcText(DateTime.UtcNow.AddHours(1))));
                await seed.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, adminToken, ct);
            var store = new SqliteWalletAdministrationStore(sessions);
            var add = new WalletAdjustment(member, "10.125", 0, "Synthetic approved credit", Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdjustAsync(actor, brand,
                add with { AmountUsd = "-1", CommandId = Guid.NewGuid() }, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdjustAsync(actor, brand,
                add with { ExpectedVersion = 9, CommandId = Guid.NewGuid() }, ct));
            Assert.Equal(add.CommandId, await store.AdjustAsync(actor, brand, add, ct));
            Assert.Equal(add.CommandId, await new SqliteWalletAdministrationStore(SqliteSessionFactory.ForPlaintext(path))
                .AdjustAsync(actor, brand, add, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                store.AdjustAsync(actor, brand, add with { AmountUsd = "11" }, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                store.AdjustAsync(actor, other, add with { CommandId = Guid.NewGuid() }, ct));
            var debit = new WalletAdjustment(member, "-3.125", 1, "Synthetic approved debit", Guid.NewGuid());
            Assert.Equal(debit.CommandId, await store.AdjustAsync(actor, brand, debit, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AdjustAsync(actor, brand, debit with
                {
                    AmountUsd = "-8",
                    ExpectedVersion = 2,
                    CommandId = Guid.NewGuid()
                }, ct));
            await SqliteSessionFactory.ForPlaintext(path).RunAsync(false, async (_, tx, token) =>
            {
                var wallet = await SqliteWallet.ReadAsync(tx, member, token);
                Assert.Equal(7m, wallet.Balance.Amount);
                Assert.Equal(2, wallet.Version);
                using var query = SqliteWallet.Command(tx, """
                    SELECT (SELECT count(*) FROM wallet_entries WHERE user_id=$member
                        AND movement_kind='admin_adjustment'),
                        (SELECT count(*) FROM audit_events WHERE actor_admin_id=$admin
                            AND action='wallet_adjustment')
                    """, ("$member", member), ("$admin", actorId));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(2, row.GetInt64(0));
                Assert.Equal(2, row.GetInt64(1));
                return 0;
            }, ct);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var revoke = SqliteWallet.Command(tx,
                    "UPDATE admin_sessions SET revoked_at=$now WHERE id=$id",
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$id", session));
                return await revoke.ExecuteNonQueryAsync(token);
            }, ct);
            await Assert.ThrowsAsync<AdminAuthenticationException>(() =>
                store.AdjustAsync(actor, brand, add, ct));
        }
        finally { directory.Delete(recursive: true); }
    }
}
