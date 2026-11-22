using System.Text;
using Platform.Core;

namespace Platform.Fulfillment;

public sealed class SqliteSyntheticSupplierAdapter : ISupplierAdapter
{
    public string Provider => "synthetic";
    public string Account => "public-test-account";
    public bool ProvenIntentLookup => true;
    public bool UsesPurchaseKey => true;
    public bool SupportsExport => true;

    public Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
    {
        Verify(binding);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new SupplierQuote(Provider, Account, binding.Product, binding.Variant,
            binding.Currency, binding.UnitBaseline, true, true, DateTime.UtcNow,
            "protected:synthetic-stage/quote/" + binding.RequestId.ToString("N")));
    }

    public Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge,
        CancellationToken ct) => SubmitAttemptAsync(binding, null, maximumUnitCharge, ct);

    public Task<SupplierOutcome> SubmitAttemptAsync(SupplierBinding binding, string? purchaseKey,
        decimal maximumUnitCharge, CancellationToken ct)
    {
        Verify(binding);
        ct.ThrowIfCancellationRequested();
        if (maximumUnitCharge < binding.UnitBaseline)
            throw new InvalidOperationException("The synthetic supplier charge exceeds its authorized ceiling.");
        return Task.FromResult(Complete(binding));
    }

    public Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference,
        CancellationToken ct)
    {
        Verify(binding);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Complete(binding));
    }

    public Task<SupplierOutcome> ExportAsync(SupplierBinding binding, string purchaseReference,
        CancellationToken ct)
    {
        Verify(binding);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Complete(binding));
    }

    private static SupplierOutcome Complete(SupplierBinding binding)
    {
        var items = Enumerable.Range(1, binding.Quantity)
            .Select(number => new SupplierItem(
                "stage-" + binding.RequestId.ToString("N") + "-" + number,
                SupplierItemDisposition.Customer, "code",
                Encoding.UTF8.GetBytes("synthetic-stage-credential-" +
                    binding.RequestId.ToString("N") + "-" + number)))
            .ToArray();
        return new SupplierOutcome(SupplierOutcomeKind.Completed,
            "synthetic-stage:" + binding.RequestId.ToString("D"),
            binding.Currency, binding.UnitBaseline, items,
            Encoding.UTF8.GetBytes("isolated synthetic supplier outcome " +
                binding.RequestId.ToString("D")));
    }

    private static void Verify(SupplierBinding binding)
    {
        if (binding.Provider != "synthetic" || binding.Account != "public-test-account" ||
            string.IsNullOrWhiteSpace(binding.Product) || binding.Quantity is < 1 or > 5 ||
            binding.UnitBaseline <= 0 || binding.Currency != "USD")
            throw new UnauthorizedAccessException("Only synthetic stage supplier requests are supported.");
    }
}
