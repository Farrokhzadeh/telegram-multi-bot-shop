using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed class PaymentMessageWorker(PaymentInstructionStore store, TimeSpan timeout, TimeSpan spacing)
{
    public async Task RunAsync(BrandContext context, JobLease lease, Func<PaymentMessageDispatch, CancellationToken, Task<PaymentMessageOutcome>> transport, CancellationToken ct = default)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!await store.BeginAsync(context, lease, spacing, ct)) return;
        var dispatch = await store.ReadForSendAsync(context, lease, ct);
        if (dispatch is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => transport(dispatch, cancellation.Token), CancellationToken.None);
        PaymentMessageOutcome outcome; bool timely = false;
        try { outcome = await task.WaitAsync(timeout, ct); timely = true; }
        catch (Exception) { _ = CancelAsync(cancellation); outcome = new(PaymentMessageOutcomeKind.Unknown); }
        try { await store.RecordAsync(context, lease, outcome, CancellationToken.None); }
        finally { if (timely) cancellation.Dispose(); else _ = ObserveAsync(task, cancellation, store, context, lease); }
        ct.ThrowIfCancellationRequested();
    }
    private static async Task CancelAsync(CancellationTokenSource cancellation)
    { try { await cancellation.CancelAsync(); } catch (Exception) { } }
    private static async Task ObserveAsync(Task<PaymentMessageOutcome> task, CancellationTokenSource cancellation, PaymentInstructionStore store, BrandContext context, JobLease lease)
    {
        try { var late = await task; if (late.Kind == PaymentMessageOutcomeKind.Sent) await store.RecordAsync(context, lease, late, CancellationToken.None); }
        catch (Exception) { }
        finally { cancellation.Dispose(); }
    }
}
