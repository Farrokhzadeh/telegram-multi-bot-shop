using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class SourceCopyInspectionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("row")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("schema")]
    [InlineData("index")]
    [InlineData("privilege")]
    [InlineData("table")]
    [InlineData("key")]
    [InlineData("count")]
    [InlineData("commitment")]
    [InlineData("role")]
    public Task ReadOnlySourceCopyRequiresExactSchemaCoverageAndIndependentCommitments(string fault) => SourceCopyQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
