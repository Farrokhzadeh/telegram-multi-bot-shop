using System.Globalization;
using System.Text.Json;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyReadOnlyOrder(Guid ArchiveId, string OriginalReference, long OriginalProduct, int Quantity, decimal OriginalTotal, string OriginalCurrency, DateTime CreatedAt, string? SavedTitle, string? SavedPaymentMethod, bool IncompleteTerms)
{
    public bool ReadOnly => true;
    public bool ImportApproved => false;
    public bool SettlementVerified => false;
    public bool DeliveryVerified => false;
    public override string ToString() => "LegacyReadOnlyOrder [redacted]";
}
public sealed class LegacyReadOnlyOrderHistory(SecretSnapshot secrets)
{
    public LegacyReadOnlyOrder Read(BrandContext context, VerifiedImportManifest manifest, Guid member, ArchivedLegacyRecord row, ProtectedLegacyEvidence retained)
    {
        if (context.BrandId != manifest.TargetBrand || member == Guid.Empty || row.Source.Projection != LegacyProjection.OrderRecord || !new LegacyRecordArchive(secrets).Matches(context, row, retained)) throw new MigrationRejectedException();
        manifest.Require(row.Source.Proof(row.Target, context, secrets));
        try
        {
            using var document = row.Source.Open(); var source = document.RootElement;
            long owner = source.GetProperty("user_id").GetInt64(), product = source.GetProperty("product_id").GetInt64(); int quantity = source.GetProperty("qty").GetInt32(); decimal total = LegacyValues.Number(source, "total"); string currency = LegacyValues.Text(source, "currency"); DateTime created = LegacyValues.Time(source, "created_at");
            if (owner <= 0 || product <= 0 || quantity <= 0 || total < 0 || currency is not ("USD" or "IRR") || !manifest.Proofs.Any(p => p.Projection == LegacyProjection.User && p.Target == member && p.Brand == context.BrandId && p.LegacyId == owner.ToString(CultureInfo.InvariantCulture))) throw new MigrationRejectedException();
            string? title = null, method = null;
            if (source.TryGetProperty("checkout_snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
            {
                if (snapshot.TryGetProperty("product_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is string saved && !string.IsNullOrWhiteSpace(saved) && saved.Length <= 160) title = saved;
                if (snapshot.TryGetProperty("payment_method", out var payment) && payment.ValueKind == JsonValueKind.String && payment.GetString() is "wallet" or "bank" or "crypto") method = payment.GetString();
            }
            bool incomplete = false;
            try { _ = LegacyHistoricalRecord.ReadTerms(row.Source); }
            catch (MigrationRejectedException) { incomplete = true; }
            return new(row.Target, row.Source.Id, product, quantity, total, currency, created, title, method, incomplete);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArithmeticException) { throw new MigrationRejectedException(); }
    }
}
