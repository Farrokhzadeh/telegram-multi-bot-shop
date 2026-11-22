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
}
