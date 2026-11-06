using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class WalletBatchTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("clock")]
    [InlineData("clock-source")]
    [InlineData("schema")]
    [InlineData("missing")]
    [InlineData("source")]
    [InlineData("namespace")]
    [InlineData("register")]
    [InlineData("retention-boundary")]
    [InlineData("rollback")]
    public Task FrozenBulkWalletPlansRejectChangedInputsAndReconcileReplay(string fault) => WalletBatchQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
