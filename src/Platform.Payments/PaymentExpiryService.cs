using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Payments;

public enum PaymentExpiryOutcome { Expired, Protected, NotDue, Terminal }
public sealed class PaymentExpiryService(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<PaymentExpiryOutcome> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.ExpireOrderPayment || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid order) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            Guid payment = await session.QuerySingleAsync(new DbStatement("SELECT p.id FROM shop.payments p JOIN shop.orders o ON o.id=p.order_id JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE o.id=$1 AND b.id=$2 AND b.bot_id=$3", DbValue.Uuid(order), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetGuid(0), ct);
            var before = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            await PaymentSettlementStore.LockAsync(session, context, before, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            var proof = await session.QuerySingleAsync(new DbStatement("SELECT receipt_asset_ref IS NOT NULL,clock_timestamp() FROM shop.payments WHERE id=$1", DbValue.Uuid(payment)), r => (Receipt: r.GetBoolean(0), Now: r.GetUtc(1)), ct);
            bool evidence = proof.Receipt || data.First is not null || data.Status is not ("created" or "awaiting");
            PaymentExpiryOutcome outcome;
            if (data.OrderStatus is not ("awaiting_payment" or "under_review" or "confirmation_pending")) outcome = PaymentExpiryOutcome.Terminal;
            else if (data.OrderStatus != "awaiting_payment" || evidence) outcome = PaymentExpiryOutcome.Protected;
            else if (!PaymentDeadlinePolicy.CanExpire(data.OrderStatus, data.Deadline!.Value, proof.Now, false))
            {
                int delay = Math.Clamp(checked((int)Math.Ceiling((data.Deadline.Value - proof.Now).TotalSeconds)) + 1, 1, 1801);
                await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(delay), int.MaxValue, JobError.Busy, ct);
                await session.CommitAsync(ct); return PaymentExpiryOutcome.NotDue;
            }
            else
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='expired',fulfillment_status='cancelled',resolution_kind='unpaid_expiry',resolution_at=clock_timestamp(),revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(order)), ct);
                Guid? held = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
                if (held is Guid discount) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, discount, order, false, ct);
                await PaymentInstructionStore.EnqueueAsync(session, context, order, "expired", ct);
                outcome = PaymentExpiryOutcome.Expired;
            }
            await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return outcome;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
