using Platform.Testing;
namespace Platform.UnitTests;

public sealed class LegacyReadOnlyOrderHistoryTests
{
    [Fact]
    public Task UnknownOriginalTermsStayReadOnlyPrivateAndInTheirOriginalCurrency() => LegacyReadOnlyOrderQualification.RunAsync(TestContext.Current.CancellationToken);
}
