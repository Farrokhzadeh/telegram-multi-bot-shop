using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LegacyOrderSnapshotInspectionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("links")]
    [InlineData("schema")]
    [InlineData("lines")]
    [InlineData("commitment")]
    [InlineData("count")]
    [InlineData("data")]
    public Task SnapshotInspectionPreservesSourceAndReportsOnlyQualifiedTerms(string fault) => LegacyOrderSnapshotQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
