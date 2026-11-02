using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AiIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ScopedReadsLeaveCommerceUnchangedAndAiOffSupportWorks() => await AiBoundaryQualification.RunAsync(Ct);
    [Fact]
    public async Task IndependentServiceCannotChooseMembersOrMutateCommerce() => await AiHttpQualification.RunAsync(Ct);
}
