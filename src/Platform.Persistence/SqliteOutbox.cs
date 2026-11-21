using Microsoft.Data.Sqlite;

namespace Platform.Persistence;

public static partial class OutboxStore
{
    private const string SqliteFence = """
         WHERE j.id=$id AND j.brand_id IS $brand AND j.status='leased' AND j.locked_by=$worker
         AND j.attempts=$attempt AND j.lease_until>$now AND
         (j.origin_revision IS NULL OR EXISTS (SELECT 1 FROM supplier_requests r WHERE r.id=j.supplier_request_id AND r.revision=j.origin_revision))
        """;

    internal static async Task<Guid> EnqueueSqliteAsync(SqliteTransaction transaction, JobIntent intent, Guid deduplicationId, DateTime availableAt, CancellationToken ct)
    {
        if (deduplicationId == Guid.Empty) throw new ArgumentException("Job deduplication identity is required.");
        string dedupe = intent.Code + ":" + deduplicationId.ToString("D");
        using (var lookup = SqliteCommand(transaction, "SELECT " + Columns + " FROM jobs j WHERE j.dedupe_key=$dedupe", ("$dedupe", dedupe)))
        await using (var row = await lookup.ExecuteReaderAsync(ct))
        {
            if (await row.ReadAsync(ct))
            {
                if (ReadSqliteIntent(row) != intent) throw new InvalidOperationException("Job identity conflicts with its immutable origin.");
                return row.GetGuid(0);
            }
        }

        Guid id = Guid.NewGuid();
        using var command = SqliteCommand(transaction, """
            INSERT INTO jobs(id,brand_id,job_kind,payment_id,order_id,campaign_id,discount_id,support_message_id,
                telegram_delivery_id,supplier_request_id,product_id,product_supplier_source_id,audit_event_id,origin_revision,dedupe_key,available_at)
            VALUES ($id,$brand,$kind,$payment,$order,$campaign,$discount,$support,$delivery,$supplier,$product,$source,$audit,$revision,$dedupe,$available)
            """, ("$id", id), ("$brand", intent.BrandId), ("$kind", intent.Code), ("$payment", Origin(JobOrigin.Payment)),
            ("$order", Origin(JobOrigin.Order)), ("$campaign", Origin(JobOrigin.Campaign)), ("$discount", Origin(JobOrigin.Discount)),
            ("$support", Origin(JobOrigin.Support)), ("$delivery", Origin(JobOrigin.Telegram)), ("$supplier", Origin(JobOrigin.Supplier)),
            ("$product", Origin(JobOrigin.Product)), ("$source", Origin(JobOrigin.Source)), ("$audit", intent.AuditId),
            ("$revision", intent.OriginRevision), ("$dedupe", dedupe), ("$available", SqliteSchema.UtcText(availableAt)));
        await command.ExecuteNonQueryAsync(ct);
        return id;

        Guid? Origin(JobOrigin origin) => intent.Origin == origin ? intent.OriginId : null;
    }

