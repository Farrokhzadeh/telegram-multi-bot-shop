using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteLocalPreorderTests
{
    [Fact]
    public async Task SharedStockIsAllocatedToOldestPaidPreorderWithDurableFencedJobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("akanti-sqlite-preorder-");
        try
        {
            string path = Path.Combine(dir.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            var sessions = new SqliteSessionFactory(path);
            using var files = new SyntheticSecretFiles();
            files.WriteManifest(brands: CheckoutFixture.BrandIds);
            using var keys = SecretFiles.Load(files.Root, "service-config.json", files.Grant(brands: CheckoutFixture.BrandIds));
            var brand = CheckoutFixture.ResolveContexts()[0];
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(), product = Guid.NewGuid();
            Guid firstMember = Guid.NewGuid(), secondMember = Guid.NewGuid(), stock = Guid.NewGuid();
            string oldest = SqliteSchema.UtcText(DateTime.UtcNow.AddMinutes(-10));
            string newest = SqliteSchema.UtcText(DateTime.UtcNow.AddMinutes(-5));
            var cipher = new InventoryCipher(keys);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var setup = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token_ciphertext,bot_token_key_id,active)
                    VALUES ($brand,'stage','Stage','Stage','international','en','stage_bot',$bot,x'01','fixture',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($firstMember,$brand,7001,'en');
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($secondMember,$brand,7002,'en');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title)
                    VALUES ($product,'local','local_preorder','Synthetic preorder');
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,
                        source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$encrypted,'current',$hash,'local_import');
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,
                        pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                        product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,
                        is_preorder,payment_confirmed_at)
                    VALUES ($first,$firstMember,$product,1,'product_purchase','paid','waiting_stock','wallet',
                        'USDT','10.25',false,'5000','synthetic','Stage','Synthetic preorder','wallet','local',true,true,$oldest);
                    INSERT INTO orders(id,user_id,product_id,quantity,order_kind,payment_status,fulfillment_status,
                        payment_method,currency,unit_price_usdt,auto_price_toman_snapshot,
                        pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,
                        product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,
                        is_preorder,payment_confirmed_at)
                    VALUES ($second,$secondMember,$product,1,'product_purchase','paid','waiting_stock','wallet',
                        'USDT','10.25',false,'5000','synthetic','Stage','Synthetic preorder','wallet','local',true,true,$newest);
                    """, ("$brand", brand.BrandId), ("$bot", brand.BotId), ("$firstMember", firstMember),
                    ("$secondMember", secondMember), ("$product", product), ("$stock", stock),
                    ("$encrypted", cipher.Encrypt(stock, Encoding.UTF8.GetBytes("synthetic first"))),
                    ("$hash", SHA256.HashData(stock.ToByteArray())), ("$first", first), ("$second", second),
                    ("$oldest", oldest), ("$newest", newest));
                await setup.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Product(JobKind.DrainLocalPreorders, product),
                    Guid.NewGuid(), DateTime.UtcNow, token);
                return 0;
            }, ct);
            var preorders = new SqliteLocalPreorderStore(sessions, keys);
            JobLease? firstLease = await preorders.ClaimAsync(Guid.NewGuid(), ct);
            Assert.NotNull(firstLease);
            Assert.Equal(first, await preorders.DrainAsync(firstLease, [brand], ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT (SELECT fulfillment_status FROM orders WHERE id=$first),
                        (SELECT fulfillment_status FROM orders WHERE id=$second),
                        (SELECT count(*) FROM inventory_items WHERE state='allocated'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest')
                    """, ("$first", first), ("$second", second));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal("acquiring", row.GetString(0));
                Assert.Equal("waiting_stock", row.GetString(1));
                Assert.Equal(1, row.GetInt64(2));
                Assert.Equal(1, row.GetInt64(3));
                return 0;
            }, ct);
            Guid laterStock = Guid.NewGuid();
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var insert = SqliteWallet.Command(tx, """
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,
                        source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$encrypted,'current',$hash,'local_import')
                    """, ("$stock", laterStock), ("$product", product),
                    ("$encrypted", cipher.Encrypt(laterStock, Encoding.UTF8.GetBytes("synthetic second"))),
                    ("$hash", SHA256.HashData(laterStock.ToByteArray())));
                await insert.ExecuteNonQueryAsync(token);
                return 0;
            }, ct);
            var reopened = new SqliteLocalPreorderStore(new SqliteSessionFactory(path), keys);
            JobLease? secondLease = await reopened.ClaimAsync(Guid.NewGuid(), ct);
            Assert.NotNull(secondLease);
            Assert.Equal(second, await reopened.DrainAsync(secondLease, [brand], ct));
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT (SELECT count(*) FROM orders WHERE id IN ($first,$second) AND fulfillment_status='acquiring'),
                        (SELECT count(*) FROM fulfillments WHERE order_id IN ($first,$second) AND status='acquired'),
                        (SELECT count(*) FROM jobs WHERE job_kind='prepare_manifest'),
                        (SELECT count(*) FROM inventory_items WHERE state='allocated')
                    """, ("$first", first), ("$second", second));
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(2, row.GetInt64(0));
                Assert.Equal(2, row.GetInt64(1));
                Assert.Equal(2, row.GetInt64(2));
                Assert.Equal(2, row.GetInt64(3));
                return 0;
            }, ct);
        }
        finally { dir.Delete(recursive: true); }
    }
}
