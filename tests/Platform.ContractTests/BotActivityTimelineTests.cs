using Platform.Testing;

namespace Platform.ContractTests;

public sealed class BotActivityTimelineTests
{
    [Fact]
    public async Task ScopedCursorTimelineRequiresCurrentIndependentAdminAndRedactsSourceData() => await BotActivityRuntimeQualification.TimelineAsync(TestContext.Current.CancellationToken);
}
