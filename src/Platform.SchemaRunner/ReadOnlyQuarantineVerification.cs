using System.Text.Json;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ReadOnlyQuarantineVerification
{
    internal static LegacyOriginalReviewAudit Verify(LegacyWalletOrderPlan plan, string reviewJson)
    {
        var disposition = LegacyRecordDispositionReport.Verify(plan);
        var audit = LegacyOriginalReviewQualification.Verify(plan.Archive, reviewJson, Convert.ToHexStringLower(plan.Manifest.Digest.Span), plan.Set.Orders.Count);
        using var document = JsonDocument.Parse(reviewJson); var root = document.RootElement;
        if (!disposition.Accounted || root.GetProperty("typed_payments").GetInt32() != disposition.Payments || root.GetProperty("native_zero_proofs").GetInt32() != disposition.NativeZero || root.GetProperty("inactive_master_catalog").GetInt32() != disposition.Catalog || audit.ReviewedRecords != plan.Review.Count || root.GetProperty("review_reason_counts").EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != disposition.ReviewReasons.Count) throw new MigrationRejectedException();
        var expected = plan.Review.ToDictionary(r => (r.Projection, r.LegacyId));
        foreach (var row in root.GetProperty("review").EnumerateArray())
        {
            if (!Enum.TryParse<LegacyProjection>(row.GetProperty("projection").GetString(), out var projection) || !expected.TryGetValue((projection, row.GetProperty("legacy_id").GetString() ?? ""), out var original) || row.GetProperty("target").GetGuid() != original.Target || row.GetProperty("reason").GetString() != original.Reason) throw new MigrationRejectedException();
        }
        return audit;
    }
}
