using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BotActivityRuntimeTests
{
    [Fact]
    public async Task IndependentReplayCheckpointSurvivesAnalyticsDeletionAndRollback() => await BotActivityRuntimeQualification.ReplayAsync(TestContext.Current.CancellationToken);
    [Fact]
    public async Task BoundedCleanupRetainsEqualityAndUnqualifiedRowsWithoutDomainChanges() => await BotActivityRuntimeQualification.RetentionAsync(TestContext.Current.CancellationToken);
}
