using Microsoft.Data.Sqlite;

namespace Platform.Persistence;

public static partial class OutboxStore
{
    internal static Task<Guid> EnqueueSqliteAsync(SqliteTransaction transaction, JobIntent intent, Guid deduplicationId, DateTime availableAt, CancellationToken ct)
    {
        if (deduplicationId == Guid.Empty) throw new ArgumentException("Job deduplication identity is required.");
        return SqliteSessionFactory.StepEfAsync(transaction, true, (data, token) => OutboxQueries.EnqueueAsync(data, intent, intent.Code + ":" + deduplicationId.ToString("D"), availableAt, token), ct);
    }
    internal static Task<JobLease?> ClaimSqliteAsync(SqliteTransaction transaction, Guid? brand, JobKind kind, Guid worker, TimeSpan duration, CancellationToken ct)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        return SqliteSessionFactory.StepEfAsync(transaction, true, (data, token) => data.ClaimJobAsync(brand, true, false, kind, worker, Seconds(duration), token), ct);
    }
    internal static Task<JobLease> RenewSqliteAsync(SqliteTransaction transaction, JobLease lease, TimeSpan duration, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(transaction, true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, true, token); row.LeaseUntil = DateTime.UtcNow.AddSeconds(Seconds(duration)); return new JobLease(lease.Id, lease.Intent, lease.Worker, lease.Attempt, row.LeaseUntil.Value); }, ct);
    internal static Task CompleteSqliteAsync(SqliteTransaction transaction, JobLease lease, CancellationToken ct) => FinishSqliteAsync(transaction, lease, "succeeded", null, ct);
    internal static Task FailSqliteAsync(SqliteTransaction transaction, JobLease lease, JobError error, CancellationToken ct) => FinishSqliteAsync(transaction, lease, "failed", ErrorCode(error), ct);
    internal static Task RetrySqliteAsync(SqliteTransaction transaction, JobLease lease, TimeSpan delay, int maximumAttempts, JobError error, CancellationToken ct)
    {
        if (lease.Intent.RequiresExplicitRecovery || error == JobError.RemoteOutcomeUnknown)
            throw new InvalidOperationException("Remote-effect work requires domain recovery authorization.");
        if (error is not (JobError.Transient or JobError.Busy)) throw new ArgumentException("Only known transient local failures may retry.");
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        bool exhausted = lease.Attempt >= maximumAttempts;
        return SqliteSessionFactory.StepEfAsync(transaction, true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, true, token); DateTime now = DateTime.UtcNow; row.Status = exhausted ? "failed" : "retry"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now.AddSeconds(Seconds(delay)); row.CompletedAt = exhausted ? now : null; row.LastErrorCode = ErrorCode(exhausted ? JobError.AttemptLimit : error); return true; }, ct);
    }
    private static Task FinishSqliteAsync(SqliteTransaction transaction, JobLease lease, string status, string? code, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(transaction, true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, true, token); row.Status = status; row.LockedBy = null; row.LeaseUntil = null; row.CompletedAt = DateTime.UtcNow; row.LastErrorCode = code; return true; }, ct);
}
