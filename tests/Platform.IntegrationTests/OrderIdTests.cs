using System.Collections.Concurrent;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class OrderIdTests
{
    [Fact]
    public void ConcurrentPublicReferencesAreUniqueVersionSevenUuids()
    {
        var ids = new ConcurrentDictionary<Guid, byte>();
        Parallel.For(0, 100000, _ =>
        {
            Guid id = OrderIds.New();
            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(7, id.Version);
            Assert.True(ids.TryAdd(id, 0));
        });
        Assert.Equal(100000, ids.Count);
    }

    [Fact]
    public async Task ExistingHistoricalUuidCannotBeReusedToCreateDebitOrClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: ct);
        Guid historical = OrderIds.New();
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,
                    auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,
                    payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,payment_due_at)
                VALUES ($1,$2,$3,1,'product_purchase','awaiting_payment','USDT','crypto',10,false,5000,'public historical fixture','public brand','public title',
                    'public terms','local',true,false,CURRENT_TIMESTAMP+INTERVAL '30 minutes')
                """, DbValue.Uuid(historical), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product)), ct);
            await seed.CommitAsync(ct);
        }
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Collision must not refresh."));
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(historical, f.Product, 1),
            (_, _, _) => throw new InvalidOperationException("Collision must not fetch channels."), ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", ct));
    }
}
