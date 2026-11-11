using System.Text.Json;
using Platform.Migration;

namespace Platform.UnitTests;

public sealed class RedactedRecordReconciliationTests
{
    private static LegacyRecordReconciliation Fixture() => new(Guid.NewGuid(), 10, 10, 0, 0, 0, 0, 0, true, 0, 5, 5, 0, 2, 8, 3, 1, 0);
    [Fact]
    public void PublicJsonOmitsBatchAndIncludesOnlyScopedCounts()
    {
        var source = Fixture();
        string json = JsonSerializer.Serialize(ReconciliationReport.RedactRecords(source), MigrationReportJsonContext.Default.RedactedLegacyRecordReconciliation);
        using var document = JsonDocument.Parse(json);
        string[] expected = ["expectedRecords", "comparedRecords", "mismatchedRecords", "invalidArchives", "missingMappings", "mismatchedMappings", "unexpectedMappings", "walletMatched", "saleableInventory", "expectedEncryptedFields", "reencryptedFields", "invalidRetentions", "manualIrrAccounts", "retainedHistoryRows", "retainedIrrHistory", "retainedZeroHistory", "unexpectedActivityEvents", "projectionMatched", "scope", "financialObligationsVerified", "releaseReady"];
        Assert.Equal(expected.Order(), document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(source.Batch.ToString(), json);
        Assert.True(document.RootElement.GetProperty("projectionMatched").GetBoolean());
        Assert.False(document.RootElement.GetProperty("financialObligationsVerified").GetBoolean());
        Assert.False(document.RootElement.GetProperty("releaseReady").GetBoolean());
    }
    [Fact]
    public void DifferencesPreventProjectionMatch()
    {
        var source = Fixture();
        foreach (var report in new[] { source with { ComparedRecords = 9 }, source with { MismatchedRecords = 1 }, source with { InvalidArchives = 1 }, source with { MissingMappings = 1 }, source with { MismatchedMappings = 1 }, source with { UnexpectedMappings = 1 }, source with { WalletMatched = false }, source with { SaleableInventory = 1 }, source with { ReencryptedFields = 4 }, source with { InvalidRetentions = 1 }, source with { UnexpectedActivityEvents = 1 } })
            Assert.False(ReconciliationReport.RedactRecords(report).ProjectionMatched);
    }
    [Fact]
    public void NegativeCountsAndNullAreRejected()
    {
        var source = Fixture();
        foreach (var report in new[] { source with { ExpectedRecords = -1 }, source with { ComparedRecords = -1 }, source with { MismatchedRecords = -1 }, source with { InvalidArchives = -1 }, source with { MissingMappings = -1 }, source with { MismatchedMappings = -1 }, source with { UnexpectedMappings = -1 }, source with { SaleableInventory = -1 }, source with { ExpectedEncryptedFields = -1 }, source with { ReencryptedFields = -1 }, source with { InvalidRetentions = -1 }, source with { ManualIrrAccounts = -1 }, source with { RetainedHistoryRows = -1 }, source with { RetainedIrrHistory = -1 }, source with { RetainedZeroHistory = -1 }, source with { UnexpectedActivityEvents = -1 } })
            Assert.Throws<MigrationRejectedException>(() => ReconciliationReport.RedactRecords(report));
        Assert.Throws<ArgumentNullException>(() => ReconciliationReport.RedactRecords(null!));
    }
}
