using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BotActivityRegistryTests
{
    [Fact]
    public async Task VersionedFiniteRegistryMatchesTheTypedMembershipScopedBaseline() => await BotActivityQualification.RunAsync(TestContext.Current.CancellationToken);
}
