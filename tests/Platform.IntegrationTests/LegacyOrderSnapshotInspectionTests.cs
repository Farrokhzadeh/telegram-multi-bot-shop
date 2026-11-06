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
    public Task SnapshotInspectionPreservesSourceAndReportsOnlyQualifiedTerms(string fault) => LegacyOrderSnapshotQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
