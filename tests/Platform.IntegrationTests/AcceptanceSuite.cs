using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class AcceptanceSuite
{
    [Fact]
    public async Task OneDisposableRunRetainsMoneyStockDeliveryAndOriginalSupplierIntentAcrossRestarts() =>
        await AcceptanceQualification.RunAsync(ct: TestContext.Current.CancellationToken);
}
