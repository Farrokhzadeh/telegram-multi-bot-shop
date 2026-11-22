using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqlitePrepaidSourceTests
{
    [Fact]
    public async Task VerifiedPaidPrepaidOrderIssuesOneEncryptedEntitlementAfterReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-prepaid-");
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
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), source = Guid.NewGuid(),
                payment = Guid.NewGuid();
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic prepaid');
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,
                        fulfillment_status,currency,payment_method,unit_price_usdt,
                        auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,
                        brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,
                        fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_confirmed_at)
                    VALUES ($order,$member,$product,5,'prepaid_units','paid','acquiring','USDT','crypto',
                        '10',false,$step,$version,'Stage','Synthetic prepaid','crypto','local',true,false,$now);
                    INSERT INTO payments(id,order_id,payment_method,quote_currency,amount_due,amount_received,
                        provider_code,provider_payment_ref,idempotency_ref,network_asset,status,confirmed_at)
                    VALUES ($payment,$order,'crypto','USD','50','50','nowpayments','123456789',
                        $origin,'USDTTRC20','settled',$now);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId),
                    ("$member", member), ("$product", product), ("$order", source),
                    ("$payment", payment), ("$origin", "order:" + source.ToString("D")), ("$now", now),
                    ("$step", ExactDecimal.Format(PricingPolicy.TomanStep)),
                    ("$version", PricingPolicy.Version));
                await sql.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var store = new SqlitePrepaidSourceStore(sessions, secrets);
            Guid issued = await store.IssueAsync(brand, source, ct);
            Assert.Equal(issued, await new SqlitePrepaidSourceStore(new SqliteSessionFactory(path), secrets)
                .IssueAsync(brand, source, ct));
            await Assert.ThrowsAsync<PricingRejectedException>(() =>
                store.IssueAsync(CheckoutFixture.ResolveContexts()[1], source, ct));
            await Assert.ThrowsAsync<PricingRejectedException>(() =>
                store.IssueAsync(brand, Guid.NewGuid(), ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var sql = SqliteWallet.Command(tx, """
                    SELECT d.code_ciphertext,d.code_key_id,d.code_digest,d.eligible_user_id,
                        d.source_order_id,d.max_total_uses,d.percent_off,o.fulfillment_status,
                        (SELECT count(*) FROM discount_revisions WHERE discount_id=$coupon),
                        (SELECT count(*) FROM jobs WHERE job_kind='coupon_notice' AND discount_id=$coupon),
                        (SELECT count(*) FROM wallet_entries)
                    FROM discounts d JOIN orders o ON o.id=d.source_order_id WHERE d.id=$coupon
                    """, ("$coupon", issued));
                await using var row = await sql.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(member, row.GetGuid(3));
                Assert.Equal(source, row.GetGuid(4));
                Assert.Equal(5, row.GetInt64(5));
                Assert.Equal("100", row.GetString(6));
                Assert.Equal("completed", row.GetString(7));
                Assert.Equal(1, row.GetInt64(8));
                Assert.Equal(1, row.GetInt64(9));
                Assert.Equal(0, row.GetInt64(10));
                byte[] plain = new BrandCipher(secrets).Decrypt(brand, "discounts", issued,
                    "code_ciphertext", row.GetString(1), row.GetFieldValue<byte[]>(0));
                try
                {
                    string code = Encoding.UTF8.GetString(plain);
                    Assert.Equal(48, code.Length);
                    byte[] payload = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + brand.BrandId.ToString("D") +
                        ":" + code);
                    try
                    {
                        byte[] digest = secrets.UseBrandKey(brand, row.GetString(1),
                            key => HMACSHA256.HashData(key, payload));
                        Assert.True(CryptographicOperations.FixedTimeEquals(digest, row.GetFieldValue<byte[]>(2)));
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WalletPrepaidCheckoutDebitsOnceAndIssuesOriginalSourceEntitlement(bool unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-prepaid-wallet-");
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
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), order = Guid.NewGuid(),
                admin = Guid.NewGuid(), audit = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active,allow_wallet)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'wallet-admin','external','synthetic');
                    INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,
                        reason,correlation_id)
                    VALUES ($audit,'admin',$admin,$brand,'wallet_adjustment','wallet',$member,'fixture',$audit);
                    INSERT INTO wallets(user_id,balance,version) VALUES ($member,'80',1);
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,
                        is_posting,idempotency_key,admin_id,audit_event_id,reason)
                    VALUES ($audit,$member,'80','80',1,'admin_adjustment',1,'synthetic-fund',$admin,$audit,'fixture');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic prepaid units');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,allow_wallet)
                    VALUES ($brand,$product,1,'7.5',1);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$admin", admin), ("$audit", audit), ("$product", product));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var catalog = new SqliteCatalogStore(sessions, secrets);
            PreparedCheckout prepared = await catalog.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 3, CheckoutPayment.Wallet, CheckoutKind.PrepaidUnits), ct);
            var checkout = new SqlitePreparedCheckoutStore(sessions, secrets);
            var request = new CheckoutRequest(order, product, 3, CheckoutPayment.Wallet, CheckoutKind.PrepaidUnits);
            Assert.Equal(CheckoutOutcome.Purchased,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            Assert.Equal(CheckoutOutcome.Replayed,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                checkout.PurchaseAsync(CheckoutFixture.ResolveContexts()[1], 0, 7001, request, prepared, catalog, ct));
            await new SqliteSessionFactory(path).RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT w.balance,w.version,o.payment_status,o.fulfillment_status,
                        (SELECT count(*) FROM wallet_entries WHERE order_id=$order AND movement_kind='purchase_debit'),
                        (SELECT count(*) FROM discounts WHERE source_order_id=$order AND kind='prepaid_entitlement'),
                        (SELECT count(*) FROM jobs WHERE job_kind='coupon_notice'
                            AND discount_id IN (SELECT id FROM discounts WHERE source_order_id=$order))
                    FROM wallets w JOIN orders o ON o.user_id=w.user_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("57.5", row.GetString(0));
                Assert.Equal(2, row.GetInt64(1));
                Assert.Equal("paid", row.GetString(2));
                Assert.Equal("completed", row.GetString(3));
                Assert.Equal(1, row.GetInt64(4));
                Assert.Equal(1, row.GetInt64(5));
                Assert.Equal(1, row.GetInt64(6));
                return 0;
            }, ct);
            var notices = new SqliteCouponDeliveryStore(sessions, secrets);
            JobLease? pending = await notices.ClaimNoticeAsync(brand, Guid.NewGuid(), ct);
            Assert.NotNull(pending);
            await notices.PrepareAsync(brand, pending, ct);
            Assert.Null(await notices.ClaimNoticeAsync(brand, Guid.NewGuid(), ct));
            JobLease? delivery = await sessions.RunAsync(true, (_, tx, token) =>
                OutboxStore.ClaimSqliteAsync(tx, brand.BrandId, JobKind.SendTelegram, Guid.NewGuid(),
                    TimeSpan.FromMinutes(1), token), ct);
            Assert.NotNull(delivery);
            var dispatch = await notices.BeginAsync(brand, delivery, ct);
            Assert.NotNull(dispatch);
            Assert.Equal(7001, dispatch.ChatId);
            Assert.Contains("Code: ", dispatch.Text);
            Assert.Equal(48, dispatch.Text.Split("Code: ", 2)[1].Length);
            await notices.RecordOutcomeAsync(brand, delivery, unknown
                ? new(DeliveryOutcomeKind.Unknown)
                : new(DeliveryOutcomeKind.Succeeded, 901), ct);
            await new SqliteSessionFactory(path).RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT d.status,j.status,(SELECT count(*) FROM wallet_entries WHERE order_id=$order),
                        (SELECT count(*) FROM discounts WHERE source_order_id=$order)
                    FROM telegram_deliveries d JOIN jobs j ON j.telegram_delivery_id=d.id
                    WHERE d.purpose='discount_notice' AND d.discount_id IN
                        (SELECT id FROM discounts WHERE source_order_id=$order)
                    """, ("$order", order));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(unknown ? "uncertain" : "succeeded", row.GetString(0));
                Assert.Equal(unknown ? "failed" : "succeeded", row.GetString(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                return 0;
            }, ct);
            Assert.Null(await sessions.RunAsync(true, (_, tx, token) =>
                OutboxStore.ClaimSqliteAsync(tx, brand.BrandId, JobKind.SendTelegram, Guid.NewGuid(),
                    TimeSpan.FromSeconds(30), token), ct));
        }
        finally { dir.Delete(recursive: true); }
    }
}
