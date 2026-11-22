using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteDiscountClaimTests
{
    [Fact]
    public async Task UnpaidExpiryReleasesTheOriginalCouponWithoutRedemptionOrWalletMovement()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-coupon-expiry-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), order = Guid.NewGuid(),
                payment = Guid.NewGuid(), coupon = Guid.NewGuid(), audit = Guid.NewGuid(),
                revision = Guid.NewGuid();
            DateTime created = DateTime.UtcNow.AddMinutes(-40);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'manual','immediate','Synthetic goods');
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,
                        payment_method,unit_price_usdt,auto_price_toman_snapshot,pricing_rounding_step_snapshot,
                        price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                        payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,
                        discount_pct_snapshot,discount_units_snapshot,discount_revision_id,
                        created_at,updated_at,payment_due_at)
                    VALUES ($order,$member,$product,1,'product_purchase','awaiting_payment','USDT','crypto',
                        '10.25',false,'5000','synthetic','Stage','Synthetic goods','synthetic',
                        'manual',false,false,'20',1,$revision,$created,$created,$due);
                    INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,
                        reason,correlation_id)
                    VALUES ($audit,'system',$brand,'coupon_issue','discount',$coupon,'synthetic',$audit);
                    INSERT INTO discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,
                        issuance_batch_id,kind,percent_off,product_id,max_total_uses)
                    VALUES ($coupon,$brand,zeroblob(32),x'01','fixture','SAVE',$batch,'promotion',
                        '20',$product,10);
                    INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                        max_total_uses,usage_adjustment_units,redeemed_count_at_revision,
                        held_units_at_revision,active,product_id)
                    VALUES ($revision,$coupon,0,$audit,'20',10,0,0,0,1,$product);
                    INSERT INTO discount_uses(discount_id,order_id,discount_revision_id,
                        units_covered,saved_amount,saved_currency,status)
                    VALUES ($coupon,$order,$revision,1,'2.05','USD','held');
                    INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,
                        idempotency_ref,network_asset,status)
                    VALUES ($payment,$order,'crypto','USD','8.2',$origin,'USDTTRC20','created');
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$order", order), ("$payment", payment),
                    ("$coupon", coupon), ("$batch", Guid.NewGuid()), ("$revision", revision),
                    ("$audit", audit), ("$created", SqliteSchema.UtcText(created)),
                    ("$due", SqliteSchema.UtcText(created.AddMinutes(30))),
                    ("$origin", "order:" + order.ToString("D")));
                await setup.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx,
                    JobIntent.Order(JobKind.ExpireOrderPayment, brand.BrandId, order),
                    order, DateTime.UtcNow.AddMinutes(-1), token);
                return 0;
            }, ct);
            var expiry = new SqlitePaymentExpiryStore(sessions);
            JobLease? lease = await expiry.ClaimAsync(brand, Guid.NewGuid(), ct);
            Assert.NotNull(lease);
            Assert.Equal(SqlitePaymentExpiryOutcome.Expired,
                await expiry.RunAsync(brand, lease, ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT c.status,d.redeemed_count,o.payment_status,
                        (SELECT count(*) FROM wallet_entries),
                        (SELECT count(*) FROM jobs WHERE job_kind='expire_order_payment'
                            AND status='succeeded')
                    FROM discount_uses c JOIN discounts d ON d.id=c.discount_id
                    JOIN orders o ON o.id=c.order_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("released", row.GetString(0));
                Assert.Equal(0, row.GetInt64(1));
                Assert.Equal("expired", row.GetString(2));
                Assert.Equal(0, row.GetInt64(3));
                Assert.Equal(1, row.GetInt64(4));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task FrozenCryptoCouponRedeemsOnlyWhenPaidManifestIsAuthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-coupon-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var secrets = SecretFiles.Load(files.Root, "service-config.json",
                files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), stock = Guid.NewGuid();
            Guid coupon = Guid.NewGuid(), batch = Guid.NewGuid(), audit = Guid.NewGuid(),
                couponRevision = Guid.NewGuid(), order = Guid.NewGuid();
            var cipher = new BrandCipher(secrets);
            byte[] code = Encoding.UTF8.GetBytes("SAVE20");
            byte[] encrypted = cipher.Encrypt(brand, "discounts", coupon, "code_ciphertext", code);
            byte[] credential = new InventoryCipher(secrets).Encrypt(stock,
                Encoding.UTF8.GetBytes("public synthetic coupon credential"));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active,allow_crypto)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic goods');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,allow_crypto)
                    VALUES ($brand,$product,1,'10.25',1);
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,
                        source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$credential,'current',$fingerprint,'local_import');
                    INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                    VALUES ($audit,'system',$brand,'coupon_issue','discount',$coupon,'synthetic',$audit);
                    INSERT INTO discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,
                        issuance_batch_id,kind,percent_off,product_id,max_total_uses,max_uses_per_user)
                    VALUES ($coupon,$brand,$digest,$encrypted,$key,'SAVE',$batch,'promotion','20',$product,10,10);
                    INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                        max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count_at_revision,
                        held_units_at_revision,active,product_id)
                    VALUES ($revision,$coupon,0,$audit,'20',10,10,0,0,0,1,$product);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$product", product), ("$stock", stock), ("$credential", credential),
                    ("$fingerprint", SHA256.HashData(stock.ToByteArray())),
                    ("$audit", audit), ("$coupon", coupon), ("$digest", SHA256.HashData(code)),
                    ("$encrypted", encrypted), ("$key", secrets.CurrentBrandKeyId(brand)),
                    ("$batch", batch), ("$revision", couponRevision));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var catalog = new SqliteCatalogStore(sessions, secrets);
            PreparedCheckout prepared = await catalog.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 1, CheckoutPayment.Crypto), ct);
            Assert.Equal(coupon, prepared.Quote.Terms.DiscountId);
            Assert.Equal(couponRevision, prepared.Quote.Terms.DiscountRevisionId);
            Assert.Equal(20m, prepared.Quote.Terms.DiscountPercent);
            Assert.Equal(8.2m, prepared.Quote.TotalUsd.Amount);
            var request = new CheckoutRequest(order, product, 1, CheckoutPayment.Crypto,
                CheckoutKind.ProductPurchase, CryptoAsset.UsdtTrc20);
            Assert.Equal(CheckoutOutcome.AwaitingPayment,
                (await new SqlitePreparedCheckoutStore(sessions, secrets)
                    .PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            Guid payment = await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT p.id,p.amount_due,c.status,c.saved_amount,c.saved_currency,c.discount_revision_id,
                        (SELECT redeemed_count FROM discounts WHERE id=$coupon),
                        (SELECT count(*) FROM wallet_entries)
                    FROM payments p JOIN discount_uses c ON c.order_id=p.order_id
                    WHERE p.order_id=$order
                    """, ("$order", order), ("$coupon", coupon));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("8.2", row.GetString(1));
                Assert.Equal("held", row.GetString(2));
                Assert.Equal("2.05", row.GetString(3));
                Assert.Equal("USD", row.GetString(4));
                Assert.Equal(couponRevision, row.GetGuid(5));
                Assert.Equal(0, row.GetInt64(6));
                Assert.Equal(0, row.GetInt64(7));
                return row.GetGuid(0);
            }, ct);
            await new SqlitePaymentEvidenceStore(sessions).BindInvoiceAsync(brand, 0, 7001,
                payment, "123456789", 8.2m, ct);
            using var notice = PaymentNoticeFixture.Notice(origin: "order:" + order.ToString("D"),
                quote: 8.2m, expected: 8.2m, actual: 8.2m);
            var capture = new SqlitePaymentCaptureStore(sessions, secrets);
            Assert.Equal(PaymentCaptureOutcome.Ready, (await capture.CaptureAsync(brand, 0, notice,
                (proof, _) => Task.FromResult("protected:synthetic/" + proof.Digest), ct)).Outcome);
            Assert.Equal(PaymentSettlementOutcome.Settled,
                (await new SqliteOrderPaymentSettlementStore(sessions, secrets)
                    .SettleAsync(brand, 0, payment, PaymentSettlementFixture.Lookup, ct)).Outcome);
            var lease = await sessions.RunAsync(true, (_, tx, token) =>
                OutboxStore.ClaimSqliteAsync(tx, brand.BrandId, JobKind.PrepareManifest, Guid.NewGuid(),
                    TimeSpan.FromMinutes(1), token), ct);
            Assert.NotNull(lease);
            var delivery = new SqliteDeliveryStore(sessions, secrets);
            Assert.Null(await delivery.PrepareAsync(brand, lease, ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT c.status,d.redeemed_count,o.fulfillment_status,
                        o.release_authorized_at IS NOT NULL,
                        (SELECT count(*) FROM wallet_entries),
                        (SELECT count(*) FROM fulfillments WHERE order_id=$order AND status='ready')
                    FROM discount_uses c JOIN discounts d ON d.id=c.discount_id
                        JOIN orders o ON o.id=c.order_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("redeemed", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.Equal("ready", row.GetString(2));
                Assert.True(row.GetBoolean(3));
                Assert.Equal(0, row.GetInt64(4));
                Assert.Equal(1, row.GetInt64(5));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
