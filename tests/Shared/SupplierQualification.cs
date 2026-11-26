using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;

namespace Platform.Testing;

public sealed class SyntheticSupplier : ISupplierAdapter, ISupplierEvidenceArchive
{
    public string Provider => "synthetic";
    public string Account => "public account";
    public bool ProvenIntentLookup { get; set; } = true;
    public int Submits { get; private set; }
    public int Lookups { get; private set; }
    public bool SupportsExport { get; set; } = true;
    public int Exports { get; private set; }
    public int Archives { get; private set; }
    public decimal UnitCost { get; set; } = 1;
    public bool Enforced { get; set; } = true;
    public bool Ready { get; set; } = true;
    public bool ArchiveFails { get; set; }
    public Func<SupplierBinding, SupplierOutcome> Submit { get; set; } = b => Complete(b);
    public Func<SupplierBinding, SupplierOutcome> Lookup { get; set; } = b => Complete(b);
    public List<string> Intents { get; } = [];
    public Task<SupplierQuote> InspectAsync(SupplierBinding b, CancellationToken ct) => Task.FromResult(new SupplierQuote(Provider, Account, b.Product, b.Variant, b.Currency, UnitCost, Ready, Enforced, DateTime.UtcNow, "protected:synthetic/quote"));
    public Task<SupplierOutcome> SubmitAsync(SupplierBinding b, decimal maximumUnitCharge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maximumUnitCharge != SupplierRules.UnitCeiling(b)) throw new InvalidOperationException("Charge ceiling changed.");
        Submits++; Intents.Add(b.Intent); return Task.FromResult(Submit(b));
    }
    public Task<SupplierOutcome> LookupAsync(SupplierBinding b, string? originalPurchaseReference, CancellationToken ct)
    { Lookups++; Intents.Add(b.Intent); return Task.FromResult(Lookup(b)); }
    public Task<SupplierOutcome> ExportAsync(SupplierBinding b, string originalPurchaseReference, CancellationToken ct)
    { Exports++; Intents.Add(b.Intent); return Task.FromResult(Lookup(b)); }
    public Task<string> StoreAsync(SupplierBinding b, Guid attempt, ReadOnlyMemory<byte> evidence, CancellationToken ct)
    {
        if (ArchiveFails) throw new InvalidOperationException("Public archive failure");
        if (evidence.Length == 0) throw new InvalidOperationException("Missing wire evidence");
        Archives++; return Task.FromResult("protected:synthetic/supplier/" + attempt.ToString("D"));
    }
    public static SupplierOutcome Complete(SupplierBinding b, int bonus = 0, int? customers = null) => new(SupplierOutcomeKind.Completed, "public purchase", b.Currency, b.Quantity * 1m,
        Enumerable.Range(0, (customers ?? b.Quantity) + bonus).Select(i => new SupplierItem(i < (customers ?? b.Quantity) ? "public item " + i : "public bonus " + (i - (customers ?? b.Quantity)), i < (customers ?? b.Quantity) ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus, "code", "public supplier credential"u8)).ToArray(), "public protected response"u8);
    public static SupplierOutcome Unknown() => new(SupplierOutcomeKind.Unknown, null, null, null, [], "public timeout evidence"u8);
    public static SupplierOutcome NoEffect() => new(SupplierOutcomeKind.ConfirmedNoEffect, null, null, null, [], "public confirmed rejection"u8);
}
public static class SupplierQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: ct);
        await SupplierFixture.ConfigureAsync(f, ct);
        Guid order = await SupplierFixture.AcceptAsync(f, 5, ct);
        var port = new SyntheticSupplier { Submit = b => SyntheticSupplier.Complete(b, 1) };
        var store = new SupplierRequestStore(f.Sessions, -100777);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, ct);
        await new SupplierFulfillmentWorker(store, port, port).RunAsync(f.Context(0), lease, ct);
        if (port.Submits != 1 || port.Archives != 1 || await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE kind='supplier' AND status='acquired'", ct) != 2 ||
            await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE origin_kind='supplier_bonus' AND state='admin_held'", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE requested_quantity=2 AND status='succeeded'", ct) != 1 ||
            await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-50", ct) != 1) throw new InvalidOperationException("Supplier acquisition qualification failed.");
        var manifest = await SupplierFixture.ClaimAsync(f, JobKind.PrepareManifest, ct);
        await new DeliveryStore(f.Sessions).PrepareAsync(f.Context(0), manifest, ct);
        if (await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", ct) != 5 || await f.CountAsync("SELECT count(*) FROM shop.orders WHERE id='" + order.ToString("D") + "' AND release_authorized_at IS NOT NULL", ct) != 1) throw new InvalidOperationException("Complete mixed manifest failed.");
    }
}
