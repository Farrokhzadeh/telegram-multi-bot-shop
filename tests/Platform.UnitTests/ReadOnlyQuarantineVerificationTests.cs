using System.Text;
using System.Text.Json;
using Platform.Migration;
using Platform.SchemaRunner;
using Platform.Testing;
namespace Platform.UnitTests;

public sealed class ReadOnlyQuarantineVerificationTests
{
    private static string Review(LegacyWalletOrderPlan plan, bool inventedCounts = false, bool changedReason = false)
    {
        var d = LegacyRecordDispositionReport.Verify(plan); var rows = plan.Review.Select(r => changedReason ? r with { Reason = "outside_wallet_order_scope" } : r).ToArray();
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject(); w.WriteString("archive_manifest_sha256", Convert.ToHexStringLower(plan.Archive.Digest.Span)); w.WriteString("typed_manifest_sha256", Convert.ToHexStringLower(plan.Manifest.Digest.Span)); w.WriteNumber("original_records", d.Archived); w.WriteNumber("typed_orders", d.Orders); w.WriteNumber("typed_payments", inventedCounts ? d.Payments + 1 : d.Payments); w.WriteNumber("native_zero_proofs", inventedCounts ? d.NativeZero - 1 : d.NativeZero); w.WriteNumber("inactive_master_catalog", d.Catalog); w.WriteNumber("reviewed_records", rows.Length); w.WriteStartObject("review_reason_counts"); foreach (var group in rows.GroupBy(r => r.Reason)) w.WriteNumber(group.Key, group.Count()); w.WriteEndObject(); w.WriteStartArray("review");
            foreach (var r in rows) { w.WriteStartObject(); w.WriteString("projection", r.Projection.ToString()); w.WriteString("legacy_id", r.LegacyId); w.WriteString("target", r.Target); w.WriteString("reason", r.Reason); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject(); w.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("missing_snapshot")]
    [InlineData("zero_valid")]
    public Task ExactComputedDispositionAndOriginalReviewMatch(string fault) => LegacyWalletOrderPlanQualification.RunAsync(fault, TestContext.Current.CancellationToken, plan => Assert.Equal(plan.Review.Count, ReadOnlyQuarantineVerification.Verify(plan, Review(plan)).ReviewedRecords));
    [Fact]
    public Task ConsistentInventedCountsCannotReplaceActualSelectedFamilies() => LegacyWalletOrderPlanQualification.RunAsync("zero_valid", TestContext.Current.CancellationToken, plan => Assert.Throws<MigrationRejectedException>(() => ReadOnlyQuarantineVerification.Verify(plan, Review(plan, inventedCounts: true))));
    [Fact]
    public Task ValidOriginalIdentityCannotAuthorizeChangedQuarantineReason() => LegacyWalletOrderPlanQualification.RunAsync("missing_snapshot", TestContext.Current.CancellationToken, plan => Assert.Throws<MigrationRejectedException>(() => ReadOnlyQuarantineVerification.Verify(plan, Review(plan, changedReason: true))));
}
