using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Entities = Platform.Persistence.Entities;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public enum ManualDepositOutcome { Submitted, DeniedUnverified, ConfirmedPaid, Replayed, LateReview }
public sealed class ManualDepositService(DbSessionFactory sessions, PaymentSettlementStore settlement)
{
    public async Task<ManualDepositOutcome> SubmitAsync(BrandContext context, long actor, Guid payment, string protectedReceipt, CancellationToken ct = default)
    {
        if (!protectedReceipt.StartsWith("protected:", StringComparison.Ordinal) || protectedReceipt.Length is < 12 or > 1024 || protectedReceipt.Any(char.IsControl)) throw new ArgumentException("Invalid protected receipt.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, actor, token), ct);
            BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            if (data.Member != member.Id || data.Method != "bank" || member.Blocked || member.Banned) throw new UnauthorizedAccessException("Receipt is unavailable.");
            await PaymentSettlementStore.LockAsync(session, context, data, ct);
            var receipt = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Payment>().Where(value => value.Id == payment).Select(value => new { Asset = value.ReceiptAssetRef, Now = EfValues.UtcNow() }).SingleAsync(token), ct);
            if (receipt.Asset is not null)
            {
                if (receipt.Asset != protectedReceipt) throw new InvalidOperationException("Receipt identity conflict.");
                return ManualDepositOutcome.Replayed;
            }
            data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            if (data.Status == "settled" || data.OrderStatus is "paid" or "not_required") throw new InvalidOperationException("Payment already accepted.");
            bool timely = data.OrderStatus == "awaiting_payment" && receipt.Now <= data.Deadline;
            await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequirePaymentCommandAsync(context, payment, token);
                var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == payment, token);
                row.ReceiptAssetRef = protectedReceipt; row.FirstPaymentObservedAt ??= receipt.Now; row.Status = timely ? "observed" : "review";
                if (timely)
                {
                    var order = await ef.Data.Set<Entities.Order>().SingleAsync(value => value.Id == data.Order, token);
                    order.PaymentStatus = "under_review"; order.Revision++; order.UpdatedAt = await ef.BrandClockAsync(context.BrandId, token);
                }
                return true;
            }, ct);
            await session.CommitAsync(ct);
            return timely ? ManualDepositOutcome.Submitted : ManualDepositOutcome.LateReview;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public async Task<ManualDepositOutcome> DecideAsync(BrandContext context, AuthenticatedAdmin actor, Guid payment, bool approve, long expectedOrderRevision,
        Guid command, string reason, CancellationToken ct = default)
    {
        Validate(reason, expectedOrderRevision);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"receipt-decision-v1:{payment:D}:{approve}:{expectedOrderRevision}:{reason}"));
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.Payment, payment, context.BrandId, AuditReason.OperatorReview, command, hash), ct))
                return ManualDepositOutcome.Replayed;
            var before = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            await PaymentSettlementStore.LockAsync(session, context, before, ct);
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            var receipt = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Payment>().Where(value => value.Id == payment).Select(value => value.ReceiptAssetRef).SingleAsync(token), ct);
            long revision = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Order>().Where(value => value.Id == data.Order).Select(value => value.Revision).SingleAsync(token), ct);
            if (data.Method != "bank" || receipt is null || revision != expectedOrderRevision || data.Status == "settled" || data.OrderStatus != "under_review" || data.First > data.Deadline)
                throw new InvalidOperationException("Receipt is not eligible for this decision.");
            await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequireAdminAsync(actor, null, token); await ef.RequirePaymentCommandAsync(context, payment, token);
                var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == payment, token);
                DateTime now = await ef.BrandClockAsync(context.BrandId, token);
                row.Status = approve ? "settled" : "rejected"; row.ReviewAdminId = actor.AdminId; row.ReviewedAt = now; row.ReviewReason = reason;
                if (approve) { row.AmountReceived = row.AmountDue; row.ConfirmedAt = now; }
                return true;
            }, ct);
            if (approve)
            {
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequireOrderCommandAsync(context, data.Order!.Value, token);
                    var order = await ef.Data.Set<Entities.Order>().SingleAsync(value => value.Id == data.Order, token);
                    DateTime now = await ef.BrandClockAsync(context.BrandId, token);
                    order.PaymentStatus = "paid"; order.PaymentConfirmedAt = now; order.Revision++; order.UpdatedAt = now; return true;
                }, ct);
                await settlement.DispatchAsync(session, context, data, ct);
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequireOrderCommandAsync(context, data.Order!.Value, token); await ef.LockExpiryJobsAsync(data.Order.Value, token);
                    var jobs = await ef.Data.Set<Entities.Job>().Where(value => value.OrderId == data.Order && value.JobKind == "expire_order_payment" && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
                    if (jobs.Length > 1000) ef.RejectScope("Payment expiry job bound exceeded.");
                    DateTime now = await ef.BrandClockAsync(context.BrandId, token);
                    foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = now; }
                    return true;
                }, ct);
                await PaymentSettlementStore.NoticeAsync(session, context, data, ct);
            }
            await session.CommitAsync(ct);
            return approve ? ManualDepositOutcome.ConfirmedPaid : ManualDepositOutcome.DeniedUnverified;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public async Task ReviewPaidUnfulfilledAsync(BrandContext context, AuthenticatedAdmin actor, Guid payment, long expectedOrderRevision,
        Guid command, string reason, CancellationToken ct = default)
    {
        Validate(reason, expectedOrderRevision);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"off-bot-review-v1:{payment:D}:{expectedOrderRevision}:{reason}"));
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.Payment, payment, context.BrandId, AuditReason.Reconciliation, command, hash), ct)) return;
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            await PaymentSettlementStore.LockAsync(session, context, data, ct);
            var order = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Order>().Where(value => value.Id == data.Order).Select(value => new { Payment = value.PaymentStatus, Fulfillment = value.FulfillmentStatus, value.Revision }).SingleAsync(token), ct);
            if (data.Method != "bank" || data.Status != "settled" || order.Payment != "paid" || order.Fulfillment is not ("waiting_stock" or "requires_admin") || order.Revision != expectedOrderRevision)
                throw new InvalidOperationException("Paid obligation is unavailable for review.");
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static void Validate(string reason, long revision)
    {
        if (revision < 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || reason.Any(char.IsControl)) throw new ArgumentException("Invalid receipt decision.");
    }
}
