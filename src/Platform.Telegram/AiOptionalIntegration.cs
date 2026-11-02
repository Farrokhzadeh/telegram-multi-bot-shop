using System.Collections.Concurrent;
using Platform.Core;
using Platform.Security;

namespace Platform.Telegram;

public sealed class AiOptionalIntegration
{
    private readonly Func<AiTurn, CancellationToken, Task<AiAdvice>>? client;
    private readonly TimeSpan timeout, circuitDuration;
    private readonly int threshold;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<Guid, State> states = new();
    private sealed class State { public bool Busy; public int Failures; public DateTimeOffset OpenUntil; }
    public AiOptionalIntegration(Func<AiTurn, CancellationToken, Task<AiAdvice>>? client, TimeSpan timeout, TimeSpan circuitDuration, int failureThreshold, TimeProvider? clock = null)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromSeconds(30) || circuitDuration < TimeSpan.FromMilliseconds(1) || circuitDuration > TimeSpan.FromHours(1) || failureThreshold is < 1 or > 100) throw new ArgumentException("Invalid optional AI policy.");
        this.client = client; this.timeout = timeout; this.circuitDuration = circuitDuration; threshold = failureThreshold; this.clock = clock ?? TimeProvider.System;
    }
    public async Task<AiOptionalResult> SuggestAsync(BrandContext brand, bool enabled, AiShopContext context, string question, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!enabled) return Human(AiFallback.Disabled);
        if (client is null) return Human(AiFallback.Unavailable);
        if (context.Version != 1 || context.BrandId != brand.BrandId || string.IsNullOrWhiteSpace(question) || question.Length > 4096) return Human(AiFallback.InvalidContext);
        var state = states.GetOrAdd(brand.BrandId, _ => new());
        lock (state)
        {
            if (clock.GetUtcNow() < state.OpenUntil) return Human(AiFallback.CircuitOpen);
            if (state.Busy) return Human(AiFallback.Busy);
            state.Busy = true;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<AiAdvice> task = Task.Run(() => client(new(context, question), cancellation.Token), CancellationToken.None);
        try
        {
            var advice = await task.WaitAsync(timeout, ct);
            if (advice is null || advice.Version != 1 || string.IsNullOrWhiteSpace(advice.Text) || advice.Text.Length > 2048 || advice.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            { Fail(state); return Human(AiFallback.InvalidReply); }
            lock (state) { state.Failures = 0; state.OpenUntil = default; }
            return advice.HumanRequested ? Human(AiFallback.Requested) : new(AiDisposition.Advice, advice.Text, AiFallback.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancellation.Cancel(); throw; }
        catch (TimeoutException) { cancellation.Cancel(); Fail(state); return Human(AiFallback.Timeout); }
        catch (Exception) { Fail(state); return Human(AiFallback.Unavailable); }
        finally
        {
            if (task.IsCompleted) { _ = task.Exception; cancellation.Dispose(); lock (state) state.Busy = false; }
            else _ = ReleaseAsync(task, cancellation, state);
        }
    }
    private void Fail(State state)
    { lock (state) { state.Failures = Math.Min(threshold, state.Failures + 1); if (state.Failures >= threshold) state.OpenUntil = clock.GetUtcNow() + circuitDuration; } }
    private static async Task ReleaseAsync(Task<AiAdvice> task, CancellationTokenSource cancellation, State state)
    {
        try { await task; } catch (Exception) { }
        finally { cancellation.Dispose(); lock (state) state.Busy = false; }
    }
    private static AiOptionalResult Human(AiFallback reason) => new(AiDisposition.HumanSupport, null, reason);
}
