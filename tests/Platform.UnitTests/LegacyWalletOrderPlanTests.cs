using Platform.Testing;
namespace Platform.UnitTests;

public sealed class LegacyWalletOrderPlanTests
{
    [Theory]
    [InlineData("zero_valid")]
    [InlineData("zero_missing")]
    [InlineData("zero_owner")]
    [InlineData("zero_currency")]
    [InlineData("zero_clock")]
    [InlineData("zero_prepared")]
    [InlineData("valid")]
    [InlineData("terms")]
    [InlineData("owner")]
    [InlineData("debit")]
    [InlineData("method")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wallet")]
    [InlineData("retention")]
    [InlineData("cipher")]
    public Task CompleteArchiveAndExactNativeDebitAreRequiredBeforeSelectingTypedOrders(string fault) => LegacyWalletOrderPlanQualification.RunAsync(fault, TestContext.Current.CancellationToken);
}
