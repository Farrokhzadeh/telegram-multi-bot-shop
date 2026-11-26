using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Entities = Platform.Persistence.Entities;
using Platform.Core;
using Platform.Persistence;

namespace Platform.Payments;

public enum PaymentExpiryOutcome { Expired, Protected, NotDue, Terminal }
public sealed class PaymentExpiryService(DbSessionFactory sessions)
{
    public async Task<PaymentExpiryOutcome> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.ExpireOrderPayment || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid order) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            Guid payment = await session.RunEfStepAsync(false, (data, token) => (from value in data.Data.Set<Entities.Payment>() join source in data.Data.Set<Entities.Order>() on value.OrderId equals source.Id join user in data.Data.Set<Entities.User>() on source.UserId equals user.Id join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id where source.Id == order && brand.Id == context.BrandId && brand.BotId == context.BotId select value.Id).SingleAsync(token), ct);
            var before = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            await PaymentSettlementStore.LockAsync(session, context, before, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
            var data = await PaymentSettlementStore.LoadAsync(session, context, payment, ct);
            var proof = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Payment>().Where(value => value.Id == payment).Select(value => new { Receipt = value.ReceiptAssetRef != null, Now = EfValues.UtcNow() }).SingleAsync(token), ct);
            bool evidence = proof.Receipt || data.First is not null || data.Status is not ("created" or "awaiting");
            PaymentExpiryOutcome outcome;
            if (data.OrderStatus is not ("awaiting_payment" or "under_review" or "confirmation_pending")) outcome = PaymentExpiryOutcome.Terminal;
            else if (data.OrderStatus != "awaiting_payment" || evidence) outcome = PaymentExpiryOutcome.Protected;
            else if (!PaymentDeadlinePolicy.CanExpire(data.OrderStatus, data.Deadline!.Value, proof.Now, false))
            {
                int delay = Math.Clamp(checked((int)Math.Ceiling((data.Deadline.Value - proof.Now).TotalSeconds)) + 1, 1, 300);
                await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(delay), int.MaxValue, JobError.Busy, ct);
                await session.CommitAsync(ct); return PaymentExpiryOutcome.NotDue;
            }
            else
            {
                await session.RunEfStepAsync(true, (data, token) => PaymentStateQueries.ExpireAsync(data, context, order, proof.Now, token), ct);
                Guid? held = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().Where(value => value.OrderId == order && value.Status == "held").Select(value => (Guid?)value.DiscountId).SingleOrDefaultAsync(token), ct);
                if (held is Guid discount) await new PricingStore(sessions).TransitionClaimAsync(session, context, discount, order, false, ct);
                await PaymentInstructionStore.EnqueueAsync(session, context, order, "expired", ct);
                outcome = PaymentExpiryOutcome.Expired;
            }
            await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return outcome;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
