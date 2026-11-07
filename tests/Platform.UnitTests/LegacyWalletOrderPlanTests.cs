using Platform.Testing;
namespace Platform.UnitTests;

public sealed class LegacyWalletOrderPlanTests
{
    [Theory]
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
