using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePreparedCheckoutTests
{
    [Fact]
    public async Task FrozenCryptoCheckoutSettlesAndAllocatesOneEncryptedLocalItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-prepared-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), stock = Guid.NewGuid(), order = Guid.NewGuid();
            byte[] encrypted = new InventoryCipher(secrets).Encrypt(stock, Encoding.UTF8.GetBytes("synthetic-stock"));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active,allow_crypto)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic goods');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,allow_crypto)
                    VALUES ($brand,$product,1,'10.25',1);
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$content,'current',$fingerprint,'local_import');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$stock", stock), ("$content", encrypted),
                    ("$fingerprint", SHA256.HashData(stock.ToByteArray())));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);

            var catalog = new SqliteCatalogStore(sessions, secrets);
            var query = new CatalogQuery(product, 1, CheckoutPayment.Crypto);
            PreparedCheckout prepared = await catalog.PrepareAsync(brand, 7001, query, ct);
            var checkout = new SqlitePreparedCheckoutStore(sessions, secrets);
            var request = new CheckoutRequest(order, product, 1, CheckoutPayment.Crypto,
                CheckoutKind.ProductPurchase, CryptoAsset.UsdtTrc20);
            Assert.Equal(CheckoutOutcome.AwaitingPayment,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            Assert.Equal(CheckoutOutcome.Replayed,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                checkout.PurchaseAsync(brand, 0, 7001, request with { Quantity = 2 }, prepared, catalog, ct));
            Guid payment = await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var inspect = SqliteWallet.Command(tx, """
                    SELECT p.id,p.status,p.amount_due,p.network_asset,o.payment_status,
                        (SELECT count(*) FROM jobs WHERE job_kind='expire_order_payment' AND order_id=$order),
                        (SELECT count(*) FROM jobs WHERE job_kind='send_telegram' AND order_id=$order),
                        (SELECT count(*) FROM wallet_entries),
                        (SELECT count(*) FROM inventory_items WHERE id=$stock AND state='available')
                    FROM payments p JOIN orders o ON o.id=p.order_id WHERE o.id=$order
                    """, ("$order", order), ("$stock", stock));
                await using var row = await inspect.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("created", row.GetString(1));
                Assert.Equal("10.25", row.GetString(2));
                Assert.Equal("USDTTRC20", row.GetString(3));
                Assert.Equal("awaiting_payment", row.GetString(4));
                Assert.Equal(1, row.GetInt64(5));
                Assert.Equal(1, row.GetInt64(6));
                Assert.Equal(0, row.GetInt64(7));
                Assert.Equal(1, row.GetInt64(8));
                return row.GetGuid(0);
            }, ct);

            await new SqlitePaymentEvidenceStore(sessions).BindInvoiceAsync(brand, 0, 7001, payment, "123456789", 10.4m, ct);
            using var notice = PaymentNoticeFixture.Notice(origin: "order:" + order.ToString("D"));
            var evidence = new SqlitePaymentCaptureStore(sessions, secrets);
            Assert.Equal(PaymentCaptureOutcome.Ready, (await evidence.CaptureAsync(brand, 0, notice,
                (proof, _) => Task.FromResult("protected:synthetic/" + proof.Digest), ct)).Outcome);
            Assert.Equal(PaymentSettlementOutcome.Settled,
                (await new SqliteOrderPaymentSettlementStore(sessions, secrets)
                    .SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct)).Outcome);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var inspect = SqliteWallet.Command(tx, """
                    SELECT (SELECT count(*) FROM fulfillments WHERE order_id=$order AND status='acquired'),
                        (SELECT count(*) FROM inventory_items WHERE id=$stock AND state='allocated'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest' AND order_id=$order),
                        (SELECT count(*) FROM wallet_entries),
                        (SELECT payment_status FROM orders WHERE id=$order)
                    """, ("$order", order), ("$stock", stock));
                await using var row = await inspect.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(1, row.GetInt64(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(0, row.GetInt64(3));
                Assert.Equal("paid", row.GetString(4));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
