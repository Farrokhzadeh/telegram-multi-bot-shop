using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteOrderPaymentSettlementTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OriginalVerifiedPaymentSettlesOnceOrCreditsShortage(bool stocked)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-payment-order-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            var alternate = CheckoutFixture.ResolveContexts()[1];
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), stock = Guid.NewGuid();
            Guid order = Guid.NewGuid(), payment = Guid.NewGuid();
            string created = SqliteSchema.UtcText(DateTime.UtcNow);
            string deadline = SqliteSchema.UtcText(DateTime.Parse(created, null, System.Globalization.DateTimeStyles.AdjustToUniversal).AddMinutes(30));
            byte[] encrypted = new InventoryCipher(secrets).Encrypt(stock, Encoding.UTF8.GetBytes("public synthetic delivered item"));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active,allow_crypto)
                    VALUES ($brand,'fixture','Fixture','Fixture','international','en','fixture_bot',1001,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,allow_crypto) VALUES ($brand,$product,1,'10',1);
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,
                        unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                        brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,
                        local_first_snapshot,is_preorder,payment_due_at,created_at,updated_at)
                    VALUES ($order,$member,$product,1,'product_purchase','confirmation_pending','USDT','crypto',
                        '10',false,'5000','synthetic','Fixture','Fixture','synthetic terms','local',true,false,$deadline,$now,$now);
                    INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,amount_received,provider_code,provider_payment_ref,
                        idempotency_ref,network_asset,expected_network_amount,network_amount,provider_status_code,status,ipn_key_id,ipn_signature,
                        ipn_payload_sha256,ipn_verified_at,first_payment_observed_at,confirmed_at,evidence_asset_ref,evidence_digest)
                    VALUES ($payment,$order,'crypto','USD','10','10','nowpayments','123456','order:'||$order,'USDTBSC','10','10',
                        'finished','confirming','fixture',zeroblob(64),zeroblob(32),$now,$now,$now,'public-fixture',zeroblob(32));
                    """, ("$brand", brand.BrandId), ("$member", member), ("$product", product), ("$order", order),
                    ("$payment", payment), ("$deadline", deadline), ("$now", created));
                await setup.ExecuteNonQueryAsync(token);
                if (stocked)
                {
                    using var inventory = SqliteWallet.Command(tx, """
                        INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind)
                        VALUES ($stock,$product,$cipher,'current',$fingerprint,'local_import')
                        """, ("$stock", stock), ("$product", product), ("$cipher", encrypted), ("$fingerprint", SHA256.HashData(stock.ToByteArray())));
                    await inventory.ExecuteNonQueryAsync(token);
                }
                return 0;
            }, ct);

            var settle = new SqliteOrderPaymentSettlementStore(new SqliteSessionFactory(path), secrets);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                settle.SettleAsync(alternate, 0, payment, PaymentSettlementFixture.Lookup, ct));
            var accepted = await settle.SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct);
            Assert.Equal(PaymentSettlementOutcome.Settled, accepted.Outcome);
            Assert.Equal(stocked ? ExternalSettlementRoute.LocalGoods : ExternalSettlementRoute.LocalShortageCredit, accepted.Route);
            Assert.Equal(!stocked, accepted.Credit.HasValue);
            var duplicate = await settle.SettleAsync(brand, 0, payment,
                (_, _) => throw new InvalidOperationException("The supplier must not be called for replay."), ct);
            Assert.Equal(PaymentSettlementOutcome.Replayed, duplicate.Outcome);
            Assert.Equal(accepted.Credit, duplicate.Credit);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var inspect = SqliteWallet.Command(tx, """
                    SELECT (SELECT count(*) FROM wallet_entries),(SELECT count(*) FROM fulfillments),
                        (SELECT count(*) FROM telegram_deliveries WHERE purpose='order_status'),
                        (SELECT count(*) FROM jobs WHERE job_kind='send_telegram'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest'),
                        (SELECT payment_status FROM orders WHERE id=$order),
                        (SELECT fulfillment_status FROM orders WHERE id=$order)
                    """, ("$order", order));
                await using var row = await inspect.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(stocked ? 0 : 1, row.GetInt64(0));
                Assert.Equal(stocked ? 1 : 0, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                Assert.Equal(stocked ? 1 : 0, row.GetInt64(4));
                Assert.Equal("paid", row.GetString(5));
                Assert.Equal(stocked ? "acquiring" : "compensated", row.GetString(6));
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
