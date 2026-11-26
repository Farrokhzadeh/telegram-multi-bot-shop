using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierEligibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(CheckoutPayment.Wallet, SupplierCatalogStatus.Available)]
    [InlineData(CheckoutPayment.Crypto, SupplierCatalogStatus.Available)]
    [InlineData(CheckoutPayment.Bank, SupplierCatalogStatus.Available)]
    [InlineData(CheckoutPayment.Wallet, SupplierCatalogStatus.Ineligible)]
    [InlineData(CheckoutPayment.Crypto, SupplierCatalogStatus.Ineligible)]
    [InlineData(CheckoutPayment.Bank, SupplierCatalogStatus.Ineligible)]
    [InlineData(CheckoutPayment.Wallet, SupplierCatalogStatus.Error)]
    [InlineData(CheckoutPayment.Crypto, SupplierCatalogStatus.Error)]
    [InlineData(CheckoutPayment.Bank, SupplierCatalogStatus.Error)]
    public async Task UnreadyExcludedOrUnknownRequirementsRejectBeforeOrderInvoiceDebitOrProcurement(CheckoutPayment method, SupplierCatalogStatus status)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid source = await SupplierFixture.ConfigureAsync(f, Ct);
        var payments = new PaymentSettlementFixture(f);
        await payments.EnableAsync(Ct);
        await payments.EnableBankAsync(Ct);
        int calls = 0;
        var catalog = new CatalogStore(f.Sessions, [new("synthetic", "public account")], (request, _) =>
        {
            calls++;
            Assert.Equal("public product", request.ProductRef);
            Assert.Null(request.VariantRef);
            Assert.Equal(1, request.Quantity);
            return Task.FromResult(new SupplierCatalogResult(status, 10, 1, "USD", "public quote", false));
        });
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        await Assert.ThrowsAsync<PricingRejectedException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(OrderIds.New(), f.Product, 1, method), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct));
        Assert.Equal(1, calls);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind IN ('submit_supplier','reconcile_supplier','export_supplier','expire_order_payment')", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind<>'admin_adjustment'", Ct));
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(source, await read.QuerySingleAsync(new DbStatement("SELECT active_supplier_source_id FROM shop.products WHERE id=$1", DbValue.Uuid(f.Product)), r => r.GetGuid(0), Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullyCoveredCouponCannotBypassUnknownEligibilityButFullLocalStockNeedsNoProvider(bool local)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: local ? 1 : 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierRecoveryTests.Coupon(f, 100);
        int calls = 0;
        var catalog = new CatalogStore(f.Sessions, [new("synthetic", "public account")], (_, _) =>
        {
            calls++;
            return Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Error, 0, ReadyItem: false));
        });
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        Task<CheckoutResult> Buy() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(OrderIds.New(), f.Product, 1), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        if (local)
        {
            Assert.Equal(CheckoutOutcome.Purchased, (await Buy()).Outcome);
            Assert.Equal(0, calls);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='not_required'", Ct));
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='local' AND status='acquired'", Ct));
        }
        else
        {
            await Assert.ThrowsAsync<PricingRejectedException>(Buy);
            Assert.Equal(1, calls);
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
            Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses", Ct));
        }
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
    }
}
