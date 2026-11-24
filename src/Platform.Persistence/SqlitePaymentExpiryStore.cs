using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public enum SqlitePaymentExpiryOutcome { Expired, Protected, NotDue, Terminal }

public sealed class SqlitePaymentExpiryStore(SqliteSessionFactory sessions)
{
    public Task<JobLease?> ClaimAsync(BrandContext context, Guid worker, CancellationToken ct = default) =>
        sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx, context.BrandId,
            JobKind.ExpireOrderPayment, worker, TimeSpan.FromSeconds(60), token), ct);

    public Task<SqlitePaymentExpiryOutcome> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.ExpireOrderPayment || lease.Intent.BrandId != context.BrandId ||
            lease.Intent.OriginId is not Guid order || order == Guid.Empty)
            throw new UnauthorizedAccessException("Payment expiry lease is unavailable.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(60), token);
            var target = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) =>
                (from source in data.Data.Set<Entities.Order>()
                 join payment in data.Data.Set<Entities.Payment>() on source.Id equals payment.OrderId
                 join user in data.Data.Set<Entities.User>() on source.UserId equals user.Id
                 join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
                 where source.Id == order && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active
                 select new { payment.Status, payment.FirstPaymentObservedAt, payment.ReceiptAssetRef, OrderStatus = source.PaymentStatus, Deadline = source.PaymentDueAt }).SingleOrDefaultAsync(cancel), token);
            if (target is null) throw new UnauthorizedAccessException("Payment expiry target is unavailable.");
            string status = target.Status, orderStatus = target.OrderStatus;
            bool evidence = target.FirstPaymentObservedAt != null || target.ReceiptAssetRef != null || status is not ("created" or "awaiting");
            if (orderStatus is not ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return SqlitePaymentExpiryOutcome.Terminal;
            }
            if (orderStatus != "awaiting_payment" || evidence)
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return SqlitePaymentExpiryOutcome.Protected;
            }
            DateTime now = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.BrandClockAsync(context.BrandId, cancel), token);
            DateTime deadline = target.Deadline ?? throw new InvalidOperationException("Payment deadline is unavailable.");
            if (now <= deadline)
            {
                int delay = Math.Clamp(checked((int)Math.Ceiling((deadline - now).TotalSeconds)) + 1, 1, 300);
                await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(delay),
                    int.MaxValue, JobError.Busy, token);
                return SqlitePaymentExpiryOutcome.NotDue;
            }
            await SqliteSessionFactory.StepEfAsync(tx, true, (data, cancel) => PaymentStateQueries.ExpireAsync(data, context, order, now, cancel), token);
            await SqliteDiscountClaimStore.TransitionAsync(tx, context, order, false, token);
            Guid delivery = await SqliteSessionFactory.StepEfAsync(tx, true, (data, cancel) => PaymentInstructionQueries.EnqueueAsync(data, context, order, "expired", cancel), token);
            await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery), delivery, now, token);
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return SqlitePaymentExpiryOutcome.Expired;
        }, ct);
    }
}
