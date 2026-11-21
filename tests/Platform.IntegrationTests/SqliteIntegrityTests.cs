using Microsoft.Data.Sqlite;
using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class SqliteIntegrityTests
{
    [Fact]
    public async Task WalletPostingChecksRejectIncompleteCommitsAndPreserveAcceptedBalance()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-integrity-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            Guid brand = Guid.NewGuid(), member = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                    VALUES ($brand,'integrity','Integrity','Integrity','international','en','integrity_bot',81,x'01','fixture');
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,8001,'en');
                    INSERT INTO wallets(user_id) VALUES ($member);
                    """, token, ("$brand", brand), ("$member", member));
                return true;
            }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, "UPDATE wallets SET balance='7',version=1 WHERE user_id=$member", token, ("$member", member));
                return true;
            }, ct));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, """
                    UPDATE wallets SET balance='7',version=1 WHERE user_id=$member;
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,
                        idempotency_key,legacy_import_batch,reason)
                    VALUES ($entry,$member,'7','7',1,'opening_balance',true,'fixture-opening',$batch,'fixture');
                    """, token, ("$member", member), ("$entry", Guid.NewGuid()), ("$batch", Guid.NewGuid()));
                return true;
            }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, "UPDATE wallets SET balance='9',version=2 WHERE user_id=$member",
                    token, ("$member", member));
                return true;
            }, ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, "SELECT balance,version FROM wallets WHERE user_id=$member", ("$member", member));
                await using var row = await command.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("7", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReleaseAndAuditTenantChecksRejectInvalidAtomicWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-scope-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(), member = Guid.NewGuid(), product = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                    VALUES ($first,'first','First','First','international','en','first_bot',91,x'01','fixture');
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                    VALUES ($second,'second','Second','Second','international','en','second_bot',92,x'01','fixture');
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$first,9001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    """, token, ("$first", first), ("$second", second), ("$member", member), ("$product", product));
                return true;
            }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, """
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,currency,payment_method,
                        unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                        brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,
                        local_first_snapshot,is_preorder,payment_confirmed_at)
                    VALUES ($order,$member,$product,1,'product_purchase','paid','ready','USDT','wallet',
                        '1',false,'5000','fixture','First','Fixture','wallet','local',true,false,$now);
                    """, token, ("$order", Guid.NewGuid()), ("$member", member), ("$product", product),
                    ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
                return true;
            }, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunAsync(true, async (_, tx, token) =>
            {
                await ExecuteAsync(tx, """
                    INSERT INTO audit_events(id,actor_kind,actor_user_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                    VALUES ($event,'customer',$member,$second,'create','brand',$second,'fixture',$event);
                    """, token, ("$event", Guid.NewGuid()), ("$member", member), ("$second", second));
                return true;
            }, ct));
            var unchanged = await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var command = SqliteWallet.Command(tx, "SELECT (SELECT count(*) FROM orders)+(SELECT count(*) FROM audit_events)");
                return (long)(await command.ExecuteScalarAsync(token))!;
            }, ct);
            Assert.Equal(0L, unchanged);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task ExecuteAsync(SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] values)
    {
        using var command = SqliteWallet.Command(tx, sql, values.Select(v => (v.Name, (object?)v.Value)).ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }
}
