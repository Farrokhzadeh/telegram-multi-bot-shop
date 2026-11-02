namespace Platform.Persistence;

public sealed record BotActivityBacklog(long Expired, long Blocked, DateTime? Oldest);
public sealed record BotActivityCleanup(int Deleted, BotActivityBacklog Backlog);
public sealed record BotActivityMaintenanceMetrics(long Batches, long Failed, long Deleted);
public sealed class BotActivityRetentionStore(DbSessionFactory sessions)
{
    private long batches, failed, deleted;
    public BotActivityMaintenanceMetrics Metrics => new(Interlocked.Read(ref batches), Interlocked.Read(ref failed), Interlocked.Read(ref deleted));
    private const string Covered = """
        (shop.activity_payment_origin(e) OR COALESCE(shop.telegram_update_seen(b.telegram_update_highwater,b.telegram_update_window,e.telegram_update_id),false)
        OR (e.action_code IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') AND EXISTS(
            SELECT 1 FROM shop.orders o WHERE o.id=e.order_id AND o.user_id=e.user_id AND o.product_id=e.product_id AND o.quantity=e.quantity_selected
                AND CASE e.action_code WHEN 'checkout_created' THEN o.created_at=e.occurred_at
                    WHEN 'checkout_funded' THEN o.payment_status IN ('paid','not_required') AND o.payment_confirmed_at=e.occurred_at
                    WHEN 'checkout_expired' THEN o.resolution_kind='unpaid_expiry' AND o.resolution_at=e.occurred_at
                    WHEN 'checkout_replaced' THEN o.resolution_kind='replaced' AND o.resolution_at=e.occurred_at ELSE false END))
        OR COALESCE(e.action_code='flow_expired' AND e.flow_revision IS NOT NULL AND u.flow_revision>e.flow_revision,false)
        OR (e.action_code IN ('quantity_enter','checkout_rejected') AND e.source_kind='flow'
            AND e.outcome_code=CASE WHEN e.action_code='quantity_enter' THEN 'accepted' ELSE 'rejected' END AND e.product_id IS NOT NULL
            AND e.quantity_selected IS NOT NULL AND e.order_id IS NULL AND e.support_ticket_id IS NULL AND e.flow_revision IS NULL
            AND e.telegram_update_id IS NULL AND e.idempotency_key ~ ('^workflow:'||e.action_code||':[0-9a-f]{32}$'))
        OR (e.action_code='discounts_open' AND e.source_kind='flow' AND e.outcome_code IN ('accepted','rejected') AND e.product_id IS NULL
            AND e.order_id IS NULL AND e.support_ticket_id IS NULL AND e.quantity_selected IS NULL AND e.flow_revision IS NULL
            AND e.telegram_update_id IS NULL AND e.idempotency_key ~ '^coupon_lookup:[0-9a-f]{32}$'))
        """;
    public async Task<BotActivityCleanup> CleanAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            await using var session = await sessions.BeginAsync(ct);
            int count = await session.ExecuteAsync(new DbStatement($"""
                WITH batch AS MATERIALIZED (
                    SELECT e.id FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id JOIN shop.brands b ON b.id=u.brand_id
                    WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours' AND {Covered}
                    ORDER BY e.occurred_at,e.id LIMIT $1 FOR UPDATE OF e SKIP LOCKED)
                DELETE FROM shop.bot_activity_events e USING batch WHERE e.id=batch.id
                """, DbValue.Integer(limit)), ct);
            var backlog = await BacklogAsync(session, ct);
            await session.CommitAsync(ct);
            Interlocked.Increment(ref batches); Interlocked.Add(ref deleted, count);
            return new(count, backlog);
        }
        catch { Interlocked.Increment(ref failed); throw; }
    }
    public async Task<BotActivityBacklog> BacklogAsync(CancellationToken ct = default)
    { await using var session = await sessions.BeginAsync(ct); return await BacklogAsync(session, ct); }
    private static Task<BotActivityBacklog> BacklogAsync(DbSession session, CancellationToken ct) => session.QuerySingleAsync(new DbStatement($"""
        SELECT count(*),count(*) FILTER (WHERE NOT {Covered}),min(e.occurred_at)
        FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id JOIN shop.brands b ON b.id=u.brand_id
        WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'
        """), r => new BotActivityBacklog(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetUtc(2)), ct);
}
