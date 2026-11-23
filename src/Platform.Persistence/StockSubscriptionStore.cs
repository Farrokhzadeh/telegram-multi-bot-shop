using Platform.Security;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

namespace Platform.Persistence;

public sealed record RestockObservation(Guid ProductId, long ProductRevision, long ListingRevision, DateTime ObservedAt, bool OutOfStock);
public static class StockSubscriptionStore
{
    public static async Task<Guid> SubscribeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid membershipId,
        RestockObservation observation, CancellationToken ct = default)
    {
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, membershipId, ct);
        if (member.Blocked || member.Banned || !observation.OutOfStock || observation.ProductId == Guid.Empty || observation.ProductRevision < 0 || observation.ListingRevision < 0) Deny(session);
        await session.AdmitCustomerWriterAsync(context, verifiedActorId, observation.ProductId, ct);
        _ = DbValue.Utc(observation.ObservedAt);
        Guid consent = await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireMembershipAsync(context, verifiedActorId, false, false, token);
            await data.LockListingAsync(context.BrandId, observation.ProductId, token, true);
            bool eligible = await (from product in data.Data.Set<Entities.Product>()
                                   join listing in data.Data.Set<Entities.BrandProduct>() on product.Id equals listing.ProductId
                                   where product.Id == observation.ProductId && listing.BrandId == context.BrandId && product.Active && listing.Visible &&
                                       product.Revision == observation.ProductRevision && listing.Revision == observation.ListingRevision && observation.ObservedAt <= EfValues.UtcNow() && observation.ObservedAt >= EfValues.UtcNow().AddSeconds(-30)
                                   select product.Id).AnyAsync(token);
            if (!eligible) data.RejectScope("Restock consent is unavailable.");
            var existing = await data.Data.Set<Entities.ProductStockSubscription>().SingleOrDefaultAsync(value => value.UserId == member.Id && value.ProductId == observation.ProductId && value.ClosedAt == null, token);
            if (existing is not null) return existing.Id;
            var consent = new Entities.ProductStockSubscription { Id = Guid.NewGuid(), UserId = member.Id, ProductId = observation.ProductId };
            data.Data.Add(consent);
            return consent.Id;
        }, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, observation.ProductId), consent, DateTime.UtcNow, ct);
        return consent;
    }

    public static async Task<Guid?> UnsubscribeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid membershipId,
        Guid productId, CancellationToken ct = default)
    {
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, membershipId, ct);
        if (member.Blocked || member.Banned) Deny(session);
        await session.AdmitCustomerWriterAsync(context, verifiedActorId, productId, ct);
        return await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireMembershipAsync(context, verifiedActorId, false, false, token);
            await data.LockSubscriptionAsync(member.Id, productId, token);
            var consent = await data.Data.Set<Entities.ProductStockSubscription>().SingleOrDefaultAsync(value => value.UserId == member.Id && value.ProductId == productId && value.ClosedAt == null, token);
            if (consent is null) return (Guid?)null;
            await data.LockSubscriptionDeliveriesAsync(consent.Id, token);
            var deliveries = await data.Data.Set<Entities.TelegramDelivery>().Where(value => value.ProductStockSubscriptionId == consent.Id).Take(2).ToArrayAsync(token);
            if (deliveries.Length > 1 || (consent.TriggeredAt != null && deliveries.Length == 0) || deliveries.Any(value => value.SentAt != null || value.TelegramMessageId != null || value.Status is "leased" or "uncertain" or "succeeded"))
                data.RejectScope("Restock consent is unavailable.");
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            foreach (var delivery in deliveries)
            {
                await data.LockDeliveryJobsAsync(delivery.Id, token);
                var jobs = await data.Data.Set<Entities.Job>().Where(value => value.TelegramDeliveryId == delivery.Id).OrderBy(value => value.Id).Take(1001).ToArrayAsync(token);
                if (jobs.Length > 1000 || jobs.Any(value => value.Status is "leased" or "succeeded")) data.RejectScope("Restock consent is unavailable.");
                int count = 0;
                foreach (var job in jobs)
                {
                    if (job.Status is not ("pending" or "retry" or "failed")) continue;
                    job.Status = "cancelled"; job.LockedBy = null; job.LeaseUntil = null; job.CompletedAt ??= now;
                    if (++count % 128 == 0) await data.FlushAsync(token, false);
                }
                delivery.Status = "cancelled";
            }
            consent.CancelledAt = now; consent.ClosedAt = now;
            return (Guid?)consent.Id;
        }, ct);
    }
    private static void Deny(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Restock consent is unavailable."); }
}
