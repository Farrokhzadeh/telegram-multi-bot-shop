using System.Text.Json;
using Platform.Migration;
namespace Platform.UnitTests;

public sealed class RedactedLegacySnapshotTests
{
    private static RedactedLegacySnapshotBrand Brand(string name) => new(name, 56, 100, 2, 0, new(3, 3, 0, 0, 0, 0, 0, true, 0, 0, 0, 0, 0, 0, 0, 0, 0, true), 2, 1, 0, 1, 0, 0, 1, new Dictionary<string, int>());
    [Fact]
    public void SourceGeneratedReportHasExplicitCountOnlyScopeAndNoReleaseClaims()
    {
        var report = ReconciliationReport.RedactSnapshot([Brand("acc-abad"), Brand("kuala"), Brand("zikotech")]); Assert.True(report.PreservationMatched); Assert.False(report.FinancialObligationsVerified); Assert.False(report.ReleaseReady); Assert.False(report.MediaAvailabilityVerified);
        string json = JsonSerializer.Serialize(report, MigrationReportJsonContext.Default.RedactedLegacySnapshotReconciliation); using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "brands", "financialObligationsVerified", "mediaAvailabilityVerified", "mediaScope", "preservationMatched", "releaseReady", "scope" }, document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("batch", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("legacyId", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void MissingDuplicateOrContradictoryFamiliesCannotMatch()
    {
        Assert.False(ReconciliationReport.RedactSnapshot([Brand("acc-abad")]).PreservationMatched);
        Assert.False(ReconciliationReport.RedactSnapshot([Brand("acc-abad"), Brand("acc-abad"), Brand("zikotech")]).PreservationMatched);
        var changed = Brand("acc-abad") with { OriginalArchiveRecords = 1 }; Assert.False(ReconciliationReport.RedactSnapshot([changed, Brand("kuala"), Brand("zikotech")]).PreservationMatched);
        changed = Brand("acc-abad") with { Projection = Brand("acc-abad").Projection with { MissingMappings = 1 } }; Assert.False(changed.PreservationMatched);
        Assert.Throws<MigrationRejectedException>(() => ReconciliationReport.RedactSnapshot([Brand("acc-abad") with { SourceRows = -1 }]));
        Assert.Throws<MigrationRejectedException>(() => ReconciliationReport.RedactSnapshot([Brand("acc-abad") with { TypedOrders = long.MaxValue, TypedPayments = long.MaxValue }]));
    }
    [Fact]
    public void FormatterCopiesReasonCountsInsteadOfRetainingMutableCallerState()
    {
        var reasons = new Dictionary<string, int> { ["missing_checkout_snapshot"] = 1 }; var brand = Brand("acc-abad") with { OriginalRecords = 3, OriginalArchiveRecords = 3, QuarantinedRecords = 1, QuarantineReasons = reasons };
        var report = ReconciliationReport.RedactSnapshot([brand]); reasons["missing_checkout_snapshot"] = 2; Assert.Equal(1, report.Brands[0].QuarantineReasons["missing_checkout_snapshot"]);
    }
}
