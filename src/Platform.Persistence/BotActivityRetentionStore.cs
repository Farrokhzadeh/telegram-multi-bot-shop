namespace Platform.Persistence;

public sealed record BotActivityBacklog(long Expired, long Blocked, DateTime? Oldest);
public sealed record BotActivityCleanup(int Deleted, BotActivityBacklog Backlog);
public sealed class BotActivityRetentionStore(DbSessionFactory sessions)
{
    public async Task<BotActivityCleanup> CleanAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var session = await sessions.BeginAsync(ct);
        int count = await session.ExecuteAsync(new DbStatement("""
            WITH batch AS MATERIALIZED (
                SELECT e.id FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id JOIN shop.brands b ON b.id=u.brand_id
                WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours' AND e.telegram_update_id IS NOT NULL
                    AND shop.telegram_update_seen(b.telegram_update_highwater,b.telegram_update_window,e.telegram_update_id)
                ORDER BY e.occurred_at,e.id LIMIT $1 FOR UPDATE OF e SKIP LOCKED)
            DELETE FROM shop.bot_activity_events e USING batch WHERE e.id=batch.id
            """, DbValue.Integer(limit)), ct);
        var backlog = await BacklogAsync(session, ct);
        await session.CommitAsync(ct);
        return new(count, backlog);
    }
    public async Task<BotActivityBacklog> BacklogAsync(CancellationToken ct = default)
    { await using var session = await sessions.BeginAsync(ct); return await BacklogAsync(session, ct); }
    private static Task<BotActivityBacklog> BacklogAsync(DbSession session, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("""
        SELECT count(*),count(*) FILTER (WHERE e.telegram_update_id IS NULL OR NOT shop.telegram_update_seen(b.telegram_update_highwater,b.telegram_update_window,e.telegram_update_id)),min(e.occurred_at)
        FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id JOIN shop.brands b ON b.id=u.brand_id
        WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'
        """), r => new BotActivityBacklog(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetUtc(2)), ct);
}
