namespace Platform.Persistence;

public sealed record BotActivityBacklog(long Expired, long Blocked, DateTime? Oldest);
public sealed record BotActivityCleanup(int Deleted, BotActivityBacklog Backlog);
public sealed record BotActivityMaintenanceMetrics(long Batches, long Failed, long Deleted);
public sealed class BotActivityRetentionStore(DbSessionFactory sessions)
{
    private long batches, failed, deleted;
    public BotActivityMaintenanceMetrics Metrics => new(Interlocked.Read(ref batches), Interlocked.Read(ref failed), Interlocked.Read(ref deleted));
    public async Task<BotActivityCleanup> CleanAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            await using var session = await sessions.BeginAsync(ct);
            int count = await session.ExecuteAsync(new DbStatement("""
                WITH batch AS MATERIALIZED (
                    SELECT event_id AS id FROM shop.activity_cleanup_candidates($1))
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
    private static Task<BotActivityBacklog> BacklogAsync(DbSession session, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("""
        SELECT count(*),count(*) FILTER (WHERE NOT shop.activity_retention_covered(e.id)),min(e.occurred_at)
        FROM shop.bot_activity_events e
        WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'
        """), r => new BotActivityBacklog(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetUtc(2)), ct);
}
