using Platform.Testing;
namespace Platform.IntegrationTests;

public sealed class LegacyWalletOrderImportTests
{
    [Theory]
    [InlineData("topup_valid")]
    [InlineData("topup_paid")]
    [InlineData("topup_payment")]
    [InlineData("topup_tampered")]
    [InlineData("zero_valid")]
    [InlineData("zero_source")]
    [InlineData("zero_register")]
    [InlineData("zero_prepared")]
    [InlineData("valid")]
    [InlineData("source")]
    [InlineData("unselected")]
    [InlineData("wallet")]
    [InlineData("register")]
    [InlineData("archive")]
    public Task TypedWalletOrdersRequireCompletePreflightAndReplayWithoutAnotherDebit(string fault) => LegacyWalletOrderImportQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
