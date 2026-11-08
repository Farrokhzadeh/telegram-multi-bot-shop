using Platform.Testing;
namespace Platform.IntegrationTests;

public sealed class LegacyWalletOrderImportTests
{
    [Theory]
    [InlineData("direct_bank_valid")]
    [InlineData("direct_crypto_valid")]
    [InlineData("direct_bank_owner")]
    [InlineData("direct_bank_amount")]
    [InlineData("direct_bank_status")]
    [InlineData("direct_crypto_status")]
    [InlineData("direct_bank_source")]
    [InlineData("direct_crypto_tampered")]
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
