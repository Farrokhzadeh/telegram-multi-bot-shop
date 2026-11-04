using System.Security.Cryptography;
using System.Text;

namespace Platform.Core;

public sealed record SupplierBinding(Guid RequestId, Guid OrderId, Guid ProductId, Guid SourceId, long SourceRevision, string Provider, string Account, string Product,
    string? Variant, string Intent, int Quantity, decimal UnitBaseline, string Currency, decimal? ApprovedUnitCeiling);
public sealed record SupplierQuote(string Provider, string Account, string Product, string? Variant, string Currency, decimal UnitCost, bool Ready,
    bool EnforcesMaximum, DateTime ObservedAt, string EvidenceReference);
public enum SupplierOutcomeKind { Completed, Processing, ConfirmedNoEffect, Unknown }
public enum SupplierItemDisposition { Customer, Bonus, Unverified }
public sealed class SupplierItem : IDisposable
{
    private readonly byte[] content;
    public SupplierItem(string reference, SupplierItemDisposition disposition, string contentKind, ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 256 || content.Length is < 1 or > 65536 || contentKind is not ("account" or "key" or "code")) throw new ArgumentException("Invalid supplier item.");
        Reference = reference; Disposition = disposition; ContentKind = contentKind; this.content = content.ToArray();
    }
    public string Reference { get; }
    public SupplierItemDisposition Disposition { get; }
    public string ContentKind { get; }
    public ReadOnlyMemory<byte> Content => content;
    public void Dispose() => CryptographicOperations.ZeroMemory(content);
    public override string ToString() => "SupplierItem [redacted]";
}
public sealed class SupplierOutcome : IDisposable
{
    private readonly byte[] evidence;
    public SupplierOutcome(SupplierOutcomeKind kind, string? purchaseReference, string? currency, decimal? chargedAmount, SupplierItem[] items, ReadOnlySpan<byte> evidence)
    {
        if (!Enum.IsDefined(kind) || items.Length > 10000 || evidence.Length > 1048576 || purchaseReference?.Length > 256 || currency?.Length > 32 || chargedAmount < 0) throw new ArgumentException("Invalid supplier result.");
        Kind = kind; PurchaseReference = purchaseReference; Currency = currency; ChargedAmount = chargedAmount; Items = items.ToArray(); this.evidence = evidence.ToArray();
    }
    public SupplierOutcomeKind Kind { get; }
    public string? PurchaseReference { get; }
    public string? Currency { get; }
    public decimal? ChargedAmount { get; }
    public IReadOnlyList<SupplierItem> Items { get; }
    public ReadOnlyMemory<byte> Evidence => evidence;
    public void Dispose() { CryptographicOperations.ZeroMemory(evidence); foreach (var item in Items) item.Dispose(); }
    public override string ToString() => "SupplierOutcome [redacted]";
}
public static class SupplierRules
{
    public static decimal UnitCeiling(SupplierBinding binding)
    {
        if (binding.Quantity <= 0 || binding.UnitBaseline <= 0 || binding.ApprovedUnitCeiling <= 0) throw new ArgumentException("Invalid original supplier cost.");
        return binding.ApprovedUnitCeiling ?? ExactDecimal.Multiply(binding.UnitBaseline, 1.10m);
    }
    public static string? Preflight(SupplierBinding binding, SupplierQuote quote, DateTime now)
    {
        if (quote.Provider != binding.Provider || quote.Account != binding.Account || quote.Product != binding.Product || quote.Variant != binding.Variant) return "identity_mismatch";
        if (!quote.Ready) return "source_ineligible";
        if (quote.Currency != binding.Currency || quote.UnitCost <= 0 || quote.ObservedAt.Kind != DateTimeKind.Utc || quote.ObservedAt > now || now - quote.ObservedAt > TimeSpan.FromSeconds(30) || string.IsNullOrWhiteSpace(quote.EvidenceReference)) return "cost_unverified";
        try { return quote.UnitCost <= UnitCeiling(binding) ? null : "cost_limit"; }
        catch (ArithmeticException) { return "cost_unverified"; }
    }
    public static string? Identity(SupplierBinding binding, SupplierOutcome result)
    {
        if (result.Items.Any(i => i.Disposition is not (SupplierItemDisposition.Customer or SupplierItemDisposition.Bonus)) || result.Items.Select(i => i.Reference).Distinct(StringComparer.Ordinal).Count() != result.Items.Count ||
            result.Items.Count(i => i.Disposition == SupplierItemDisposition.Customer) > binding.Quantity ||
            (result.Items.Count > 0 && string.IsNullOrWhiteSpace(result.PurchaseReference)) ||
            (result.Kind == SupplierOutcomeKind.ConfirmedNoEffect && (result.Items.Count > 0 || result.ChargedAmount is > 0))) return "identity_mismatch";
        return null;
    }
    public static string? Validate(SupplierBinding binding, SupplierOutcome result)
    {
        string? identity = Identity(binding, result); if (identity is not null) return identity;
        if (result.Kind == SupplierOutcomeKind.ConfirmedNoEffect) return "provider_failed";
        if (result.Kind == SupplierOutcomeKind.Unknown) return "provider_unknown";
        if (result.Kind == SupplierOutcomeKind.Processing) return "provider_unknown";
        if (result.Currency != binding.Currency || result.ChargedAmount is null) return "cost_unverified";
        try { if (result.ChargedAmount > ExactDecimal.Multiply(UnitCeiling(binding), binding.Quantity)) return "cost_limit"; }
        catch (ArithmeticException) { return "cost_unverified"; }
        return result.Items.Count(i => i.Disposition == SupplierItemDisposition.Customer) == binding.Quantity ? null : "partial_acquisition";
    }
    public static byte[] ItemKey(SupplierBinding binding, string reference)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("supplier-item-v1"); writer.Write(binding.Provider); writer.Write(binding.Account); writer.Write(reference); }
        return SHA256.HashData(stream.ToArray());
    }
}
