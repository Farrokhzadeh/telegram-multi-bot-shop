using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class WorkflowActivityTests
{
    [Fact]
    public async Task ActualTargetBackupRestorePreservesReplayMoneyAndOriginalRetentionClock() => await TargetRestoreQualification.RunAsync(Ct);
    [Fact]
    public async Task VerifiedNumericIngressPreservesBrandScopeReplayAndSupportBoundary() => await WorkflowActivityQualification.QuantityAsync(Ct);
    [Fact]
    public async Task InvalidQuantityRecordsOnlyFiniteTypedRejection() => await WorkflowActivityQualification.QuantityErrorsAsync(Ct);
    [Fact]
    public async Task QuantityEventCommitFailureRollsBackFlowAndReplayCheckpoint() => await WorkflowActivityQualification.QuantityRollbackAsync(Ct);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ActualCheckoutAndQuantityEventsPreserveOriginalClocksAndFinancialReplay() => await WorkflowActivityQualification.CheckoutAsync(Ct);
    [Fact]
    public async Task EventCommitFailureRollsBackOrderMoneyAndAllocationAtomically() => await WorkflowActivityQualification.AtomicAsync(Ct);
    [Fact]
    public async Task ActualFlowExpiryIsConcurrentIdempotentAndNeverRetimestampsOldOccurrence() => await WorkflowActivityQualification.FlowAsync(Ct);
    [Fact]
    public async Task DomainFencedCleanupPreservesAllOtherTablesAndBlocksUnprovenRows() => await WorkflowActivityQualification.RetentionAsync(Ct);
}
