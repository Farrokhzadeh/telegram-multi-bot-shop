using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteCatalogTests
{
    [Fact]
    public async Task PreparedLocalQuoteIsFencedToPriceRevisionStockAndActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-catalog-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await PlainTargetRuntimeTests.InitializeSqliteAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            var brand = CheckoutFixture.ResolveContexts()[0];
            var other = CheckoutFixture.ResolveContexts()[1];
            Guid member = Guid.NewGuid(), otherMember = Guid.NewGuid(), product = Guid.NewGuid();
            Guid stock = Guid.NewGuid(), admin = Guid.NewGuid(), audit = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("public fixture");
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,active)
                    VALUES ($brand,'local','Local','Local','international','en','local_bot',1001,x'01',1);
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,active)
                    VALUES ($other,'other','Other','Other','international','en','other_bot',1002,x'01',1);
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($member,$brand,7001,'en');
                    INSERT INTO users(id,brand_id,telegram_id,language) VALUES ($otherMember,$other,7001,'en');
                    INSERT INTO admins(id,login,authentication_mode,external_subject)
                    VALUES ($admin,'fixture','external','fixture');
                    INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                    VALUES ($audit,'admin',$admin,$brand,'wallet_adjustment','wallet',$member,'fixture',$audit);
                    INSERT INTO wallets(user_id,balance,version) VALUES ($member,'20',1);
                    INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,
                        idempotency_key,admin_id,audit_event_id,reason)
                    VALUES ($audit,$member,'20','20',1,'admin_adjustment',1,'funding',$admin,$audit,'fixture');
                    INSERT INTO products(id,fulfillment_kind,availability_policy,title) VALUES ($product,'local','immediate','Fixture');
                    INSERT INTO brand_products(brand_id,product_id,visible,price_usdt)
                    VALUES ($brand,$product,1,'10');
                    INSERT INTO inventory_items(id,product_id,credential_content,source_fingerprint,origin_kind)
                    VALUES ($stock,$product,$content,$fingerprint,'local_import');
                    """, ("$brand", brand.BrandId), ("$other", other.BrandId), ("$member", member),
                    ("$otherMember", otherMember), ("$admin", admin), ("$audit", audit),
                    ("$product", product), ("$stock", stock), ("$content", content),
                    ("$fingerprint", SHA256.HashData(stock.ToByteArray())));
                await command.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            var store = new SqliteCatalogStore(sessions);
            var item = await store.GetAsync(brand, 7001, product, ct);
            Assert.NotNull(item);
            Assert.Equal(CatalogAvailability.Available, item.Availability);
            Assert.Equal(10m, item.UnitUsdt);
            Assert.Equal(1L, item.LocalStock);
            Assert.Null(await store.GetAsync(other, 7001, product, ct));
            var stale = await store.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 1, CheckoutPayment.Wallet), ct);
            Assert.Equal(10m, stale.Quote.TotalUsd.Amount);
            await sessions.RunAsync(true, async (_, tx, cancel) =>
            {
                using var command = SqliteWallet.Command(tx, """
                    UPDATE brand_products SET price_usdt='11',revision=revision+1 WHERE brand_id=$brand AND product_id=$product
                    """, ("$brand", brand.BrandId), ("$product", product));
                await command.ExecuteNonQueryAsync(cancel);
                return true;
            }, ct);
            await Assert.ThrowsAsync<PricingRejectedException>(() =>
                store.RequireLocalAsync(brand, 7001, stale, Guid.NewGuid(), ct));
            var prepared = await store.PrepareAsync(brand, 7001,
                new CatalogQuery(product, 1, CheckoutPayment.Wallet), ct);
            var request = await store.RequireLocalAsync(brand, 7001, prepared, Guid.NewGuid(), ct);
            Assert.Equal(11m, request.UnitPrice);
            await Assert.ThrowsAsync<PricingRejectedException>(() =>
                store.RequireLocalAsync(brand, 7001, prepared, Guid.NewGuid(), ct));
            var checkout = new SqliteCheckoutStore(SqliteSessionFactory.ForPlaintext(path));
            Assert.Equal(CheckoutOutcome.Purchased,
                (await checkout.PurchaseLocalAsync(brand, request.PolicyRevision, 7001, request, ct)).Outcome);
            var result = await store.GetAsync(brand, 7001, product, ct);
            Assert.Equal(CatalogAvailability.OutOfStock, result!.Availability);
        }
        finally { directory.Delete(recursive: true); }
    }
}
