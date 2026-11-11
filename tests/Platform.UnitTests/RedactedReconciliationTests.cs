using System.Text.Json;
using Platform.Migration;

namespace Platform.UnitTests;

public sealed class RedactedReconciliationTests
{
    private static MigrationReconciliation Fixture() => new(Guid.NewGuid(), 7, 7, 0, 0, 12345.6789m, 12345.6789m, 12345.6789m, 0, 1, 2, 3, 4, 5, 6, true);
    [Fact]
    public void PublicJsonContainsOnlyAllowlistedCountsAndFlags()
    {
        var source = Fixture();
        string json = JsonSerializer.Serialize(ReconciliationReport.Redact(source), MigrationReportJsonContext.Default.RedactedMigrationReconciliation);
        using var document = JsonDocument.Parse(json);
        string[] expected = ["expectedMappings", "committedMappings", "missingMappings", "mismatchedMappings", "unexpectedMappings", "openingPostings", "nonPostingHistory", "paidUnfulfilledOrders", "heldCouponUnits", "targetCiphertexts", "saleableInventory", "walletAmountsMatched", "walletProjectionMatched", "scope", "financialObligationsVerified", "releaseReady"];
        Assert.Equal(expected.Order(), document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(source.Batch.ToString(), json);
        Assert.DoesNotContain("12345.6789", json);
        Assert.True(document.RootElement.GetProperty("walletAmountsMatched").GetBoolean());
        Assert.True(document.RootElement.GetProperty("walletProjectionMatched").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("paidUnfulfilledOrders").GetInt64());
        Assert.Equal(4, document.RootElement.GetProperty("heldCouponUnits").GetInt64());
        Assert.Equal(5, document.RootElement.GetProperty("targetCiphertexts").GetInt64());
    }
    [Fact]
    public void ContradictoryMoneyCannotMatchDespiteCompleteFlag()
    {
        var source = Fixture();
        foreach (var report in new[] { source with { OpeningUsd = 99 }, source with { TargetWalletUsd = 99 }, source with { ExpectedOpeningUsd = 99 }, source with { WalletDifferenceUsd = 99 } })
        {
            var result = ReconciliationReport.Redact(report);
            Assert.False(result.WalletAmountsMatched);
            Assert.False(result.WalletProjectionMatched);
        }
    }
    [Fact]
    public void MappingGapsAndIncompleteEvidenceCannotMatch()
    {
        var source = Fixture();
        foreach (var report in new[] { source with { CommittedMappings = 8 }, source with { MissingMappings = 1 }, source with { MismatchedMappings = 1 }, source with { UnexpectedMappings = 1 }, source with { Complete = false } })
        {
            var result = ReconciliationReport.Redact(report);
            Assert.True(result.WalletAmountsMatched);
            Assert.False(result.WalletProjectionMatched);
        }
    }
    [Fact]
    public void NegativeCountsAreRejected()
    {
        var source = Fixture();
        foreach (var report in new[] { source with { ExpectedMappings = -1 }, source with { CommittedMappings = -1 }, source with { MissingMappings = -1 }, source with { MismatchedMappings = -1 }, source with { UnexpectedMappings = -1 }, source with { OpeningPostings = -1 }, source with { NonPostingHistory = -1 }, source with { PaidUnfulfilledOrders = -1 }, source with { HeldCouponUnits = -1 }, source with { TargetCiphertexts = -1 }, source with { SaleableInventory = -1 } })
            Assert.Throws<MigrationRejectedException>(() => ReconciliationReport.Redact(report));
    }
    [Fact]
    public void MatchedProjectionDoesNotQualifyFinancialObligationsOrRelease()
    {
        var result = ReconciliationReport.Redact(Fixture() with { PaidUnfulfilledOrders = 0, HeldCouponUnits = 0 });
        Assert.True(result.WalletProjectionMatched);
        Assert.False(result.FinancialObligationsVerified);
        Assert.False(result.ReleaseReady);
        Assert.Equal("qualified_wallet_projection_only", result.Scope);
    }
    [Fact]
    public void NullReportIsRejected() => Assert.Throws<ArgumentNullException>(() => ReconciliationReport.Redact(null!));
}
