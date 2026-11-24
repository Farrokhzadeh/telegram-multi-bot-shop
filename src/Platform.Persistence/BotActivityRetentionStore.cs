using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

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
            await session.AdmitSharedWriterAsync(ct);
            int count = await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireJobCommandAsync(null, token);
                DateTime cutoff = await CutoffAsync(data, token); DateTime? at = null; Guid? after = null; int removed = 0;
                while (removed < limit)
                {
                    var rows = await data.Data.Set<Entities.BotActivityEvent>().AsNoTracking().Where(row => row.OccurredAt < cutoff && (at == null || row.OccurredAt > at || row.OccurredAt == at && row.Id.CompareTo(after!.Value) > 0)).OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Take(128).ToListAsync(token);
                    foreach (var row in rows)
                    {
                        at = row.OccurredAt; after = row.Id;
                        if (!await ActivityRetentionQueries.CoveredAsync(data, row, token) || !await data.LockActivityAsync(row.Id, token)) continue;
                        if (session.IsWriterScoped && !await (from u in data.Data.Set<Entities.User>() join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where u.Id == row.UserId && b.Active select u.Id).AnyAsync(token)) continue;
                        data.Data.Remove(row); await data.FlushAsync(token, false); removed++; if (removed == limit) break;
                    }
                    if (rows.Count < 128) break;
                }
                return removed;
            }, ct);
            var backlog = await BacklogAsync(session, ct);
            await session.CommitAsync(ct);
            Interlocked.Increment(ref batches); Interlocked.Add(ref deleted, count);
            return new(count, backlog);
        }
        catch { Interlocked.Increment(ref failed); throw; }
    }
    public async Task<BotActivityBacklog> BacklogAsync(CancellationToken ct = default)
    { await using var session = await sessions.BeginAsync(ct); return await BacklogAsync(session, ct); }
    private static Task<BotActivityBacklog> BacklogAsync(DbSession session, CancellationToken ct) => session.RunEfStepAsync(false, async (data, token) =>
    {
        DateTime cutoff = await CutoffAsync(data, token); DateTime? at = null, oldest = null; Guid? after = null; long expired = 0, blocked = 0;
        while (true)
        {
            var rows = await data.Data.Set<Entities.BotActivityEvent>().AsNoTracking().Where(row => row.OccurredAt < cutoff && (at == null || row.OccurredAt > at || row.OccurredAt == at && row.Id.CompareTo(after!.Value) > 0)).OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Take(128).ToListAsync(token);
            foreach (var row in rows) { expired++; if (await ActivityRetentionQueries.CoveredAsync(data, row, token)) oldest ??= row.OccurredAt; else blocked++; at = row.OccurredAt; after = row.Id; }
            if (rows.Count < 128) return new BotActivityBacklog(expired, blocked, oldest);
        }
    }, ct);
    private static async Task<DateTime> CutoffAsync(EfSession data, CancellationToken ct)
    {
        DateTime? clock = await data.Data.Set<Entities.Brand>().Select(row => (DateTime?)EfValues.TransactionTime()).FirstOrDefaultAsync(ct);
        return (clock ?? DateTime.UtcNow).AddHours(-4320);
    }
}
