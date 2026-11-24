using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public enum JobError { Transient, Busy, InvalidOrigin, RemoteOutcomeUnknown, AttemptLimit, CredentialRecovery }

public sealed class JobLease
{
    internal JobLease(Guid id, JobIntent intent, Guid worker, long attempt, DateTime until)
    { Id = id; Intent = intent; Worker = worker; Attempt = attempt; Until = until; }
    public Guid Id { get; }
    public JobIntent Intent { get; }
    public Guid Worker { get; }
    public long Attempt { get; }
    public DateTime Until { get; }
    public bool IsRepeatAttempt => Attempt > 1;
}

/// <summary>
/// Trusted service persistence, not customer authorization or remote-effect permission. Caller owns the
/// business transaction and explicit commit. No callback, network operation, retry loop or startup DDL.
/// </summary>
public static partial class OutboxStore
{
    public static async Task<Guid> EnqueueAsync(DbSession session, JobIntent intent, Guid deduplicationId,
        DateTime availableAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intent);
        if (deduplicationId == Guid.Empty) throw new ArgumentException("Job deduplication identity is required.");
        string dedupe = intent.Code + ":" + deduplicationId.ToString("D");
        // Transaction-scoped lock serializes the logical identity even after its origin changes state.
        // Hash collisions only serialize unrelated work; UNIQUE(dedupe_key) remains authoritative.
        long lockId = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.ASCII.GetBytes(dedupe)));
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(lockId)), cancellationToken);
        return await session.RunEfStepAsync(true, (data, token) => OutboxQueries.EnqueueAsync(data, intent, dedupe, availableAt, token), cancellationToken);
    }

    public static async Task<JobLease?> ClaimAsync(DbSession session, JobKind kind, Guid worker, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Guid? brand = await session.AdmitClaimWriterAsync(false, cancellationToken);
        return await ClaimCoreAsync(session, kind, worker, duration, brand, cancellationToken);
    }

    public static async Task<JobLease?> ClaimForBrandAsync(DbSession session, BrandContext context, long expectedRevision,
        JobKind kind, Guid worker, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await BrandWriterStore.RequireAsync(session, context, expectedRevision, cancellationToken);
        return await ClaimCoreAsync(session, kind, worker, duration, context.BrandId, cancellationToken);
    }

    private static Task<JobLease?> ClaimCoreAsync(DbSession session, JobKind kind, Guid worker, TimeSpan duration,
        Guid? brandId, CancellationToken cancellationToken)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        string code = JobIntent.CodeOf(kind);
        return session.RunEfStepAsync(true, (data, token) => data.ClaimJobAsync(brandId, false, session.IsSharedWriterScoped, kind, worker, Seconds(duration), token), cancellationToken);
    }

    public static async Task<JobLease> RenewAsync(DbSession session, JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await session.AdmitLeaseWriterAsync(lease, cancellationToken);
        return await session.RunEfStepAsync(true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, false, token); row.LeaseUntil = (await data.ClockAsync(data.Data.Set<Entities.Job>().Where(value => value.Id == row.Id), token)).AddSeconds(Seconds(duration)); return new JobLease(lease.Id, lease.Intent, lease.Worker, lease.Attempt, row.LeaseUntil.Value); }, cancellationToken);
    }

    public static Task CompleteAsync(DbSession session, JobLease lease, CancellationToken cancellationToken = default) =>
        FinishAsync(session, lease, "succeeded", null, cancellationToken);

    public static Task FailAsync(DbSession session, JobLease lease, JobError error, CancellationToken cancellationToken = default) =>
        FinishAsync(session, lease, "failed", ErrorCode(error), cancellationToken);

    public static async Task RetryAsync(DbSession session, JobLease lease, TimeSpan delay, int maximumAttempts, JobError error,
        CancellationToken cancellationToken = default)
    {
        if (lease.Intent.RequiresExplicitRecovery || error == JobError.RemoteOutcomeUnknown)
            throw new InvalidOperationException("Remote-effect work requires domain recovery authorization.");
        if (error is not (JobError.Transient or JobError.Busy)) throw new ArgumentException("Only known transient local failures may retry.");
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        string status = lease.Attempt >= maximumAttempts ? "failed" : "retry";
        string code = status == "failed" ? ErrorCode(JobError.AttemptLimit) : ErrorCode(error);
        await session.AdmitLeaseWriterAsync(lease, cancellationToken);
        await session.RunEfStepAsync(true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, false, token); DateTime now = await data.ClockAsync(data.Data.Set<Entities.Job>().Where(value => value.Id == row.Id), token); row.Status = status; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now.AddSeconds(Seconds(delay)); row.CompletedAt = status == "failed" ? now : null; row.LastErrorCode = code; return true; }, cancellationToken);
    }
    private static async Task FinishAsync(DbSession session, JobLease lease, string status, string? code, CancellationToken cancellationToken)
    {
        await session.AdmitLeaseWriterAsync(lease, cancellationToken);
        await session.RunEfStepAsync(true, async (data, token) => { var row = await data.RequireJobLeaseAsync(lease, false, token); DateTime now = await data.ClockAsync(data.Data.Set<Entities.Job>().Where(value => value.Id == row.Id), token); row.Status = status; row.LockedBy = null; row.LeaseUntil = null; row.CompletedAt = now; row.LastErrorCode = code; return true; }, cancellationToken);
    }

    private static int Seconds(TimeSpan value) => value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromMinutes(5) && value.Ticks % TimeSpan.TicksPerSecond == 0
        ? checked((int)value.TotalSeconds) : throw new ArgumentOutOfRangeException(nameof(value), "Duration requires 1–300 whole seconds.");
    private static string ErrorCode(JobError error) => error switch
    {
        JobError.Transient => "transient",
        JobError.Busy => "busy",
        JobError.InvalidOrigin => "invalid_origin",
        JobError.RemoteOutcomeUnknown => "remote_outcome_unknown",
        JobError.AttemptLimit => "attempt_limit",
        JobError.CredentialRecovery => "credential_recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };
}
