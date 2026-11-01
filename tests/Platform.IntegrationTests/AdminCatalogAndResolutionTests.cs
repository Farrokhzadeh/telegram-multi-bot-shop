using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AdminCatalogAndResolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NativeCompatibleAdministrationImportsPostingsAndCampaignsQualifyTogether() => await AdministrationPhaseQualification.RunAsync(Ct);
    [Fact]
    public async Task CatalogPoliciesOverridesPricesAndStockRemainAuditedScopedAndRevisionChecked()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new AdminCatalogStore(f.Sessions, f.Secrets);
        var listing = new ListingEdit(true, "override", "Public override", "clear", null, "inherit", null, "inherit", null, "12.50", "20000", false, true, false, false, 1, 5);
        Guid command = Guid.NewGuid();
        Assert.Equal(1, await store.ListingAsync(admin, f.Context(0), f.Product, listing, 0, command, Ct));
        Assert.Equal(1, await store.ListingAsync(admin, f.Context(0), f.Product, listing, 0, command, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListingAsync(admin, f.Context(0), f.Product, listing with { PriceUsd = "13" }, 0, Guid.NewGuid(), Ct));
        await store.ProductAsync(admin, f.Product, new("Public revised master", "Public inherited description", null, null, "local", "immediate", true, true), 0, Guid.NewGuid(), Ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException());
        Guid item = Guid.NewGuid();
        InventoryImportItem[] items = [new(item, "Public synthetic stock value")];
        Guid import = Guid.NewGuid();
        await store.ImportAsync(admin, f.Product, items, import, Ct);
        await store.ImportAsync(admin, f.Product, items, import, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(admin, f.Product, [new(Guid.NewGuid(), items[0].Credential)], Guid.NewGuid(), Ct));
        var a = await catalog.QuoteAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        var b = await catalog.QuoteAsync(f.Context(1), CheckoutFixture.Actor(1), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        Assert.Equal(12.50m, a.TotalUsd.Amount);
        Assert.Equal(10m, b.TotalUsd.Amount);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.brand_products WHERE title_mode='override' AND description_mode='clear' AND price_usdt=12.50 AND NOT auto_price_toman", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.brand_products WHERE title_mode='inherit' AND price_usdt=10", Ct));
        var inventory = await store.InventoryAsync(admin, f.Product, null, 1, Ct);
        Assert.Single(inventory.Items);
        Assert.Null(inventory.Next);
        await using (var read = await f.Sessions.BeginAsync(Ct))
        {
            var encrypted = await read.QuerySingleAsync(new DbStatement("SELECT credential_ciphertext,credential_key_id FROM shop.inventory_items WHERE id=$1", DbValue.Uuid(item)), r => (Value: r.GetBinary(0), Key: r.GetString(1)), Ct);
            byte[] plain = new InventoryCipher(f.Secrets).Decrypt(item, encrypted.Key, encrypted.Value);
            try { Assert.Equal(items[0].Credential, Encoding.UTF8.GetString(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var policy = new BrandPolicyEdit("Public name", "fa", true, true, false, false, false, false, false, null, false, null);
        await store.PolicyAsync(admin, f.Context(0), policy, 0, Guid.NewGuid(), Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE public_name='Public name' AND policy_version=1", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.brands WHERE default_language='en' AND policy_version=0", Ct));
    }
    [Fact]
    public async Task HundredWalletAdjustmentReplaysPostOneExactUsdEntryAndChangedOrStaleCommandsReject()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
        var value = new WalletAdjustment(f.Users[0], "5.25", 0, "Public correction", Guid.NewGuid());
        using var limit = new SemaphoreSlim(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ => { await limit.WaitAsync(Ct); try { return await store.AdjustAsync(admin, f.Context(0), value, Ct); } finally { limit.Release(); } }));
        Assert.All(results, id => Assert.Equal(value.CommandId, id));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount=5.25 AND posting_version=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets WHERE balance=5.25 AND version=1", Ct));
        var conflict = await Assert.ThrowsAsync<DbOperationException>(() => store.AdjustAsync(admin, f.Context(0), value with { AmountUsd = "6" }, Ct));
        Assert.Equal(DbFailureKind.InvalidData, conflict.Failure.Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdjustAsync(admin, f.Context(0), value with { CommandId = Guid.NewGuid() }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdjustAsync(admin, f.Context(0), value with { CommandId = Guid.NewGuid(), ExpectedVersion = 1, AmountUsd = "-6" }, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='wallet_adjustment' AND command_sha256 IS NOT NULL", Ct));
    }
    [Fact]
    public async Task PaidPreorderPauseResumeAndCancellationPreserveMoneyPriorityAndReturnOnlyHeldUses()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 100, stock: 0, ct: Ct);
        await using (var policy = await f.Sessions.BeginAsync(Ct))
        { await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), Ct); await policy.CommitAsync(Ct); }
        var admin = await f.AdminAsync(Ct);
        var pricing = new PricingStore(f.Sessions, f.Secrets);
        Guid coupon = Guid.NewGuid();
        DateTime ends;
        await using (var clock = await f.Sessions.BeginAsync(Ct)) ends = await clock.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()+INTERVAL '1 hour'"), r => r.GetUtc(0), Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        { await pricing.IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-PAUSE", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 2, EndsAt: ends), Guid.NewGuid(), Ct); await issue.CommitAsync(Ct); }
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException()));
        var order = await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 2), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
        long revision = await Revision(f, order.OrderId!.Value);
        var pause = new OrderResolution("pause_preorder", revision, "Public blocking hold", Guid.NewGuid());
        await store.ResolveAsync(admin, f.Context(0), order.OrderId.Value, pause, Ct);
        await store.ResolveAsync(admin, f.Context(0), order.OrderId.Value, pause, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE hold_at IS NOT NULL AND payment_status='paid' AND fulfillment_status='waiting_stock'", Ct));
        await store.ResolveAsync(admin, f.Context(0), order.OrderId.Value, new("resume_preorder", revision + 1, "Public resume", Guid.NewGuid()), Ct);
        var cancel = new OrderResolution("cancel_local_preorder", revision + 2, "Public cancellation state only", Guid.NewGuid());
        await store.ResolveAsync(admin, f.Context(0), order.OrderId.Value, cancel, Ct);
        await store.ResolveAsync(admin, f.Context(0), order.OrderId.Value, cancel, Ct);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='cancelled' AND resolution_kind='local_preorder_cancellation' AND resolution_wallet_entry_id IS NULL", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=2", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-10", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        await using var read = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(ends, await read.QuerySingleAsync(new DbStatement("SELECT ends_at FROM shop.discounts WHERE id=$1", DbValue.Uuid(coupon)), r => r.GetUtc(0), Ct));
    }
    internal static async Task<long> Revision(CheckoutFixture f, Guid order)
    { await using var read = await f.Sessions.BeginAsync(Ct); return await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct); }
}
