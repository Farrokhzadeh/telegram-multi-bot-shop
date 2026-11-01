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
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), r => r.GetInt64(0), ct);
            BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            if (data.Member != member.Id || data.Method != "bank" || member.Blocked || member.Banned) throw new UnauthorizedAccessException("Receipt is unavailable.");
            await PaymentSettlementStore.LockAsync(session, context, data, ct);
            var receipt = await session.QuerySingleAsync(new DbStatement("SELECT receipt_asset_ref,clock_timestamp() FROM shop.payments WHERE id=$1", DbValue.Uuid(payment)), r => (Asset: r.IsDBNull(0) ? null : r.GetString(0), Now: r.GetUtc(1)), ct);
            if (receipt.Asset is not null)
            {
                if (receipt.Asset != protectedReceipt) throw new InvalidOperationException("Receipt identity conflict.");
                return ManualDepositOutcome.Replayed;
            }
            data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            if (data.Status == "settled" || data.OrderStatus is "paid" or "not_required") throw new InvalidOperationException("Payment already accepted.");
            bool timely = data.OrderStatus == "awaiting_payment" && receipt.Now <= data.Deadline;
            await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET receipt_asset_ref=$1,first_payment_observed_at=COALESCE(first_payment_observed_at,$2),status=$3 WHERE id=$4",
                DbValue.Text(protectedReceipt), DbValue.Utc(receipt.Now), DbValue.Text(timely ? "observed" : "review"), DbValue.Uuid(payment)), ct);
            if (timely) await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='under_review',revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(data.Order!.Value)), ct);
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
            var receipt = await session.QuerySingleAsync(new DbStatement("SELECT receipt_asset_ref FROM shop.payments WHERE id=$1", DbValue.Uuid(payment)), r => r.IsDBNull(0) ? null : r.GetString(0), ct);
            long revision = await session.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.orders WHERE id=$1", DbValue.Uuid(data.Order ?? Guid.Empty)), r => r.GetInt64(0), ct);
            if (data.Method != "bank" || receipt is null || revision != expectedOrderRevision || data.Status == "settled" || data.OrderStatus != "under_review" || data.First > data.Deadline)
                throw new InvalidOperationException("Receipt is not eligible for this decision.");
            await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.payments SET status=$1,review_admin_id=$2,reviewed_at=clock_timestamp(),review_reason=$3,
                    amount_received=CASE WHEN $4 THEN amount_due ELSE amount_received END,confirmed_at=CASE WHEN $4 THEN clock_timestamp() ELSE confirmed_at END WHERE id=$5
                """, DbValue.Text(approve ? "settled" : "rejected"), DbValue.Uuid(actor.AdminId), DbValue.Text(reason), DbValue.Boolean(approve), DbValue.Uuid(payment)), ct);
            if (approve)
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='paid',payment_confirmed_at=clock_timestamp(),revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(data.Order!.Value)), ct);
                await settlement.DispatchAsync(session, context, data, ct);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE order_id=$1 AND job_kind='expire_order_payment' AND status IN ('pending','retry')", DbValue.Uuid(data.Order.Value)), ct);
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
            var order = await session.QuerySingleAsync(new DbStatement("SELECT payment_status,fulfillment_status,revision FROM shop.orders WHERE id=$1", DbValue.Uuid(data.Order ?? Guid.Empty)), r => (Payment: r.GetString(0), Fulfillment: r.GetString(1), Revision: r.GetInt64(2)), ct);
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
