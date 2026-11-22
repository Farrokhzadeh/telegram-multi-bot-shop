using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteWalletCouponTests
{
    [Fact]
    public async Task DiscountedWalletCheckoutDebitsNetAmountAllocatesOnceAndRedeemsOnManifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-wallet-coupon-");
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
            Guid member = Guid.NewGuid(), product = Guid.NewGuid(), stock = Guid.NewGuid(),
                coupon = Guid.NewGuid(), revision = Guid.NewGuid(), order = Guid.NewGuid(),
                admin = Guid.NewGuid(), fundingAudit = Guid.NewGuid(), couponAudit = Guid.NewGuid();
            byte[] couponCode = Encoding.UTF8.GetBytes("SAVE20");
            byte[] encryptedCoupon = new BrandCipher(secrets).Encrypt(brand, "discounts",
                coupon, "code_ciphertext", couponCode);
            byte[] encryptedStock = new InventoryCipher(secrets).Encrypt(stock,
                Encoding.UTF8.GetBytes("synthetic discounted credential"));
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,
                        bot_id,bot_token_ciphertext,bot_token_key_id,active,allow_wallet)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1,1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'synthetic-funding','external','synthetic');
                    INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,
                        reason,correlation_id)
                    VALUES ($fundingAudit,'admin',$admin,$brand,'wallet_adjustment','wallet',$member,
                        'fixture',$fundingAudit);
                    INSERT INTO wallets(user_id,balance,version) VALUES ($member,'10.25',1);
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,
                        is_posting,idempotency_key,admin_id,audit_event_id,reason)
                    VALUES ($fundingAudit,$member,'10.25','10.25',1,'admin_adjustment',1,
                        'synthetic-fund',$admin,$fundingAudit,'fixture');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','immediate','Synthetic discounted goods');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt,allow_wallet)
                    VALUES ($brand,$product,1,'10.25',1);
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,
                        source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$credential,'current',$fingerprint,'local_import');
                    INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,
                        reason,correlation_id)
                    VALUES ($couponAudit,'system',$brand,'coupon_issue','discount',$coupon,
                        'synthetic',$couponAudit);
                    INSERT INTO discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,
                        issuance_batch_id,kind,percent_off,product_id,max_total_uses,max_uses_per_user)
                    VALUES ($coupon,$brand,$digest,$encrypted,$key,'SAVE',$coupon,
                        'promotion','20',$product,10,10);
                    INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                        max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count_at_revision,
                        held_units_at_revision,active,product_id)
                    VALUES ($revision,$coupon,0,$couponAudit,'20',10,10,0,0,0,1,$product);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$member", member),
                    ("$admin", admin), ("$fundingAudit", fundingAudit), ("$couponAudit", couponAudit),
                    ("$product", product), ("$stock", stock), ("$credential", encryptedStock),
                    ("$fingerprint", SHA256.HashData(stock.ToByteArray())), ("$coupon", coupon),
                    ("$digest", SHA256.HashData(couponCode)), ("$encrypted", encryptedCoupon),
                    ("$key", secrets.CurrentBrandKeyId(brand)), ("$revision", revision));
                await setup.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var catalog = new SqliteCatalogStore(sessions, secrets);
            PreparedCheckout prepared = await catalog.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 1, CheckoutPayment.Wallet), ct);
            Assert.Equal(coupon, prepared.Quote.Terms.DiscountId);
            Assert.Equal(8.2m, prepared.Quote.TotalUsd.Amount);
            var checkout = new SqlitePreparedCheckoutStore(sessions, secrets);
            var request = new CheckoutRequest(order, product, 1);
            Assert.Equal(CheckoutOutcome.Purchased,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            Assert.Equal(CheckoutOutcome.Replayed,
                (await checkout.PurchaseAsync(brand, 0, 7001, request, prepared, catalog, ct)).Outcome);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT w.balance,w.version,c.status,d.redeemed_count,
                        (SELECT count(*) FROM wallet_entries WHERE order_id=$order AND movement_kind='purchase_debit'),
                        (SELECT count(*) FROM fulfillments WHERE order_id=$order AND status='acquired'),
                        (SELECT count(*) FROM jobs WHERE order_id=$order AND job_kind='prepare_manifest'),
                        (SELECT count(*) FROM inventory_items WHERE id=$stock AND state='allocated')
                    FROM wallets w JOIN orders o ON o.user_id=w.user_id
                        JOIN discount_uses c ON c.order_id=o.id JOIN discounts d ON d.id=c.discount_id
                    WHERE o.id=$order
                    """, ("$order", order), ("$stock", stock));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("2.05", row.GetString(0));
                Assert.Equal(2, row.GetInt64(1));
                Assert.Equal("held", row.GetString(2));
                Assert.Equal(0, row.GetInt64(3));
                Assert.Equal(1, row.GetInt64(4));
                Assert.Equal(1, row.GetInt64(5));
                Assert.Equal(1, row.GetInt64(6));
                Assert.Equal(1, row.GetInt64(7));
                return 0;
            }, ct);
            JobLease? lease = await sessions.RunAsync(true, (_, tx, token) =>
                OutboxStore.ClaimSqliteAsync(tx, brand.BrandId, JobKind.PrepareManifest, Guid.NewGuid(),
                    TimeSpan.FromMinutes(1), token), ct);
            Assert.NotNull(lease);
            Assert.Null(await new SqliteDeliveryStore(sessions, secrets).PrepareAsync(brand, lease, ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT c.status,d.redeemed_count,o.release_authorized_at IS NOT NULL,
                        (SELECT count(*) FROM wallet_entries WHERE order_id=$order AND movement_kind='purchase_debit')
                    FROM discount_uses c JOIN discounts d ON d.id=c.discount_id
                        JOIN orders o ON o.id=c.order_id WHERE o.id=$order
                    """, ("$order", order));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("redeemed", row.GetString(0));
                Assert.Equal(1, row.GetInt64(1));
                Assert.True(row.GetBoolean(2));
                Assert.Equal(1, row.GetInt64(3));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
