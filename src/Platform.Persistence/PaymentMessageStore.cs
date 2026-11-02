using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum PaymentMessageOutcomeKind { Sent, RateLimited, Uneditable, Blocked, Unknown }
public sealed record PaymentMessageOutcome(PaymentMessageOutcomeKind Kind, long? MessageId = null, int? RetryAfterSeconds = null);
public sealed record PaymentMessageDispatch(Guid Id, Guid Order, long BotId, long ChatId, long? TargetMessageId, string Text)
{
    public override string ToString() => "PaymentMessageDispatch [redacted]";
}
public sealed partial class PaymentInstructionStore
{
    public async Task<JobLease?> ClaimAsync(Guid worker, CancellationToken ct = default)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        await using var session = await sessions.BeginAsync(ct);
        var lease = await session.QuerySingleAsync<JobLease?>(new DbStatement("""
            WITH candidate AS (SELECT j.id FROM shop.jobs j JOIN shop.telegram_deliveries d ON d.id=j.telegram_delivery_id JOIN shop.orders o ON o.id=d.order_id
                WHERE o.payment_due_at IS NOT NULL AND j.job_kind='send_telegram' AND j.status IN ('pending','retry') AND j.available_at<=clock_timestamp()
                    AND d.purpose='order_status' AND d.status IN ('pending','retry') AND d.next_attempt_at<=clock_timestamp()
                ORDER BY j.available_at,j.id LIMIT 1 FOR UPDATE OF j SKIP LOCKED), claimed AS (
                UPDATE shop.jobs j SET status='leased',locked_by=$1,lease_until=clock_timestamp()+INTERVAL '60 seconds',attempts=attempts+1 FROM candidate c WHERE j.id=c.id RETURNING j.*)
            SELECT j.id,j.brand_id,j.telegram_delivery_id,j.attempts,j.lease_until FROM (SELECT 1) seed LEFT JOIN claimed j ON true
            """, DbValue.Text(worker.ToString("D"))), r => r.IsDBNull(0) ? null : new JobLease(r.GetGuid(0), JobIntent.Telegram(r.GetGuid(1), r.GetGuid(2)), worker, r.GetInt64(3), r.GetUtc(4)), ct);
        await session.CommitAsync(ct); return lease;
    }
    public async Task<bool> BeginAsync(BrandContext context, JobLease lease, TimeSpan spacing, CancellationToken ct = default)
    {
        if (spacing < TimeSpan.FromMilliseconds(100) || spacing > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(spacing));
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        if (data.Status is "succeeded" or "cancelled" or "failed" or "uncertain" or "leased") return false;
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
        if (!data.Reachable) { await FinishAsync(session, lease, id, "cancelled", "recipient_blocked", ct); await session.CommitAsync(ct); return false; }
        long inFlight = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_status' AND id<>$2 AND status='leased'", DbValue.Uuid(data.Order), DbValue.Uuid(id)), r => r.GetInt64(0), ct);
        if (inFlight > 0) { await DeferAsync(session, lease, id, 1, "order_backoff", ct); await session.CommitAsync(ct); return false; }
        bool uncertain = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_status' AND id<>$2 AND status='uncertain' AND action='send')", DbValue.Uuid(data.Order), DbValue.Uuid(id)), r => r.GetBoolean(0), ct);
        if (uncertain && data.Target is null && data.Audit is null) { await FinishAsync(session, lease, id, "failed", "recovery_required", ct); await session.CommitAsync(ct); return false; }
        long wait = await session.QuerySingleAsync(new DbStatement("SELECT GREATEST(0,ceil(EXTRACT(EPOCH FROM (GREATEST(COALESCE(max(last_attempt_at)+$2*INTERVAL '1 millisecond',clock_timestamp()),COALESCE(max(next_attempt_at) FILTER(WHERE last_error_code='rate_limited'),clock_timestamp()))-clock_timestamp()))))::bigint FROM shop.telegram_deliveries WHERE brand_id=$1", DbValue.Uuid(context.BrandId), DbValue.Bigint((long)Math.Ceiling(spacing.TotalMilliseconds))), r => r.GetInt64(0), ct);
        if (wait > 0) { await DeferAsync(session, lease, id, checked((int)Math.Min(wait, 3600)), "bot_backoff", ct); await session.CommitAsync(ct); return false; }
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='leased',attempts=$1,last_attempt_at=clock_timestamp() WHERE id=$2", DbValue.Bigint(lease.Attempt), DbValue.Uuid(id)), ct);
        await session.CommitAsync(ct); return true;
    }
    public async Task<PaymentMessageDispatch?> ReadForSendAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
        if (data.Status != "leased" || data.Attempt != lease.Attempt) throw new InvalidOperationException("Stale payment message dispatch.");
        if (!data.Reachable) { await FinishAsync(session, lease, id, "cancelled", "recipient_blocked", ct); await session.CommitAsync(ct); return null; }
        bool recovered = data.Audit is Guid audit && await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.telegram_deliveries d JOIN shop.audit_events a ON a.id=$1 WHERE d.order_id=$2 AND d.id<>$3 AND d.purpose='order_status' AND d.status='succeeded' AND d.sent_at>=a.created_at)", DbValue.Uuid(audit), DbValue.Uuid(data.Order), DbValue.Uuid(id)), r => r.GetBoolean(0), ct);
        if (recovered) { await FinishAsync(session, lease, id, "cancelled", "known_success", ct); await session.CommitAsync(ct); return null; }
        if (data.Target is null && data.Audit is null)
        {
            long? anchor = await session.QuerySingleAsync(new DbStatement("SELECT telegram_message_id FROM (SELECT 1) seed LEFT JOIN LATERAL (SELECT telegram_message_id FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_status' AND status='succeeded' AND telegram_message_id IS NOT NULL ORDER BY created_at,id LIMIT 1) d ON true", DbValue.Uuid(data.Order)), r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ct);
            if (anchor is long target)
            {
                await FinishAsync(session, lease, id, "cancelled", "saved_anchor", ct);
                Guid redirected = await session.QuerySingleAsync(new DbStatement("INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id) VALUES (gen_random_uuid(),$1,'customer_dm',$2,'order_status','edit',$3,$4,$5) ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=EXCLUDED.dedupe_key RETURNING id", DbValue.Uuid(context.BrandId), DbValue.Uuid(data.Member), DbValue.Bigint(target), DbValue.Text("instruction-anchor:" + id.ToString("D")), DbValue.Uuid(data.Order)), r => r.GetGuid(0), ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, redirected), redirected, DateTime.UtcNow, ct); await session.CommitAsync(ct); return null;
            }
        }
        var current = await session.QuerySingleAsync(new DbStatement("SELECT payment_status,resolution_kind,payment_due_at,clock_timestamp() FROM shop.orders WHERE id=$1", DbValue.Uuid(data.Order)), r => (State: r.GetString(0), Resolution: r.IsDBNull(1) ? null : r.GetString(1), Due: r.GetUtc(2), Now: r.GetUtc(3)), ct);
        if (current.State == "awaiting_payment" && current.Now > current.Due)
        { await DeferAsync(session, lease, id, 1, "expiry_pending", ct); await session.CommitAsync(ct); return null; }
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE telegram_delivery_id IN (SELECT id FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_status' AND id<>$2 AND status IN ('pending','retry')) AND status IN ('pending','retry')", DbValue.Uuid(data.Order), DbValue.Uuid(id)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='cancelled',last_error_code='latest_projection' WHERE order_id=$1 AND purpose='order_status' AND id<>$2 AND status IN ('pending','retry')", DbValue.Uuid(data.Order), DbValue.Uuid(id)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET eligibility_checked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(id)), ct);
        string text = PaymentDeadlinePolicy.Text(data.Order, current.State, current.Resolution, current.Due, data.Language);
        await session.CommitAsync(ct); return new(id, data.Order, context.BotId, data.Chat, data.Target, text);
    }
    public async Task RecordAsync(BrandContext context, JobLease lease, PaymentMessageOutcome outcome, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(outcome.Kind) || (outcome.Kind == PaymentMessageOutcomeKind.Sent) != outcome.MessageId.HasValue || (outcome.Kind == PaymentMessageOutcomeKind.RateLimited) != outcome.RetryAfterSeconds.HasValue || outcome.MessageId <= 0 || outcome.RetryAfterSeconds is < 1 or > 3600) throw new ArgumentException("Invalid payment message outcome.");
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        var attempt = await session.QuerySingleAsync(new DbStatement("SELECT attempts FROM shop.jobs WHERE id=$1 AND telegram_delivery_id=$2 FOR UPDATE", DbValue.Uuid(lease.Id), DbValue.Uuid(id)), r => r.GetInt64(0), ct);
        if (data.Status == "succeeded") { if (outcome.Kind == PaymentMessageOutcomeKind.Sent && outcome.MessageId != data.Message) throw new InvalidOperationException("Known message receipt conflict."); return; }
        if (data.Attempt != lease.Attempt || attempt != lease.Attempt || data.Status is not ("leased" or "uncertain" or "retry" or "failed")) throw new InvalidOperationException("Stale payment message outcome.");
        if (data.Status is "retry" or "failed" && outcome.Kind != PaymentMessageOutcomeKind.Sent) throw new InvalidOperationException("Only a late known receipt can resolve a finished attempt.");
        if (outcome.Kind == PaymentMessageOutcomeKind.Sent && data.Target is long target && outcome.MessageId != target) throw new InvalidOperationException("Wrong saved edit target.");
        if (outcome.Kind == PaymentMessageOutcomeKind.RateLimited || (outcome.Kind == PaymentMessageOutcomeKind.Unknown && data.Target is not null))
            await DeferAsync(session, lease, id, outcome.RetryAfterSeconds ?? 1, outcome.Kind == PaymentMessageOutcomeKind.RateLimited ? "rate_limited" : "remote_outcome_unknown", ct);
        else
        {
            string state = outcome.Kind switch { PaymentMessageOutcomeKind.Sent => "succeeded", PaymentMessageOutcomeKind.Blocked => "cancelled", PaymentMessageOutcomeKind.Unknown => "uncertain", _ => "failed" };
            string error = outcome.Kind switch { PaymentMessageOutcomeKind.Uneditable => "uneditable", PaymentMessageOutcomeKind.Blocked => "recipient_blocked", PaymentMessageOutcomeKind.Unknown => "remote_outcome_unknown", _ => "none" };
            await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status=$1,telegram_message_id=$2,sent_at=CASE WHEN $1='succeeded' THEN clock_timestamp() ELSE sent_at END,last_error_code=$3 WHERE id=$4", DbValue.Text(state), outcome.MessageId is long message ? DbValue.Bigint(message) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Text(error), DbValue.Uuid(id)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,completed_at=clock_timestamp(),last_error_code=$2 WHERE id=$3", DbValue.Text(state == "uncertain" ? "failed" : state), DbValue.Text(error), DbValue.Uuid(lease.Id)), ct);
            if (outcome.Kind == PaymentMessageOutcomeKind.Blocked) await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=COALESCE(blocked_at,clock_timestamp()) WHERE id=$1", DbValue.Uuid(data.Member)), ct);
        }
        await session.CommitAsync(ct);
    }
    public async Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct); var data = await LockedAsync(session, context, delivery, ct);
        string state = data.Status == "leased" ? data.Target is null ? "uncertain" : "retry" : data.Status;
        int changed = await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,available_at=clock_timestamp(),completed_at=CASE WHEN $1='failed' THEN clock_timestamp() ELSE NULL END,last_error_code='remote_outcome_unknown' WHERE telegram_delivery_id=$2 AND status='leased' AND lease_until<=clock_timestamp()", DbValue.Text(state == "uncertain" ? "failed" : state), DbValue.Uuid(delivery)), ct);
        if (changed > 0) await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status=$1,next_attempt_at=clock_timestamp(),last_error_code='remote_outcome_unknown' WHERE id=$2", DbValue.Text(state), DbValue.Uuid(delivery)), ct);
        await session.CommitAsync(ct);
    }
    public async Task<Guid> AuthorizeFallbackAsync(BrandContext context, AuthenticatedAdmin actor, Guid failedDelivery, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, failedDelivery, ct);
        bool fresh = await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.DeliveryRecovery, AuditEntity.Order, data.Order, context.BrandId, AuditReason.Reconciliation, Guid.NewGuid(), AdminCommand.Hash("payment-status-fallback", failedDelivery.ToString("D"), data.Order.ToString("D"))), ct);
        if (fresh && data.Status is not ("failed" or "uncertain")) throw new InvalidOperationException("Payment message needs no fallback.");
        Guid id = await session.QuerySingleAsync(new DbStatement("INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,order_id,audit_event_id) VALUES (gen_random_uuid(),$1,'customer_dm',$2,'order_status','send',$3,$4,$5) ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=EXCLUDED.dedupe_key RETURNING id", DbValue.Uuid(context.BrandId), DbValue.Uuid(data.Member), DbValue.Text("instruction-recovery:" + command.ToString("D")), DbValue.Uuid(data.Order), DbValue.Uuid(command)), r => r.GetGuid(0), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct); await session.CommitAsync(ct); return id;
    }
    private static Guid Origin(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static async Task<State> LockedAsync(DbSession session, BrandContext context, Guid id, CancellationToken ct)
    {
        var before = await ReadAsync(session, context, id, ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 FOR UPDATE) u", DbValue.Uuid(before.Member)), r => r.GetInt64(0), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE) b", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetInt64(0), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE) o", DbValue.Uuid(before.Order)), r => r.GetInt64(0), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.telegram_deliveries WHERE id=$1 FOR UPDATE) d", DbValue.Uuid(id)), r => r.GetInt64(0), ct);
        return await ReadAsync(session, context, id, ct);
    }
    private static async Task<State> ReadAsync(DbSession session, BrandContext context, Guid id, CancellationToken ct)
    {
        State? state = await session.QuerySingleAsync<State?>(new DbStatement("SELECT d.order_id,d.user_id,u.telegram_id,d.target_message_id,d.telegram_message_id,d.status,d.attempts,u.language,b.active AND u.blocked_at IS NULL AND u.banned_at IS NULL,d.audit_event_id FROM (SELECT 1) seed LEFT JOIN (shop.telegram_deliveries d JOIN shop.users u ON u.id=d.user_id JOIN shop.brands b ON b.id=u.brand_id JOIN shop.orders o ON o.id=d.order_id AND o.user_id=u.id) ON d.id=$1 AND d.brand_id=$2 AND b.bot_id=$3 AND d.purpose='order_status' AND o.payment_due_at IS NOT NULL", DbValue.Uuid(id), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.IsDBNull(0) ? null : new(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetGuid(9)), ct);
        return state ?? throw new UnauthorizedAccessException();
    }
    private static async Task DeferAsync(DbSession session, JobLease lease, Guid delivery, int seconds, string reason, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='retry',next_attempt_at=clock_timestamp()+$1*INTERVAL '1 second',last_error_code=$2 WHERE id=$3", DbValue.Integer(seconds), DbValue.Text(reason), DbValue.Uuid(delivery)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='retry',locked_by=NULL,lease_until=NULL,available_at=clock_timestamp()+$1*INTERVAL '1 second',last_error_code=$2 WHERE id=$3", DbValue.Integer(seconds), DbValue.Text(reason), DbValue.Uuid(lease.Id)), ct);
    }
    private static async Task FinishAsync(DbSession session, JobLease lease, Guid delivery, string state, string reason, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status=$1,last_error_code=$2 WHERE id=$3", DbValue.Text(state), DbValue.Text(reason), DbValue.Uuid(delivery)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,completed_at=clock_timestamp(),last_error_code=$2 WHERE id=$3", DbValue.Text(state), DbValue.Text(reason), DbValue.Uuid(lease.Id)), ct);
    }
    private sealed record State(Guid Order, Guid Member, long Chat, long? Target, long? Message, string Status, long Attempt, string Language, bool Reachable, Guid? Audit);
}
