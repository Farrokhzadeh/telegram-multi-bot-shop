using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class WorkflowActivityTests
{
    [Fact]
    public async Task ActualUiAbandonmentPreservesFinancialStateAndReplay() => await WorkflowActivityQualification.AbandonmentAsync(Ct);
    [Fact]
    public async Task TargetImportPreservesOriginalIdentityClockCutoffAndConflictingReplay() => await TargetActivityImportQualification.RunAsync(Ct);
    [Fact]
    public async Task TargetImportFailureRestartsAtomicallyWithoutPartialHistory() => await TargetActivityImportQualification.AtomicAsync(Ct);
    [Fact]
    public async Task TargetImportRejectsUnboundedUnprovenFutureAndWrongDestinationRows() => await TargetActivityImportQualification.BoundsAsync(Ct);
    [Fact]
    public async Task PaymentHistoryExpiryPreservesIndependentReplayAndEveryDomainTable() => await PaymentActivityQualification.RetentionAsync(Ct);
    [Fact]
    public async Task PaymentAndTopupEventsFollowRealThreeBrandTransitionsAndReplay() => await PaymentActivityQualification.TopupAsync(Ct);
    [Fact]
    public async Task ReviewAndAuditedDenialRemainDistinctFromSettlement() => await PaymentActivityQualification.ReviewAndBankAsync(Ct);
    [Fact]
    public async Task PaymentActivityFailureRollsBackTopupCreditAndSettlement() => await PaymentActivityQualification.RollbackAsync(Ct);
    [Fact]
    public async Task SignificantCheckoutFailuresHaveFiniteScopedPrivateDataFreeActivity() => await WorkflowActivityQualification.CheckoutErrorsAsync(Ct);
    [Fact]
    public async Task FundingEventCommitFailureRollsBackSettlementAndAllocationTogether() => await WorkflowActivityQualification.FundingRollbackAsync(Ct);
    [Fact]
    public async Task ActualFundingReplacementAndReplayPreserveOriginalPaymentAuthority() => await WorkflowActivityQualification.CheckoutStatesAsync(Ct);
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
