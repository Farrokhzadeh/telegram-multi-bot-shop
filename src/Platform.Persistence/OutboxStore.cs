using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;

namespace Platform.Persistence;

public enum JobError { Transient, Busy, InvalidOrigin, RemoteOutcomeUnknown, AttemptLimit }

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
public static class OutboxStore
{
    private const string Columns = "j.id,j.brand_id,j.job_kind,coalesce(j.payment_id,j.order_id,j.fulfillment_id,j.campaign_id,j.discount_id,j.support_message_id,j.telegram_delivery_id,j.product_stock_subscription_id,j.supplier_request_id,j.product_id,j.product_supplier_source_id),j.audit_event_id,j.origin_revision,j.attempts,j.lease_until";
    private const string Fence = " WHERE j.id=$1 AND j.status='leased' AND j.locked_by=$2 AND j.attempts=$3 AND j.lease_until>clock_timestamp() AND (j.origin_revision IS NULL OR EXISTS (SELECT 1 FROM shop.supplier_requests r WHERE r.id=j.supplier_request_id AND r.revision=j.origin_revision))";

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
        Guid? existing = await session.QuerySingleAsync(new DbStatement("SELECT " + Columns +
            " FROM (SELECT 1) singleton LEFT JOIN shop.jobs j ON j.dedupe_key=$1", DbValue.Text(dedupe)), row =>
        {
            if (row.IsDBNull(0)) return (Guid?)null;
            if (ReadIntent(row) != intent) throw new InvalidOperationException("Job identity conflicts with its immutable origin.");
            return row.GetGuid(0);
        }, cancellationToken);
        if (existing.HasValue) return existing.Value;
        Guid id = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.jobs(id,brand_id,job_kind,payment_id,order_id,campaign_id,discount_id,support_message_id,
                telegram_delivery_id,supplier_request_id,product_id,product_supplier_source_id,audit_event_id,origin_revision,dedupe_key,available_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """, DbValue.Uuid(id), Uuid(intent.BrandId), DbValue.Text(intent.Code), Origin(JobOrigin.Payment), Origin(JobOrigin.Order),
            Origin(JobOrigin.Campaign), Origin(JobOrigin.Discount), Origin(JobOrigin.Support), Origin(JobOrigin.Telegram),
            Origin(JobOrigin.Supplier), Origin(JobOrigin.Product), Origin(JobOrigin.Source), Uuid(intent.AuditId),
            intent.OriginRevision.HasValue ? DbValue.Bigint(intent.OriginRevision.Value) : DbValue.Null(NpgsqlDbType.Bigint),
            DbValue.Text(dedupe), DbValue.Utc(availableAt)), cancellationToken);
        return id;

        DbValue Origin(JobOrigin origin) => Uuid(intent.Origin == origin ? intent.OriginId : null);
    }

    public static Task<JobLease?> ClaimAsync(DbSession session, JobKind kind, Guid worker, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        string code = JobIntent.CodeOf(kind);
        bool recover = kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier or JobKind.SendTelegram);
        return session.QuerySingleAsync<JobLease?>(new DbStatement("""
            WITH candidate AS (
                SELECT j.id FROM shop.jobs j
                WHERE j.job_kind=$1 AND j.available_at<=clock_timestamp() AND j.attempts<9223372036854775807
                  AND (j.status IN ('pending','retry') OR ($4 AND j.status='leased' AND j.lease_until<=clock_timestamp()))
                  AND ($1<>'submit_supplier' OR EXISTS (SELECT 1 FROM shop.supplier_requests r WHERE r.id=j.supplier_request_id AND r.status='pending'))
                  AND (j.origin_revision IS NULL OR EXISTS (SELECT 1 FROM shop.supplier_requests r WHERE r.id=j.supplier_request_id AND r.revision=j.origin_revision))
                ORDER BY j.available_at,j.id LIMIT 1 FOR UPDATE OF j SKIP LOCKED
            ), claimed AS (
                UPDATE shop.jobs j SET status='leased',locked_by=$2,lease_until=clock_timestamp()+$3*INTERVAL '1 second',attempts=j.attempts+1
                FROM candidate c WHERE j.id=c.id RETURNING j.*
            )
            """ + " SELECT " + Columns + " FROM (SELECT 1) singleton LEFT JOIN claimed j ON true",
            DbValue.Text(code), DbValue.Text(worker.ToString("D")), DbValue.Integer(Seconds(duration)), DbValue.Boolean(recover)),
            row => row.IsDBNull(0) ? null : new JobLease(row.GetGuid(0), ReadIntent(row), worker, row.GetInt64(6), row.GetUtc(7)), cancellationToken);
    }

    public static async Task<JobLease> RenewAsync(DbSession session, JobLease lease, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await session.ExecuteLeaseFencedAsync(new DbStatement(
            "UPDATE shop.jobs j SET lease_until=clock_timestamp()+$4*INTERVAL '1 second'" + Fence,
            DbValue.Uuid(lease.Id), DbValue.Text(lease.Worker.ToString("D")), DbValue.Bigint(lease.Attempt), DbValue.Integer(Seconds(duration))), cancellationToken);
        DateTime until = await session.QuerySingleAsync(new DbStatement("SELECT lease_until FROM shop.jobs WHERE id=$1", DbValue.Uuid(lease.Id)),
            row => row.GetUtc(0), cancellationToken);
        return new JobLease(lease.Id, lease.Intent, lease.Worker, lease.Attempt, until);
    }

    public static Task CompleteAsync(DbSession session, JobLease lease, CancellationToken cancellationToken = default) =>
        FinishAsync(session, lease, "succeeded", null, cancellationToken);

    public static Task FailAsync(DbSession session, JobLease lease, JobError error, CancellationToken cancellationToken = default) =>
        FinishAsync(session, lease, "failed", ErrorCode(error), cancellationToken);

    public static Task RetryAsync(DbSession session, JobLease lease, TimeSpan delay, int maximumAttempts, JobError error,
        CancellationToken cancellationToken = default)
    {
        if (lease.Intent.RequiresExplicitRecovery || error == JobError.RemoteOutcomeUnknown)
            throw new InvalidOperationException("Remote-effect work requires domain recovery authorization.");
        if (error is not (JobError.Transient or JobError.Busy)) throw new ArgumentException("Only known transient local failures may retry.");
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        string status = lease.Attempt >= maximumAttempts ? "failed" : "retry";
        string code = status == "failed" ? ErrorCode(JobError.AttemptLimit) : ErrorCode(error);
        return session.ExecuteLeaseFencedAsync(new DbStatement("""
            UPDATE shop.jobs j SET status=$4,locked_by=NULL,lease_until=NULL,
                available_at=clock_timestamp()+$5*INTERVAL '1 second',
                completed_at=CASE WHEN $4='failed' THEN clock_timestamp() ELSE NULL END,last_error_code=$6
            """ + Fence, DbValue.Uuid(lease.Id), DbValue.Text(lease.Worker.ToString("D")), DbValue.Bigint(lease.Attempt),
            DbValue.Text(status), DbValue.Integer(Seconds(delay)), DbValue.Text(code)), cancellationToken);
    }

    private static Task FinishAsync(DbSession session, JobLease lease, string status, string? code, CancellationToken cancellationToken) =>
        session.ExecuteLeaseFencedAsync(new DbStatement("""
            UPDATE shop.jobs j SET status=$4,locked_by=NULL,lease_until=NULL,completed_at=clock_timestamp(),last_error_code=$5
            """ + Fence, DbValue.Uuid(lease.Id), DbValue.Text(lease.Worker.ToString("D")), DbValue.Bigint(lease.Attempt),
            DbValue.Text(status), code is null ? DbValue.Null(NpgsqlDbType.Text) : DbValue.Text(code)), cancellationToken);

    private static JobIntent ReadIntent(DbRow row) => JobIntent.Read(row.GetString(2),
        row.IsDBNull(1) ? null : row.GetGuid(1), row.IsDBNull(3) ? null : row.GetGuid(3),
        row.IsDBNull(4) ? null : row.GetGuid(4), row.IsDBNull(5) ? null : row.GetInt64(5));

    private static DbValue Uuid(Guid? value) => value.HasValue ? DbValue.Uuid(value.Value) : DbValue.Null(NpgsqlDbType.Uuid);
    private static int Seconds(TimeSpan value) => value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromMinutes(5) && value.Ticks % TimeSpan.TicksPerSecond == 0
        ? checked((int)value.TotalSeconds) : throw new ArgumentOutOfRangeException(nameof(value), "Duration requires 1–300 whole seconds.");
    private static string ErrorCode(JobError error) => error switch
    {
        JobError.Transient => "transient", JobError.Busy => "busy", JobError.InvalidOrigin => "invalid_origin",
        JobError.RemoteOutcomeUnknown => "remote_outcome_unknown", JobError.AttemptLimit => "attempt_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };
}
