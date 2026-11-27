using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class CheckoutDispatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CatalogStore Catalog(CheckoutFixture f, Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>>? fetch = null) =>
        new(f.Sessions, [new("synthetic", "public account")], fetch ?? ((_, _) => throw new InvalidOperationException("Unexpected supplier transport.")));
    private static WalletCheckoutService Service(CheckoutFixture f, CatalogStore? catalog = null) => new(f.Sessions, f.Store, catalog ?? Catalog(f));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    private static CheckoutRequest Request(CheckoutFixture f, CheckoutPayment payment = CheckoutPayment.Crypto, int qty = 1, CheckoutKind kind = CheckoutKind.ProductPurchase) => new(OrderIds.New(), f.Product, qty, payment, kind);

    [Theory]
    [InlineData(CryptoAsset.UsdtTrc20, "USDTTRC20")]
    [InlineData(CryptoAsset.UsdtBsc, "USDTBSC")]
    public async Task UnpaidCryptoCreatesFrozenOriginAndExpiryWithoutDebitingOrReserving(CryptoAsset network, string code)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        await Crypto(f);
        await Coupon(f, 50, 3);
        var request = Request(f, qty: 3) with { Network = network };
        var service = Service(f);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, (_, _, _) => throw new InvalidOperationException(), Ct)).Outcome);
        await using var read = await f.Sessions.BeginAsync(Ct);
        var payment = await read.QuerySingleAsync(new DbStatement("SELECT network_asset,amount_due::text,quote_currency,status,provider_payment_ref FROM shop.payments WHERE order_id=$1", DbValue.Uuid(request.OrderId)),
            row => (Network: row.GetString(0), Due: ExactDecimal.Parse(row.GetString(1)), Currency: row.GetString(2), Status: row.GetString(3), NoRemote: row.IsDBNull(4)), Ct);
        Assert.Equal((code, 15m, "USD", "created", true), payment);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=3", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs j JOIN shop.orders o ON o.id=j.order_id WHERE j.job_kind='expire_order_payment' AND j.available_at=o.payment_due_at AND o.payment_due_at=o.created_at+INTERVAL '30 minutes'", Ct));
    }

    [Fact]
    public async Task ReplacingTheSameCouponRestoresItsOwnHeldUnitsWithoutExtendingExpiry()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 5, ct: Ct);
        await Crypto(f);
        Guid coupon = await Coupon(f, 50, 5);
        var service = Service(f);
        var first = Request(f, qty: 5);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), first, Member, Ct)).Outcome);
        var next = Request(f, qty: 5);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), next, Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='cancelled' AND resolution_kind='replaced'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=5", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.True(await read.QuerySingleAsync(new DbStatement("SELECT ends_at>clock_timestamp() AND ends_at<clock_timestamp()+INTERVAL '2 hours' FROM shop.discounts WHERE id=$1", DbValue.Uuid(coupon)), row => row.GetBoolean(0), Ct));
    }

    [Fact]
    public async Task ObservedPaymentCannotBeReplacedOrHaveItsHeldUnitsReused()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 5, ct: Ct);
        await Crypto(f);
        await Coupon(f, 50, 5);
        var service = Service(f);
        await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, qty: 5), Member, Ct);
        await using (var observe = await f.Sessions.BeginAsync(Ct))
        {
            await observe.ExecuteAsync(new DbStatement("UPDATE shop.payments SET first_payment_observed_at=clock_timestamp(),status='observed'"), Ct);
            await observe.CommitAsync(Ct);
        }
        var error = await Assert.ThrowsAsync<DiscountQuantityException>(() => service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), Request(f, qty: 5), Member, Ct));
        Assert.Equal(0, error.Maximum);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment' AND resolution_kind IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held'", Ct));
    }

    [Fact]
    public async Task BankDestinationIsFrozenForTheOriginalPaymentAndSurvivesCardReplacement()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        await Card(f, "public original number", "public original holder");
        var service = Service(f);
        var request = Request(f, CheckoutPayment.Bank);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        byte[] content;
        await using (var read = await f.Sessions.BeginAsync(Ct))
            content = await read.QuerySingleAsync(new DbStatement("SELECT bank_destination FROM shop.payments WHERE order_id=$1", DbValue.Uuid(request.OrderId)), row => row.GetBinary(0), Ct);
        await Card(f, "public next number", "public next holder");
        try
        {
            using var reader = new BinaryReader(new MemoryStream(content), Encoding.UTF8);
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal("public original number", Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
            Assert.Equal("public original holder", Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(content); }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE quote_currency='TOMAN' AND amount_due=1000000", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task UnpaidSupplierOrderDirectlyRefreshesButDoesNotBindSourceOrAllocateLocalStock()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        await Crypto(f);
        await using (var source = await f.Sessions.BeginAsync(Ct))
        {
            Guid id = Guid.NewGuid();
            await source.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(id), DbValue.Uuid(f.Product)), Ct);
            await source.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(id), DbValue.Uuid(f.Product)), Ct);
            await source.CommitAsync(Ct);
        }
        int calls = 0;
        var catalog = Catalog(f, (request, _) => { Assert.Equal(2, request.Quantity); calls++; return Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 2, 1, "USD", "public quote")); });
        var request = Request(f, qty: 5);
        var service = Service(f, catalog);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }

    [Fact]
    public async Task PaidHundredUnitSourceIssuesOneNonExpiringOwnedCodeWithoutAnyGoods()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 1000, stock: 0, ct: Ct);
        var request = Request(f, CheckoutPayment.Wallet, 100, CheckoutKind.PrepaidUnits);
        var service = Service(f);
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        var pricing = new PricingStore(f.Sessions);
        Guid first;
        await using (var retry = await f.Sessions.BeginAsync(Ct))
        {
            first = await pricing.IssuePaidSourceAsync(retry, f.Context(0), request.OrderId, Ct);
            Assert.Equal(first, await pricing.IssuePaidSourceAsync(retry, f.Context(0), request.OrderId, Ct));
            await retry.CommitAsync(Ct);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discounts WHERE kind='prepaid_entitlement' AND percent_off=100 AND max_total_uses=100 AND ends_at IS NULL AND created_by_admin_id IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE actor_kind='system' AND action='coupon_issue'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE order_kind='prepaid_units' AND fulfillment_status='completed' AND payment_status='paid'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-1000", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        await using (var read = await f.Sessions.BeginAsync(Ct))
        {
            string code = await pricing.ReadCodeAsync(read, f.Context(0), CheckoutFixture.Actor(0), first, Ct);
            Assert.Equal(48, code.Length);
        }
        await using (var foreign = await f.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pricing.ReadCodeAsync(foreign, f.Context(3), CheckoutFixture.Actor(3), first, Ct));
    }

    private static async Task Crypto(CheckoutFixture f)
    {
        await using var edit = await f.Sessions.BeginAsync(Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET allow_crypto=true,policy_version=policy_version+1"), Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_crypto=true,revision=revision+1"), Ct);
        await edit.CommitAsync(Ct);
    }
    private static async Task<Guid> Coupon(CheckoutFixture f, decimal percent, long cap)
    {
        Guid id = Guid.NewGuid();
        var actor = await f.AdminAsync(Ct);
        await using var issue = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions).IssueAsync(issue, f.Context(0), actor, new(id, "PUBLIC-" + id.ToString("N"), CouponKind.Promotion, percent, f.Product, f.Users[0], MaximumUses: cap, EndsAt: DateTime.UtcNow.AddHours(1)), Guid.NewGuid(), Ct);
        await issue.CommitAsync(Ct);
        return id;
    }
    private static async Task Card(CheckoutFixture f, string number, string holder)
    {
        byte[] n = Encoding.UTF8.GetBytes(number);
        byte[] h = Encoding.UTF8.GetBytes(holder);
        await using var edit = await f.Sessions.BeginAsync(Ct);
        await edit.ExecuteAsync(new DbStatement("""
            UPDATE shop.brands SET market='iran',allow_bank=true,policy_version=policy_version+1,bank_number=$1,bank_holder=$2,
                bank_card_fingerprint=$3,bank_changed_at=clock_timestamp() WHERE id=$4
            """, DbValue.Binary(n), DbValue.Binary(h), DbValue.Binary(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(number))), DbValue.Uuid(f.Brands[0])), Ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_bank=true,price_toman=1000000,auto_price_toman=false,revision=revision+1 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), Ct);
        await edit.CommitAsync(Ct);
    }
}
