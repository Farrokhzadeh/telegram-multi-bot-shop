using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteCheckoutTests
{
    [Fact]
    public async Task ConcurrentPurchasesAndTopupsPostOnceWithExactBalances()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-checkout-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid member = Guid.NewGuid(), admin = Guid.NewGuid(), audit = Guid.NewGuid(), product = Guid.NewGuid(), stock = Guid.NewGuid();
            const string credential = "public synthetic checkout credential";
            var cipher = new InventoryCipher(secrets);
            await sessions.RunAsync(true, async (connection, tx, token) =>
            {
                using var setup = connection.CreateCommand();
                setup.Transaction = tx;
                setup.CommandText = """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'fixture','Fixture','Fixture','international','en','fixture_bot',$bot,x'01','current',true);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,$actor,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject) VALUES ($admin,'fixture','external','fixture');
                    INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                    VALUES ($audit,'admin',$admin,$brand,'wallet_adjustment','wallet',$member,'fixture',$audit);
                    INSERT INTO wallets(user_id,balance,version) VALUES ($member,'20.25',1);
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,admin_id,audit_event_id,reason)
                    VALUES ($audit,$member,'20.25','20.25',1,'admin_adjustment',true,'fixture-funding',$admin,$audit,'fixture');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt) VALUES ($brand,$product,true,'10.125');
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$encrypted,'current',$hash,'local_import');
                    """;
                setup.Parameters.AddWithValue("$brand", brand.BrandId);
                setup.Parameters.AddWithValue("$bot", brand.BotId);
                setup.Parameters.AddWithValue("$actor", CheckoutFixture.Actor(0));
                setup.Parameters.AddWithValue("$member", member);
                setup.Parameters.AddWithValue("$admin", admin);
                setup.Parameters.AddWithValue("$audit", audit);
                setup.Parameters.AddWithValue("$product", product);
                setup.Parameters.AddWithValue("$stock", stock);
                setup.Parameters.AddWithValue("$encrypted", cipher.Encrypt(stock, Encoding.UTF8.GetBytes(credential)));
                setup.Parameters.AddWithValue("$hash", SHA256.HashData(stock.ToByteArray()));
                return await setup.ExecuteNonQueryAsync(token);
            }, ct);

            var store = new SqliteCheckoutStore(sessions, secrets);
            LocalCheckoutRequest[] requests = [new(Guid.NewGuid(), product, 1, 10.125m), new(Guid.NewGuid(), product, 1, 10.125m)];
            CheckoutResult[] results = await Task.WhenAll(requests.Select(request => Task.Run(() => Purchase(request), ct)));
            Guid winner = Assert.IsType<Guid>(Assert.Single(results, r => r.Outcome == CheckoutOutcome.Purchased).OrderId);
            Assert.Single(results, r => r.Outcome == CheckoutOutcome.OutOfStock);
            var accepted = Assert.Single(requests, r => r.OrderId == winner);
            Assert.Equal(CheckoutOutcome.Replayed, (await Purchase(accepted)).Outcome);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Purchase(accepted with { Quantity = 2 }));

            await sessions.RunAsync(false, async (connection, tx, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    SELECT w.balance,w.version,
                        (SELECT count(*) FROM wallet_entries WHERE movement_kind='purchase_debit'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest'),
                        f.id,f.delivered_content,f.delivered_content_key_id
                    FROM wallets w JOIN orders o ON o.user_id=w.user_id JOIN fulfillments f ON f.order_id=o.id
                    JOIN inventory_items i ON i.assigned_fulfillment_id=f.id AND i.state='allocated'
                    """;
                await using var row = await command.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("10.125", row.GetString(0));
                Assert.Equal(2, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                byte[] plain = new BrandCipher(secrets).Decrypt(brand, "fulfillments", row.GetGuid(4), "delivered_content", row.GetString(6), row.GetFieldValue<byte[]>(5));
                try { Assert.Equal(credential, Encoding.UTF8.GetString(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Assert.False(await row.ReadAsync(token));
                return true;
            }, ct);

            Guid payment = Guid.NewGuid();
            await sessions.RunAsync(true, async (connection, tx, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO payments(id,topup_user_id,payment_method,quote_currency,amount_due,amount_received,provider_code,provider_payment_ref,
                        idempotency_ref,network_asset,expected_network_amount,network_amount,provider_status_code,status,ipn_key_id,ipn_signature,
                        ipn_payload_sha256,ipn_verified_at,first_payment_observed_at,confirmed_at,evidence_asset_ref,evidence_digest)
                    VALUES ($id,$member,'crypto','USD','7.125','7.125','nowpayments','123456','public-topup','USDTBSC','7.125','7.125',
                        'finished','confirming','fixture',zeroblob(64),zeroblob(32),$now,$now,$now,'public-fixture',zeroblob(32));
                    """;
                command.Parameters.AddWithValue("$id", payment);
                command.Parameters.AddWithValue("$member", member);
                command.Parameters.AddWithValue("$now", SqliteSchema.UtcText(DateTime.UtcNow));
                return await command.ExecuteNonQueryAsync(token);
            }, ct);
            var settlement = new SqliteTopupSettlementStore(sessions);
            PaymentSettlement[] settled = await Task.WhenAll(
                Task.Run(() => settlement.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct), ct),
                Task.Run(() => settlement.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct), ct));
            var applied = Assert.Single(settled, result => result.Outcome == PaymentSettlementOutcome.Settled);
            var replay = Assert.Single(settled, result => result.Outcome == PaymentSettlementOutcome.Replayed);
            Assert.NotNull(applied.Credit);
            Assert.Equal(applied.Credit, replay.Credit);
            var final = await sessions.RunAsync(false, (_, tx, token) => SqliteWallet.ReadAsync(tx, member, token), ct);
            Assert.Equal(17.25m, final.Balance.Amount);
            Assert.Equal(3, final.Version);

            Task<CheckoutResult> Purchase(LocalCheckoutRequest request) => store.PurchaseLocalAsync(brand, 0, CheckoutFixture.Actor(0), request, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
