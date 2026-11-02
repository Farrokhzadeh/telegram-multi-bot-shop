using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CouponRevisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ActualOwnerOverridePreservesProtectedOriginalOrdersAndTypedRevisionContext() => await CouponRevisionQualification.OverrideAsync(Ct);
    [Fact]
    public async Task ConcurrentRedemptionAndEditCaptureOneSerializedActualContext() => await CouponRevisionQualification.RaceAsync(Ct);
    [Fact]
    public async Task DeferredRevisionFailureRollsBackCouponAuditAndSettingsWithoutAHistoryGap() => await CouponRevisionQualification.RollbackAsync(Ct);
}
