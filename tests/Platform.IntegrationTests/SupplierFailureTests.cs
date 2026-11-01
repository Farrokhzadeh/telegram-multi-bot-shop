using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRetryRequiresProvenNoEffectAndRetainsOriginalIntentCostAndAccount(bool recovered)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets, -100777);
        var port = new SyntheticSupplier { Submit = _ => recovered ? SyntheticSupplier.Unknown() : SyntheticSupplier.NoEffect(), Lookup = _ => SyntheticSupplier.NoEffect() };
        var worker = new SupplierFulfillmentWorker(store, port, port);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        await worker.RunAsync(f.Context(0), lease, Ct);
        if (recovered)
        {
            await SupplierRecoveryTests.Due(f);
            await worker.RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct), Ct);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='requires_admin' AND resolution_kind IS NULL", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'", Ct));
        var admin = await f.AdminAsync(Ct);
        long revision = await AdminCatalogAndResolutionTests.Revision(f, order);
        var resolution = new OrderResolution("retry_supplier", revision, "Public reconciled retry", Guid.NewGuid(), binding.RequestId, 0);
        var administration = new OrderAdministrationStore(f.Sessions, f.Secrets);
        await administration.ResolveAsync(admin, f.Context(0), order, resolution, Ct);
        await administration.ResolveAsync(admin, f.Context(0), order, resolution, Ct);
        port.Submit = b => SyntheticSupplier.Complete(b);
        await worker.RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Assert.Equal(2, port.Submits);
        Assert.All(port.Intents, intent => Assert.Equal(binding.Intent, intent));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='succeeded' AND requested_quantity=5 AND cost_baseline_amount=1 AND account_ref='public account'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE admin_authorization_audit_id IS NOT NULL AND max_charge_amount=1.10", Ct));
    }
    [Fact]
    public async Task RetryAndTerminalCreditSerializeToOneAuditedOutcomeAndReturnTheWholeHeldClaimOnlyOnCredit()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierRecoveryTests.Coupon(f, 50);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, Ct);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.NoEffect() };
        var store = new SupplierRequestStore(f.Sessions, f.Secrets, -100777);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, Ct);
        var actor = await f.AdminAsync(Ct);
        long revision = await AdminCatalogAndResolutionTests.Revision(f, order);
        var admin = new OrderAdministrationStore(f.Sessions, f.Secrets);
        async Task<bool> Resolve(string action)
        {
            try { await admin.ResolveAsync(actor, f.Context(0), order, new(action, revision, "Public race decision", Guid.NewGuid(), binding.RequestId, 0), Ct); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Resolve("retry_supplier"), Resolve("credit_supplier_failure"));
        Assert.Single(results, x => x);
        long credits = await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit' AND signed_amount=25", Ct);
        Assert.InRange(credits, 0L, 1L);
        Assert.Equal(credits, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=5", Ct));
        Assert.Equal(1L - credits, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE actor_kind='admin' AND action IN ('retry_supplier','supplier_credit')", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-25", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
    }
}
