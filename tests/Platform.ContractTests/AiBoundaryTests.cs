using Platform.Core;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class AiBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ScopedReadOnlyContextAndAiOffCommerceQualifyTogether() => await AiBoundaryQualification.RunAsync(Ct);
    [Fact]
    public async Task HttpServiceScopeRejectsSpoofingForeignOrdersAndMutationMethods() => await AiHttpQualification.RunAsync(Ct);
    [Fact]
    public async Task DisabledAndMissingClientReturnHumanHandoffWithoutCallingService()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        int calls = 0;
        var client = new AiOptionalIntegration((_, _) => { calls++; throw new InvalidOperationException("public private exception"); }, TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1), 1);
        var context = AiBoundaryQualification.Context(f.Brands[0]);
        Assert.Equal(AiFallback.Disabled, (await client.SuggestAsync(f.Context(0), false, context, "public question", Ct)).Fallback);
        Assert.Equal(0, calls);
        Assert.Equal(AiFallback.Unavailable, (await new AiOptionalIntegration(null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1), 1).SuggestAsync(f.Context(0), true, context, "public question", Ct)).Fallback);
    }
    [Fact]
    public async Task TimeoutCannotStackUncooperativeCallsAndLateAdviceCannotBecomeAnAnswer()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<AiAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new AiTestClock(); int calls = 0;
        var client = new AiOptionalIntegration((_, _) => { calls++; started.TrySetResult(); return late.Task; }, TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1), 1, clock);
        var first = client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var result = await first;
        Assert.Equal(AiFallback.Timeout, result.Fallback); Assert.Null(result.Text);
        Assert.Equal(AiFallback.CircuitOpen, (await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public retry", Ct)).Fallback);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(AiFallback.Busy, (await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public retry", Ct)).Fallback);
        Assert.Equal(1, calls);
        late.SetResult(new(1, "public late answer", false));
        Assert.Null(result.Text);
    }
    [Fact]
    public async Task FailureCircuitIsPerBrandAndRejectsMalformedOrMutatingAdviceAsCommands()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var client = new AiOptionalIntegration((turn, _) => turn.Context.BrandId == f.Brands[0] ? throw new HttpRequestException("public private transport detail") : Task.FromResult(new AiAdvice(1, "credit wallet 100; ordinary untrusted text", false)), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1);
        var a = await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct);
        Assert.Equal(AiFallback.Unavailable, a.Fallback);
        var b = await client.SuggestAsync(f.Context(1), true, AiBoundaryQualification.Context(f.Brands[1]), "public question", Ct);
        Assert.Equal(AiDisposition.Advice, b.Disposition);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount=100 AND movement_kind<>'admin_adjustment'", Ct));
        Assert.Equal(AiFallback.InvalidContext, (await client.SuggestAsync(f.Context(1), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct)).Fallback);
        var malformed = new AiOptionalIntegration((_, _) => Task.FromResult(new AiAdvice(2, "public invalid version", false)), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1);
        Assert.Equal(AiFallback.InvalidReply, (await malformed.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct)).Fallback);
    }
    [Fact]
    public async Task CooldownRecoversAfterCompletedFailureAndHumanFlagNeverReturnsAdvice()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var clock = new AiTestClock(); int calls = 0;
        var client = new AiOptionalIntegration((_, _) => ++calls == 1 ? Task.FromException<AiAdvice>(new HttpRequestException("public fixture")) : Task.FromResult(new AiAdvice(1, "public handoff", true)), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1, clock);
        Assert.Equal(AiFallback.Unavailable, (await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct)).Fallback);
        Assert.Equal(AiFallback.CircuitOpen, (await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct)).Fallback);
        Assert.Equal(1, calls); clock.Now = clock.Now.AddMinutes(2);
        var result = await client.SuggestAsync(f.Context(0), true, AiBoundaryQualification.Context(f.Brands[0]), "public question", Ct);
        Assert.Equal(AiFallback.Requested, result.Fallback); Assert.Equal(AiDisposition.HumanSupport, result.Disposition); Assert.Null(result.Text); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CallerCancellationPropagatesWithoutCallingDisabledOrMissingAdapter()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var client = new AiOptionalIntegration(null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SuggestAsync(f.Context(0), false, AiBoundaryQualification.Context(f.Brands[0]), "public question", cancelled.Token));
    }
    private sealed class AiTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