    internal static async Task<JobLease?> ClaimSqliteAsync(SqliteTransaction transaction, Guid? brand, JobKind kind, Guid worker, TimeSpan duration, CancellationToken ct)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        DateTime now = DateTime.UtcNow;
        DateTime until = now.AddSeconds(Seconds(duration));
        using var claim = SqliteCommand(transaction, """
            UPDATE jobs SET status='leased',locked_by=$worker,lease_until=$until,attempts=attempts+1
            WHERE id=(SELECT j.id FROM jobs j WHERE j.job_kind=$kind AND j.brand_id IS $brand
              AND j.available_at<=$now AND j.attempts<9223372036854775807
              AND (j.status IN ('pending','retry') OR ($recover AND j.status='leased' AND j.lease_until<=$now))
              AND ($kind<>'submit_supplier' OR EXISTS (SELECT 1 FROM supplier_requests r WHERE r.id=j.supplier_request_id AND r.status='pending'))
              AND (j.origin_revision IS NULL OR EXISTS (SELECT 1 FROM supplier_requests r WHERE r.id=j.supplier_request_id AND r.revision=j.origin_revision))
              ORDER BY j.available_at,j.id LIMIT 1)
            RETURNING id,brand_id,job_kind,coalesce(payment_id,order_id,fulfillment_id,campaign_id,discount_id,support_message_id,
              telegram_delivery_id,product_stock_subscription_id,supplier_request_id,product_id,product_supplier_source_id),audit_event_id,origin_revision,attempts
            """, ("$brand", brand), ("$kind", JobIntent.CodeOf(kind)), ("$worker", worker.ToString("D")),
            ("$now", SqliteSchema.UtcText(now)), ("$until", SqliteSchema.UtcText(until)),
            ("$recover", kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier or JobKind.SendTelegram)));
        await using var row = await claim.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new JobLease(row.GetGuid(0), ReadSqliteIntent(row), worker, row.GetInt64(6), until) : null;
    }

    internal static async Task<JobLease> RenewSqliteAsync(SqliteTransaction transaction, JobLease lease, TimeSpan duration, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        DateTime until = now.AddSeconds(Seconds(duration));
        await ChangeSqliteLeaseAsync(transaction, lease, now, "lease_until=$until", ct, ("$until", SqliteSchema.UtcText(until)));
        return new JobLease(lease.Id, lease.Intent, lease.Worker, lease.Attempt, until);
    }

    internal static Task CompleteSqliteAsync(SqliteTransaction transaction, JobLease lease, CancellationToken ct) =>
        FinishSqliteAsync(transaction, lease, "succeeded", null, ct);

    internal static Task FailSqliteAsync(SqliteTransaction transaction, JobLease lease, JobError error, CancellationToken ct) =>
        FinishSqliteAsync(transaction, lease, "failed", ErrorCode(error), ct);

    internal static Task RetrySqliteAsync(SqliteTransaction transaction, JobLease lease, TimeSpan delay, int maximumAttempts, JobError error, CancellationToken ct)
    {
        if (lease.Intent.RequiresExplicitRecovery || error == JobError.RemoteOutcomeUnknown)
            throw new InvalidOperationException("Remote-effect work requires domain recovery authorization.");
        if (error is not (JobError.Transient or JobError.Busy)) throw new ArgumentException("Only known transient local failures may retry.");
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        DateTime now = DateTime.UtcNow;
        string available = SqliteSchema.UtcText(now.AddSeconds(Seconds(delay)));
        bool exhausted = lease.Attempt >= maximumAttempts;
        return ChangeSqliteLeaseAsync(transaction, lease, now, """
            status=$status,locked_by=NULL,lease_until=NULL,available_at=$available,
            completed_at=CASE WHEN $status='failed' THEN $now ELSE NULL END,last_error_code=$error
            """, ct, ("$status", exhausted ? "failed" : "retry"), ("$error", ErrorCode(exhausted ? JobError.AttemptLimit : error)), ("$available", available));
    }

    private static Task FinishSqliteAsync(SqliteTransaction transaction, JobLease lease, string status, string? code, CancellationToken ct) =>
        ChangeSqliteLeaseAsync(transaction, lease, DateTime.UtcNow, "status=$status,locked_by=NULL,lease_until=NULL,completed_at=$now,last_error_code=$error",
            ct, ("$status", status), ("$error", code));

    private static async Task ChangeSqliteLeaseAsync(SqliteTransaction transaction, JobLease lease, DateTime now, string set, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = SqliteCommand(transaction, "UPDATE jobs AS j SET " + set + SqliteFence,
            ("$id", lease.Id), ("$brand", lease.Intent.BrandId), ("$worker", lease.Worker.ToString("D")), ("$attempt", lease.Attempt), ("$now", SqliteSchema.UtcText(now)));
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new DbOperationException(new(DbFailureKind.LeaseLost, DbRecovery.StopLeaseOwner));
    }

    private static SqliteCommand SqliteCommand(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static JobIntent ReadSqliteIntent(SqliteDataReader row) =>
        JobIntent.Read(row.GetString(2), Id(1, row), Id(3, row), Id(4, row), row.IsDBNull(5) ? null : row.GetInt64(5));

    private static Guid? Id(int ordinal, SqliteDataReader row) => row.IsDBNull(ordinal) ? null : row.GetGuid(ordinal);
}
