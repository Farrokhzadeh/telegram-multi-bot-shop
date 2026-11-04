using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment;

public sealed class SupplierFulfillmentWorker(SupplierRequestStore store, ISupplierAdapter adapter, ISupplierEvidenceArchive archive)
{
    public async Task RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        var binding = await store.ReadAsync(context, lease, ct);
        if (adapter.Provider != binding.Provider || adapter.Account != binding.Account) throw new UnauthorizedAccessException("Original supplier account is unavailable.");
        SupplierQuote? quote = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (lease.Intent.Kind == JobKind.SubmitSupplier)
        {
            try { quote = await adapter.InspectAsync(binding, deadline.Token); }
            catch (Exception) { }
        }
        var attempt = await store.BeginAsync(context, lease, quote, ct, adapter.UsesPurchaseKey);
        if (attempt is null) return;
        SupplierOutcome result;
        try
        {
            result = attempt.Operation == "submit" ? await adapter.SubmitAttemptAsync(binding, attempt.PurchaseKey, SupplierRules.UnitCeiling(binding), deadline.Token) :
                attempt.PurchaseReference is not null || adapter.ProvenIntentLookup ? await adapter.LookupAsync(binding, attempt.PurchaseReference, deadline.Token) : Unknown();
        }
        catch (Exception) { result = Unknown(); }
        using (result)
        {
            string? evidence = null;
            try
            {
                if (result.Evidence.Length > 0) evidence = await archive.StoreAsync(binding, attempt.Id, result.Evidence, deadline.Token);
            }
            catch (Exception) { }
            await store.RecordAsync(context, lease, attempt, result, evidence, CancellationToken.None);
        }
    }
    private static SupplierOutcome Unknown() => new(SupplierOutcomeKind.Unknown, null, null, null, [], []);
}
