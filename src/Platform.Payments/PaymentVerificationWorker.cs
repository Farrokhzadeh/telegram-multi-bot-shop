using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public sealed class PaymentVerificationWorker(DbSessionFactory sessions, PaymentSettlementStore settlement, NowPaymentsClient provider,
    Func<BrandContext, Guid, long?, CancellationToken, Task>? resumeArchive = null)
{
    public Task<PaymentSettlement?> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default) => RunCoreAsync(context, lease, null, ct);
    public Task<PaymentSettlement?> RunForBrandAsync(BrandContext context, long expectedRevision, JobLease lease, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return RunCoreAsync(context, lease, expectedRevision, ct);
    }
    private async Task<PaymentSettlement?> RunCoreAsync(BrandContext context, JobLease lease, long? writerRevision, CancellationToken ct)
    {
        if (lease.Intent.Kind != JobKind.VerifyPayment || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid payment) throw new UnauthorizedAccessException();
        await using (var renew = await sessions.BeginAsync(ct))
        {
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(renew, context, revision, ct);
            await OutboxStore.RenewAsync(renew, lease, TimeSpan.FromSeconds(60), ct);
            await renew.CommitAsync(ct);
        }
        PaymentSettlement? result;
        try
        {
            if (resumeArchive is not null) await resumeArchive(context, payment, writerRevision, ct);
            result = writerRevision is long expected
                ? await settlement.SettleForBrandAsync(context, expected, payment, provider.LookupAsync, ct)
                : await settlement.SettleAsync(context, payment, provider.LookupAsync, ct);
        }
        catch (Exception error) when (error is PaymentNoticeRejectedException or IOException or TimeoutException or HttpRequestException) { result = null; }
        await using var complete = await sessions.BeginAsync(ct);
        if (writerRevision is long current) await BrandWriterStore.RequireAsync(complete, context, current, ct);
        if (result is null || result.Outcome == PaymentSettlementOutcome.Pending) await OutboxStore.RetryAsync(complete, lease, TimeSpan.FromSeconds(30), 100, JobError.Transient, ct);
        else await OutboxStore.CompleteAsync(complete, lease, ct);
        await complete.CommitAsync(ct);
        return result;
    }
}
