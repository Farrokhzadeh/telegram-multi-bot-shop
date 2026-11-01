using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SupplierRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NativeCompatibleMixedRemainderBonusAndCompleteManifest() => await SupplierQualification.RunAsync(Ct);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAfterSuccessAndCrashAfterIntentNeverResubmitOriginalPurchase(bool crash)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets, -100777);
        var port = new SyntheticSupplier { Submit = _ => SyntheticSupplier.Unknown() };
        var worker = new SupplierFulfillmentWorker(store, port, port);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        if (crash)
        {
            await store.BeginAsync(f.Context(0), lease, await port.InspectAsync(binding, Ct), Ct);
            await Expire(f, lease);
            Assert.Null(await ClaimOptional(f, JobKind.SubmitSupplier));
            Assert.True(await store.RecoverExpiredAsync(f.Context(0), Ct));
            Assert.False(await store.RecoverExpiredAsync(f.Context(0), Ct));
        }
        else await worker.RunAsync(f.Context(0), lease, Ct);
        await Due(f);
        var lookup = await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct);
        await worker.RunAsync(f.Context(0), lookup, Ct);
        Assert.Equal(crash ? 0 : 1, port.Submits);
        Assert.Equal(1, port.Lookups);
        Assert.All(port.Intents, value => Assert.Equal(binding.Intent, value));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='succeeded'", Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest' AND status='pending'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE review_code IS NOT NULL OR release_authorized_at IS NOT NULL", Ct));
    }
    [Fact]
    public async Task CrashBeforeIntentReclaimsSameLocalJobWithoutInventingRemoteEffects()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        await Expire(f, lease);
        Assert.True(await store.RecoverExpiredAsync(f.Context(0), Ct));
        var retry = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        Assert.Equal(lease.Id, retry.Id);
        Assert.Equal(lease.Attempt + 1, retry.Attempt);
        var port = new SyntheticSupplier();
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), retry, Ct);
        Assert.Equal(1, port.Submits);
    }
    [Theory]
    [InlineData("1.10", true, true, null)]
    [InlineData("1.10000001", true, true, "cost_limit")]
    [InlineData("0.9", true, true, null)]
    [InlineData("1", false, true, "cost_unverified")]
    [InlineData("1", true, false, "source_ineligible")]
    public async Task PrepurchaseChargeGateNeverRepricesOrInventsFinancialCompensation(string cost, bool enforced, bool ready, string? blocker)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        var port = new SyntheticSupplier { UnitCost = ExactDecimal.Parse(cost), Enforced = enforced, Ready = ready };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Assert.Equal(blocker is null ? 1 : 0, port.Submits);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE cost_baseline_amount=1", Ct));
        if (blocker is not null) Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid' AND fulfillment_status='requires_admin' AND review_code='" + blocker + "'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'", Ct));
    }
    [Fact]
    public async Task PartialAcquisitionRetainsCouponAndMoneyAndCannotPrepareCustomerManifest()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await Coupon(f, 100);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, customers: 3) };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='partial_pending_admin'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.payments", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind<>'opening_balance'", Ct));
        Assert.Null(await ClaimOptional(f, JobKind.PrepareManifest));
    }
    [Fact]
    public async Task LostArchiveProtectsOriginalUnknownObligationWithoutAllocatingCredentials()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 1, Ct);
        var port = new SyntheticSupplier { ArchiveFails = true };
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Assert.Equal(1, port.Submits);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE status='unknown' AND review_code='credential_recovery'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE evidence_ref IS NOT NULL", Ct));
    }
    [Fact]
    public async Task BonusAccessRequiresCurrentIndependentAdministratorAndCannotJoinCustomerUnits()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets, -100777);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, 1) };
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, Ct);
        Guid bonus;
        await using (var read = await f.Sessions.BeginAsync(Ct)) bonus = await read.QuerySingleAsync(new DbStatement("SELECT id FROM shop.inventory_items WHERE origin_kind='supplier_bonus'"), r => r.GetGuid(0), Ct);
        var admin = await f.AdminAsync(Ct);
        using (var opened = await store.OpenBonusAsync(admin, f.Context(0), binding.RequestId, bonus, Guid.NewGuid(), Ct))
        { Assert.Equal("public supplier credential"u8.ToArray(), opened.Content.ToArray()); Assert.DoesNotContain("public supplier credential", opened.ToString()); }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE actor_kind='admin' AND action='maintenance' AND entity_kind='supplier_request'", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenBonusAsync(admin, f.Context(1), binding.RequestId, bonus, Guid.NewGuid(), Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='supplier'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE origin_kind='supplier_bonus' AND state<>'admin_held'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE recipient_kind='staff_chat' AND staff_chat_id=-100777 AND purpose='staff_supplier_bonus'", Ct));
        await using (var revoke = await f.Sessions.BeginAsync(Ct))
        { await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp()"), Ct); await revoke.CommitAsync(Ct); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.OpenBonusAsync(admin, f.Context(0), binding.RequestId, bonus, Guid.NewGuid(), Ct));
    }
    [Fact]
    public async Task ReorderedRecoveryDeduplicatesPartialUnitsAndBonusesWithoutClearingProtectedPartialReview()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await SupplierFixture.ConfigureAsync(f, Ct);
        await SupplierFixture.AcceptAsync(f, 5, Ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, 1, 3) };
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        var worker = new SupplierFulfillmentWorker(store, port, port);
        await worker.RunAsync(f.Context(0), lease, Ct);
        await using (var schedule = await f.Sessions.BeginAsync(Ct))
        { await OutboxStore.EnqueueAsync(schedule, JobIntent.Supplier(JobKind.ReconcileSupplier, f.Brands[0], binding.RequestId), Guid.NewGuid(), DateTime.UtcNow, Ct); await schedule.CommitAsync(Ct); }
        port.Lookup = b =>
        {
            using var all = SyntheticSupplier.Complete(b, 1);
            return new SupplierOutcome(SupplierOutcomeKind.Completed, all.PurchaseReference, all.Currency, all.ChargedAmount, all.Items.Reverse().Select(i => new SupplierItem(i.Reference, i.Disposition, i.ContentKind, i.Content.Span)).ToArray(), all.Evidence.Span);
        };
        await worker.RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, Ct), Ct);
        Assert.Equal(1, port.Submits);
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE origin_kind='supplier_bonus'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='requires_admin' AND review_code='partial_acquisition' AND release_authorized_at IS NULL", Ct));
        Assert.Null(await ClaimOptional(f, JobKind.PrepareManifest));
    }
    internal static async Task Coupon(CheckoutFixture f, int percent)
    {
        var admin = await f.AdminAsync(Ct);
        await using var session = await f.Sessions.BeginAsync(Ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(session, f.Context(0), admin, new(Guid.NewGuid(), "PUBLIC-SUPPLIER-COUPON", CouponKind.Goodwill, percent, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
        await session.CommitAsync(Ct);
    }
    internal static async Task Due(CheckoutFixture f)
    { await using var session = await f.Sessions.BeginAsync(Ct); await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='reconcile_supplier' AND status='pending'"), Ct); await session.CommitAsync(Ct); }
    private static async Task Expire(CheckoutFixture f, JobLease lease)
    { await using var session = await f.Sessions.BeginAsync(Ct); await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(lease.Id)), Ct); await session.CommitAsync(Ct); }
    private static async Task<JobLease?> ClaimOptional(CheckoutFixture f, JobKind kind)
    { await using var session = await f.Sessions.BeginAsync(Ct); var lease = await OutboxStore.ClaimAsync(session, kind, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct); await session.CommitAsync(Ct); return lease; }
}
