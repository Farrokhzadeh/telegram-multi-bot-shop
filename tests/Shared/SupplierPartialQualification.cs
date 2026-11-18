using System.Security.Cryptography;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;

namespace Platform.Testing;

public static class SupplierPartialQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: ct);
        await SupplierFixture.ConfigureAsync(f, ct);
        var actor = await f.AdminAsync(ct);
        await using (var issue = await f.Sessions.BeginAsync(ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), actor, new(Guid.NewGuid(), "PUBLIC-PARTIAL-REVIEW", CouponKind.Goodwill, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), ct);
            await issue.CommitAsync(ct);
        }
        Guid order = await SupplierFixture.AcceptAsync(f, 5, ct);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, customers: 3), Lookup = b => SyntheticSupplier.Complete(b, customers: 3) };
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, ct);
        Guid request = (await store.ReadAsync(f.Context(0), lease, ct)).RequestId;
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, ct);
        foreach (JobKind kind in new[] { JobKind.ReconcileSupplier, JobKind.ExportSupplier })
        {
            await ScheduleAsync(f, request, kind, ct);
            await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, kind, ct), ct);
        }
        if (port.Submits != 1 || port.Lookups != 1 || port.Exports != 1 || await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts", ct) != 3 ||
            await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE (attempt_no=1 AND operation='submit') OR (attempt_no=2 AND operation='status_lookup') OR (attempt_no=3 AND operation='export')", ct) != 3 ||
            await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", ct) != 3 ||
            await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.orders WHERE review_code='partial_acquisition' AND release_authorized_at IS NULL", ct) != 1)
            throw new InvalidOperationException("Partial supplier replay released or duplicated goods.");
        await RequireBlockedAsync(f, order, ct);
        port.Lookup = b => SyntheticSupplier.Complete(b);
        await ScheduleAsync(f, request, JobKind.ReconcileSupplier, ct);
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), port, port).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.ReconcileSupplier, ct), ct);
        await RequireBlockedAsync(f, order, ct);
        long revision = await RevisionAsync(f, order, ct);
        Guid command = Guid.NewGuid();
        for (int i = 0; i < 100; i++) await store.ApproveCompleteAsync(actor, f.Context(0), order, request, revision, command, "Public complete acquisition review", ct);
        if (await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='manual_fulfillment'", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.orders WHERE review_code IS NULL AND release_authorized_at IS NULL", ct) != 1)
            throw new InvalidOperationException("Supplier review approval changed release or coupon facts.");
        var delivery = new DeliveryStore(f.Sessions, f.Secrets);
        await delivery.PrepareAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.PrepareManifest, ct), ct);
        byte[] manifest = await delivery.ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, ct);
        try { if (manifest.Length == 0) throw new InvalidOperationException("Complete supplier manifest is empty."); }
        finally { CryptographicOperations.ZeroMemory(manifest); }
        bool denied = false;
        try { await delivery.ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(1), order, ct); }
        catch (UnauthorizedAccessException) { denied = true; }
        if (!denied || port.Submits != 1 || await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", ct) != 5 ||
            await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND units_covered=5", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-25", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='supplier_wallet_credit'", ct) != 0)
            throw new InvalidOperationException("Supplier whole release changed original money or ownership.");
    }
    public static async Task ScheduleAsync(CheckoutFixture f, Guid request, JobKind kind, CancellationToken ct)
    {
        await using var session = await f.Sessions.BeginAsync(ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(kind, f.Brands[0], request), Guid.NewGuid(), DateTime.UtcNow, ct);
        await session.CommitAsync(ct);
    }
    public static async Task<long> RevisionAsync(CheckoutFixture f, Guid order, CancellationToken ct)
    {
        await using var read = await f.Sessions.BeginAsync(ct);
        return await read.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), ct);
    }
    private static async Task RequireBlockedAsync(CheckoutFixture f, Guid order, CancellationToken ct)
    {
        try
        {
            byte[] content = await new DeliveryStore(f.Sessions, f.Secrets).ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), order, ct);
            CryptographicOperations.ZeroMemory(content);
            throw new InvalidOperationException("Unapproved supplier acquisition exposed a manifest.");
        }
        catch (UnauthorizedAccessException) { }
    }
}
