using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public sealed class PaymentVerificationWorker(DbSessionFactory sessions, PaymentSettlementStore settlement, NowPaymentsClient provider)
{
    public async Task<PaymentSettlement?> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.VerifyPayment || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid payment) throw new UnauthorizedAccessException();
        await using (var renew = await sessions.BeginAsync(ct))
        { await OutboxStore.RenewAsync(renew, lease, TimeSpan.FromSeconds(60), ct); await renew.CommitAsync(ct); }
        PaymentSettlement? result;
        try { result = await settlement.SettleAsync(context, payment, provider.LookupAsync, ct); }
        catch (PaymentNoticeRejectedException) { result = null; }
        await using var complete = await sessions.BeginAsync(ct);
        if (result is null || result.Outcome == PaymentSettlementOutcome.Pending) await OutboxStore.RetryAsync(complete, lease, TimeSpan.FromSeconds(30), 100, JobError.Transient, ct);
        else await OutboxStore.CompleteAsync(complete, lease, ct);
        await complete.CommitAsync(ct);
        return result;
    }
}
