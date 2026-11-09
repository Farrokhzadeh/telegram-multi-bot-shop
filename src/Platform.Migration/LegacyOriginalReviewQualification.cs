using System.Security.Cryptography;
using System.Text.Json;

namespace Platform.Migration;

public sealed record LegacyOriginalReviewAudit(int OriginalRecords, int SelectedRecords, int ReviewedRecords, int OriginalOrders, int TypedOrders, int ReviewedOrders)
{
    public override string ToString() => "LegacyOriginalReviewAudit [redacted]";
}

public static class LegacyOriginalReviewQualification
{
    public static LegacyOriginalReviewAudit Verify(VerifiedImportManifest manifest, string reviewJson, string typedPlanDigest, int expectedTypedOrders)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (reviewJson is null || reviewJson.Length is < 2 or > 8388608 || typedPlanDigest is null || typedPlanDigest.Length != 64 ||
            !typedPlanDigest.All(char.IsAsciiHexDigit) || expectedTypedOrders < 0) throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(reviewJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 10 ||
            root.GetProperty("archive_manifest_sha256").GetString() != Convert.ToHexStringLower(manifest.Digest.Span) ||
            root.GetProperty("typed_manifest_sha256").GetString() != typedPlanDigest)
            throw new MigrationRejectedException();
        var original = manifest.Proofs.Where(p => LegacySourceRecord.Projections.Contains(p.Projection)).ToDictionary(p => (p.Projection, p.LegacyId));
        int expectedOrders = original.Values.Count(p => p.Projection == LegacyProjection.OrderRecord);
        int selectedOrders = root.GetProperty("typed_orders").GetInt32();
        int selectedPayments = root.GetProperty("typed_payments").GetInt32();
        int selectedZeros = root.GetProperty("native_zero_proofs").GetInt32();
        int selectedCatalog = root.GetProperty("inactive_master_catalog").GetInt32();
        int reviewedCount = root.GetProperty("reviewed_records").GetInt32();
        if (root.GetProperty("original_records").GetInt32() != original.Count ||
            selectedOrders != expectedTypedOrders || selectedPayments < 0 || selectedZeros < 0 || selectedCatalog < 0 || reviewedCount < 0 ||
            checked(selectedOrders + selectedPayments + selectedZeros + selectedCatalog + reviewedCount) != original.Count)
            throw new MigrationRejectedException();
        var expectedReasons = root.GetProperty("review_reason_counts");
        var entries = root.GetProperty("review");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != reviewedCount ||
            expectedReasons.ValueKind != JsonValueKind.Object) throw new MigrationRejectedException();
        var seen = new HashSet<(LegacyProjection, string)>();
        var actualReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        int reviewedOrders = 0;
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 4 ||
                !Enum.TryParse<LegacyProjection>(item.GetProperty("projection").GetString(), out var projection) ||
                !LegacySourceRecord.Projections.Contains(projection)) throw new MigrationRejectedException();
            string id = item.GetProperty("legacy_id").GetString() ?? throw new MigrationRejectedException();
            string reason = item.GetProperty("reason").GetString() ?? throw new MigrationRejectedException();
            Guid target = item.GetProperty("target").GetGuid();
            if (id.Length is < 1 or > 128 || id.Any(char.IsControl) || reason.Length is < 1 or > 80 ||
                reason.Any(c => c is not (>= 'a' and <= 'z') and not '_') || target == Guid.Empty ||
                !seen.Add((projection, id)) || !original.TryGetValue((projection, id), out var proof) || proof.Target != target)
                throw new MigrationRejectedException();
            actualReasons[reason] = actualReasons.GetValueOrDefault(reason) + 1;
            if (projection == LegacyProjection.OrderRecord)
            {
                if (reason is not ("incomplete_order_terms" or "missing_exact_settled_source_payment")) throw new MigrationRejectedException();
                reviewedOrders++;
            }
        }
        if (expectedReasons.EnumerateObject().Count() != actualReasons.Count ||
            expectedReasons.EnumerateObject().Any(x => !actualReasons.TryGetValue(x.Name, out int count) || x.Value.GetInt32() != count) ||
            selectedOrders + reviewedOrders != expectedOrders)
            throw new MigrationRejectedException();
        return new(original.Count, selectedOrders + selectedPayments + selectedZeros + selectedCatalog, reviewedCount, expectedOrders, selectedOrders, reviewedOrders);
    }
}
