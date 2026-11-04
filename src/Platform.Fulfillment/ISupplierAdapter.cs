using Platform.Core;

namespace Platform.Fulfillment;

public interface ISupplierAdapter
{
    string Provider { get; }
    string Account { get; }
    bool ProvenIntentLookup { get; }
    bool UsesPurchaseKey => false;
    Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct);
    Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct);
    Task<SupplierOutcome> SubmitAttemptAsync(SupplierBinding binding, string? purchaseKey, decimal maximumUnitCharge, CancellationToken ct) => SubmitAsync(binding, maximumUnitCharge, ct);
    Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct);
}
public interface ISupplierEvidenceArchive
{
    Task<string> StoreAsync(SupplierBinding binding, Guid attempt, ReadOnlyMemory<byte> evidence, CancellationToken ct);
}
