using Platform.Security;

namespace Platform.Persistence;

public sealed record RestockObservation(Guid ProductId, long ProductRevision, long ListingRevision, DateTime ObservedAt, bool OutOfStock);
public static class StockSubscriptionStore
{
    public static async Task<Guid> SubscribeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid membershipId,
        RestockObservation observation, CancellationToken ct = default)
    {
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, membershipId, ct);
        if (member.Blocked || member.Banned || !observation.OutOfStock || observation.ProductId == Guid.Empty || observation.ProductRevision < 0 || observation.ListingRevision < 0) Deny(session);
        bool eligible = await session.QuerySingleAsync(new DbStatement("""
            WITH listing AS (SELECT p.id FROM shop.products p JOIN shop.brand_products b ON b.product_id=p.id
                WHERE p.id=$1 AND b.brand_id=$2 AND p.active AND b.visible AND p.revision=$3 AND b.revision=$4
                    AND $5<=clock_timestamp() AND $5>=clock_timestamp()-INTERVAL '30 seconds' FOR SHARE OF p,b)
            SELECT EXISTS(SELECT 1 FROM listing)
            """, DbValue.Uuid(observation.ProductId), DbValue.Uuid(context.BrandId), DbValue.Bigint(observation.ProductRevision),
            DbValue.Bigint(observation.ListingRevision), DbValue.Utc(observation.ObservedAt)), row => row.GetBoolean(0), ct);
        if (!eligible) Deny(session);
        Guid consent = await session.QuerySingleAsync(new DbStatement("""
            INSERT INTO shop.product_stock_subscriptions(id,user_id,product_id) VALUES ($1,$2,$3)
            ON CONFLICT (user_id,product_id) WHERE closed_at IS NULL DO UPDATE SET product_id=EXCLUDED.product_id RETURNING id
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(member.Id), DbValue.Uuid(observation.ProductId)), row => row.GetGuid(0), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, observation.ProductId), consent, DateTime.UtcNow, ct);
        return consent;
    }

    public static async Task<Guid?> UnsubscribeAsync(DbSession session, BrandContext context, long verifiedActorId, Guid membershipId,
        Guid productId, CancellationToken ct = default)
    {
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, membershipId, ct);
        if (member.Blocked || member.Banned) Deny(session);
        var consent = await session.QuerySingleAsync(new DbStatement("""
            WITH consent AS (SELECT id,triggered_at FROM shop.product_stock_subscriptions WHERE user_id=$1 AND product_id=$2 AND closed_at IS NULL FOR UPDATE)
            SELECT c.id,c.triggered_at IS NOT NULL FROM (SELECT 1) seed LEFT JOIN consent c ON true
            """, DbValue.Uuid(member.Id), DbValue.Uuid(productId)), row => row.IsDBNull(0) ? null : new Consent(row.GetGuid(0), row.GetBoolean(1)), ct);
        if (consent is null) return null;
        var deliveries = await session.QueryManyAsync(new DbStatement("SELECT id,status,sent_at IS NOT NULL OR telegram_message_id IS NOT NULL FROM shop.telegram_deliveries WHERE product_stock_subscription_id=$1 FOR UPDATE", DbValue.Uuid(consent.Id)),
            row => (Id: row.GetGuid(0), Status: row.GetString(1), Sent: row.GetBoolean(2)), 1, ct);
        if ((consent.Triggered && deliveries.Count == 0) || deliveries.Any(d => d.Sent || d.Status is "leased" or "uncertain" or "succeeded")) Deny(session);
        foreach (var delivery in deliveries)
        {
            var jobs = await session.QueryManyAsync(new DbStatement("SELECT id,status FROM shop.jobs WHERE telegram_delivery_id=$1 ORDER BY id FOR UPDATE", DbValue.Uuid(delivery.Id)),
                row => (Id: row.GetGuid(0), Status: row.GetString(1)), 1000, ct);
            if (jobs.Any(j => j.Status is "leased" or "succeeded")) Deny(session);
            foreach (var job in jobs)
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',locked_by=NULL,lease_until=NULL,completed_at=COALESCE(completed_at,clock_timestamp()) WHERE id=$1 AND status IN ('pending','retry','failed')", DbValue.Uuid(job.Id)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='cancelled' WHERE id=$1", DbValue.Uuid(delivery.Id)), ct);
        }
        await session.ExecuteAsync(new DbStatement("UPDATE shop.product_stock_subscriptions SET cancelled_at=clock_timestamp(),closed_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(consent.Id)), ct);
        return consent.Id;
    }
    private static void Deny(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Restock consent is unavailable."); }
    private sealed record Consent(Guid Id, bool Triggered);
}
