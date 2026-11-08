using Platform.Testing;
using Platform.Migration;
namespace Platform.UnitTests;

public sealed class LegacyArchiveFilesTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("race")]
    [InlineData("zero")]
    [InlineData("missing")]
    [InlineData("shared")]
    [InlineData("root")]
    [InlineData("symlink")]
    [InlineData("scope")]
    [InlineData("key")]
    [InlineData("source")]
    [InlineData("cipher")]
    [InlineData("field")]
    [InlineData("duplicate")]
    public Task ProtectedArchivesReloadAndRejectChangedEvidence(string fault) => LegacyArchiveFilesQualification.RunAsync(fault, TestContext.Current.CancellationToken);
    [Fact]
    public void ArchiveCandidatesExcludeSaleableStockAndNonprojectedRows()
    {
        Assert.Null(LegacySourceRecord.CopyCandidate("bot_activity_events", "1", "{}"));
        Assert.Null(LegacySourceRecord.CopyCandidate("inventory", "1", "{\"id\":1,\"order_id\":null}"));
        Assert.Equal(LegacyProjection.InventoryUnit, LegacySourceRecord.CopyCandidate("inventory", "1", "{\"id\":1,\"order_id\":\"original-order\"}")!.Projection);
        Assert.Null(LegacySourceRecord.CopyCandidate("transactions", "1", "{\"id\":\"1\",\"amount\":1}"));
        Assert.Equal(LegacyProjection.ZeroTransaction, LegacySourceRecord.CopyCandidate("transactions", "1", "{\"id\":\"1\",\"amount\":0}")!.Projection);
        Assert.Null(LegacySourceRecord.CopyCandidate("products", "1", "{\"id\":1,\"active\":false,\"is_removed\":false}"));
        Assert.Throws<MigrationRejectedException>(() => LegacySourceRecord.CopyCandidate("orders", "1", "{\"id\":\"changed\"}"));
    }

}
