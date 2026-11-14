using Platform.Security;

namespace Platform.Persistence;

public enum NoticeOutcomeKind { Sent, RateLimited, Blocked, Failed, Unknown }
public sealed record NoticeOutcome(NoticeOutcomeKind Kind, long? MessageId = null, int? RetryAfterSeconds = null);
public sealed class NoticeDispatch
{
    internal NoticeDispatch(Guid id, long bot, long chat, string recipient, string purpose, string language, string? text, string? media)
    { Id = id; BotId = bot; ChatId = chat; Recipient = recipient; Purpose = purpose; Language = language; Text = text; Media = media; }
    public Guid Id { get; }
    public long BotId { get; }
    public long ChatId { get; }
    public string Recipient { get; }
    public string Purpose { get; }
    public string Language { get; }
    public string? Text { get; }
    public string? Media { get; }
    public override string ToString() => "NoticeDispatch [redacted]";
}
public sealed partial class CampaignStore
{
    private const string NoticePurposes = "('campaign','restock_notice','staff_supplier_case','staff_supplier_bonus')";
    public async Task<JobLease?> ClaimNoticeAsync(Guid worker, CancellationToken ct = default)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        await using var session = await sessions.BeginAsync(ct);
        Guid? scopeBrand = await session.AdmitClaimWriterAsync(true, ct);
        var lease = await session.QuerySingleAsync<JobLease?>(new DbStatement("""
            WITH candidate AS (SELECT j.id FROM shop.jobs j JOIN shop.telegram_deliveries d ON d.id=j.telegram_delivery_id
                WHERE ($2 IS NULL OR j.brand_id=$2) AND j.job_kind='send_telegram' AND j.status IN ('pending','retry') AND j.available_at<=clock_timestamp()
                    AND d.status IN ('pending','retry') AND d.next_attempt_at<=clock_timestamp() AND d.purpose IN
            """ + NoticePurposes + """
                ORDER BY j.available_at,j.id LIMIT 1 FOR UPDATE OF j SKIP LOCKED), claimed AS (
                UPDATE shop.jobs j SET status='leased',locked_by=$1,lease_until=clock_timestamp()+INTERVAL '60 seconds',attempts=attempts+1
                FROM candidate c WHERE j.id=c.id RETURNING j.*)
            SELECT j.id,j.brand_id,j.telegram_delivery_id,j.attempts,j.lease_until FROM (SELECT 1) seed LEFT JOIN claimed j ON true
            """, DbValue.Text(worker.ToString("D")), scopeBrand is Guid brand ? DbValue.Uuid(brand) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid)), r => r.IsDBNull(0) ? null : new JobLease(r.GetGuid(0), JobIntent.Telegram(r.GetGuid(1), r.GetGuid(2)), worker, r.GetInt64(3), r.GetUtc(4)), ct);
        await session.CommitAsync(ct); return lease;
    }
    public async Task<NoticeDispatch?> BeginNoticeAsync(BrandContext context, JobLease lease, TimeSpan spacing, long? trustedStaffChat, IReadOnlyDictionary<string, string> supplierAccounts, CancellationToken ct = default)
    {
        if (spacing < TimeSpan.FromMilliseconds(100) || spacing > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(spacing));
        Guid delivery = NoticeId(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var identity = await IdentityAsync(session, delivery, context, ct);
            await LockNoticeOriginAsync(session, identity, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
            var data = await session.QuerySingleAsync(new DbStatement("""
                SELECT d.status,d.recipient_kind,d.staff_chat_id,d.purpose,u.telegram_id,u.blocked_at IS NOT NULL OR u.banned_at IS NOT NULL,u.marketing_opt_out_at IS NOT NULL,
                    COALESCE(d.recipient_language,split_part(lower(u.language),'-',1),'en'),b.active,b.bot_id,c.status,c.message_text,c.media_ref,
                    sub.closed_at IS NOT NULL OR sub.cancelled_at IS NOT NULL,CASE WHEN bp.title_mode='override' THEN bp.title_override ELSE p.title END,
                    c.language,split_part(lower(u.language),'-',1),c.active_since,u.last_seen_at
                FROM shop.telegram_deliveries d JOIN shop.brands b ON b.id=d.brand_id LEFT JOIN shop.users u ON u.id=d.user_id
                    LEFT JOIN shop.campaigns c ON c.id=d.campaign_id LEFT JOIN shop.product_stock_subscriptions sub ON sub.id=d.product_stock_subscription_id
                    LEFT JOIN shop.products p ON p.id=sub.product_id LEFT JOIN shop.brand_products bp ON bp.product_id=p.id AND bp.brand_id=d.brand_id
                WHERE d.id=$1 FOR UPDATE OF d
                """, DbValue.Uuid(delivery)), r => new NoticeState(r.GetString(0), r.GetString(1), Long(r, 2), r.GetString(3), Long(r, 4), !r.IsDBNull(5) && r.GetBoolean(5), !r.IsDBNull(6) && r.GetBoolean(6), r.GetString(7), r.GetBoolean(8), r.GetInt64(9), Text(r, 10), Text(r, 11), Text(r, 12), !r.IsDBNull(13) && r.GetBoolean(13), Text(r, 14), Text(r, 15), Text(r, 16), Time(r, 17), Time(r, 18)), ct);
            if (data.Status is "succeeded" or "cancelled" or "failed") { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return null; }
            if (data.Status is not ("pending" or "retry")) throw new InvalidOperationException("Notice requires explicit recovery.");
            if (data.Bot != context.BotId) throw new UnauthorizedAccessException();
            bool cancelled = data.Blocked || (data.Purpose == "campaign" && (data.OptedOut || data.CampaignStatus is not ("approved" or "running") ||
                (data.MemberLanguage is "en" or "fa" && data.MemberLanguage != data.CampaignLanguage) || (data.ActiveSince is DateTime since && data.LastSeen < since))) ||
                (data.Purpose == "restock_notice" && data.ConsentClosed) || (data.Recipient == "staff_chat" && (trustedStaffChat is null || trustedStaffChat != data.StaffChat));
            if (cancelled) { await FinishWithoutSendAsync(session, lease, identity, "cancelled", ct); await session.CommitAsync(ct); return null; }
            if (!data.Active || (identity.Product is Guid product && !await AvailableAsync(session, context.BrandId, product, supplierAccounts, ct)))
            { await DeferAsync(session, lease, delivery, 60, "eligibility_deferred", ct); await session.CommitAsync(ct); return null; }
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(context.BotId)), ct);
            long wait = await session.QuerySingleAsync(new DbStatement("""
                SELECT CEIL(GREATEST(0,EXTRACT(EPOCH FROM GREATEST(
                    COALESCE(max(last_attempt_at)+$2*INTERVAL '1 millisecond',clock_timestamp()),
                    COALESCE(max(next_attempt_at) FILTER(WHERE last_error_code='rate_limited'),clock_timestamp()))-clock_timestamp())))::bigint
                FROM shop.telegram_deliveries WHERE brand_id=$1
                """, DbValue.Uuid(context.BrandId), DbValue.Bigint((long)Math.Ceiling(spacing.TotalMilliseconds))), r => r.GetInt64(0), ct);
            if (wait > 0) { await DeferAsync(session, lease, delivery, Math.Max(1, checked((int)wait)), "bot_backoff", ct); await session.CommitAsync(ct); return null; }
            string? text = data.Text, media = data.Media;
            if (data.Purpose == "restock_notice") { text = data.ProductTitle; media = null; }
            if (data.Recipient == "staff_chat")
            {
                Guid order = await session.QuerySingleAsync(new DbStatement("SELECT r.order_id FROM shop.supplier_requests r JOIN shop.telegram_deliveries d ON d.supplier_request_id=r.id WHERE d.id=$1", DbValue.Uuid(delivery)), r => r.GetGuid(0), ct);
                text = "Supplier review required. Order " + order.ToString("D"); media = null;
            }
            await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='leased',attempts=$1,last_attempt_at=clock_timestamp(),eligibility_checked_at=clock_timestamp() WHERE id=$2", DbValue.Bigint(lease.Attempt), DbValue.Uuid(delivery)), ct);
            await session.CommitAsync(ct);
            return new(delivery, context.BotId, data.Recipient == "staff_chat" ? data.StaffChat!.Value : data.Chat!.Value, data.Recipient, data.Purpose, data.Language, text, media);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task RecordNoticeAsync(BrandContext context, JobLease lease, NoticeOutcome outcome, CancellationToken ct = default)
    {
        Guid delivery = NoticeId(context, lease);
        if ((outcome.Kind == NoticeOutcomeKind.Sent) != outcome.MessageId.HasValue || (outcome.Kind == NoticeOutcomeKind.RateLimited) != outcome.RetryAfterSeconds.HasValue || !Enum.IsDefined(outcome.Kind) || (outcome.Kind == NoticeOutcomeKind.Sent && outcome.MessageId is not > 0) || (outcome.Kind == NoticeOutcomeKind.RateLimited && outcome.RetryAfterSeconds is not (>= 1 and <= 3600))) throw new ArgumentException("Invalid remote outcome.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var identity = await IdentityAsync(session, delivery, context, ct);
            await LockNoticeOriginAsync(session, identity, ct);
            var attempt = await session.QuerySingleAsync(new DbStatement("SELECT attempts,status FROM shop.jobs WHERE id=$1 AND telegram_delivery_id=$2 FOR UPDATE", DbValue.Uuid(lease.Id), DbValue.Uuid(delivery)), r => (Attempt: r.GetInt64(0), Status: r.GetString(1)), ct);
            var current = await session.QuerySingleAsync(new DbStatement("SELECT status,attempts,telegram_message_id FROM shop.telegram_deliveries WHERE id=$1 FOR UPDATE", DbValue.Uuid(delivery)), r => (Status: r.GetString(0), Attempt: r.GetInt64(1), Message: Long(r, 2)), ct);
            if (current.Status == "succeeded") { if (outcome.Kind == NoticeOutcomeKind.Sent && outcome.MessageId != current.Message) throw new InvalidOperationException("Known receipt identity conflict."); return; }
            if (attempt.Attempt != lease.Attempt || current.Attempt != lease.Attempt || current.Status is not ("leased" or "uncertain")) throw new InvalidOperationException("Stale notice outcome.");
            bool canRetry = true;
            if (identity.Campaign is Guid campaign) canRetry = await session.QuerySingleAsync(new DbStatement("SELECT status IN ('approved','running') FROM shop.campaigns WHERE id=$1", DbValue.Uuid(campaign)), r => r.GetBoolean(0), ct);
            if (identity.Subscription is Guid subscription) canRetry &= await session.QuerySingleAsync(new DbStatement("SELECT closed_at IS NULL AND cancelled_at IS NULL FROM shop.product_stock_subscriptions WHERE id=$1", DbValue.Uuid(subscription)), r => r.GetBoolean(0), ct);
            if (outcome.Kind == NoticeOutcomeKind.RateLimited && canRetry)
                await DeferAsync(session, lease, delivery, outcome.RetryAfterSeconds!.Value, "rate_limited", ct);
            else
            {
                string state = outcome.Kind switch { NoticeOutcomeKind.Sent => "succeeded", NoticeOutcomeKind.Unknown => "uncertain", NoticeOutcomeKind.RateLimited => "cancelled", _ => "failed" };
                string error = outcome.Kind switch { NoticeOutcomeKind.Blocked => "recipient_blocked", NoticeOutcomeKind.Unknown => "remote_outcome_unknown", NoticeOutcomeKind.Failed => "permanent_failure", _ => "none" };
                await session.ExecuteAsync(new DbStatement("""
                    UPDATE shop.telegram_deliveries SET status=$1,telegram_message_id=$2,sent_at=CASE WHEN $1='succeeded' THEN clock_timestamp() ELSE sent_at END,last_error_code=$3 WHERE id=$4
                    """, DbValue.Text(state), outcome.MessageId is long message ? DbValue.Bigint(message) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Text(error), DbValue.Uuid(delivery)), ct);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,completed_at=clock_timestamp(),last_error_code=$2 WHERE id=$3 AND attempts=$4",
                    DbValue.Text(state == "succeeded" ? "succeeded" : state == "cancelled" ? "cancelled" : "failed"), DbValue.Text(error), DbValue.Uuid(lease.Id), DbValue.Bigint(lease.Attempt)), ct);
                if (outcome.Kind == NoticeOutcomeKind.Blocked && identity.Member is Guid member) await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=COALESCE(blocked_at,clock_timestamp()) WHERE id=$1", DbValue.Uuid(member)), ct);
                if (state != "uncertain" && identity.Subscription is Guid consent) await session.ExecuteAsync(new DbStatement("UPDATE shop.product_stock_subscriptions SET closed_at=clock_timestamp() WHERE id=$1 AND closed_at IS NULL", DbValue.Uuid(consent)), ct);
                if (identity.Campaign is Guid id) await CompleteCampaignAsync(session, id, ct);
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<int> RecoverExpiredNoticesAsync(CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var ids = await session.QueryManyAsync(new DbStatement("SELECT j.id,j.telegram_delivery_id FROM shop.jobs j JOIN shop.telegram_deliveries d ON d.id=j.telegram_delivery_id WHERE ($1 IS NULL OR d.brand_id=$1) AND j.status='leased' AND j.lease_until<=clock_timestamp() AND d.purpose IN " + NoticePurposes + " ORDER BY j.id LIMIT 100", session.WriterBrand is Guid scope ? DbValue.Uuid(scope) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid)), r => (Job: r.GetGuid(0), Delivery: r.GetGuid(1)), 100, ct);
        if (session.IsWriterScoped)
        {
            var origins = new List<NoticeIdentity>();
            foreach (var id in ids) origins.Add(await session.QuerySingleAsync(new DbStatement("SELECT d.campaign_id,d.user_id,d.product_stock_subscription_id,sub.product_id,d.brand_id FROM shop.telegram_deliveries d LEFT JOIN shop.product_stock_subscriptions sub ON sub.id=d.product_stock_subscription_id WHERE d.id=$1", DbValue.Uuid(id.Delivery)), r => new NoticeIdentity(Id(r, 0), Id(r, 1), Id(r, 2), Id(r, 3), r.GetGuid(4)), ct));
            foreach (Guid campaign in origins.Where(v => v.Campaign is not null).Select(v => v.Campaign!.Value).Distinct().Order()) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.campaigns WHERE id=$1 FOR UPDATE", DbValue.Uuid(campaign)), r => r.GetGuid(0), ct);
            foreach (Guid member in origins.Where(v => v.Member is not null).Select(v => v.Member!.Value).Distinct().Order()) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(member)), r => r.GetGuid(0), ct);
            foreach (Guid product in origins.Where(v => v.Product is not null).Select(v => v.Product!.Value).Distinct().Order()) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => r.GetGuid(0), ct);
            if (session.WriterBrand is Guid brand) await session.AdmitBrandWriterAsync(brand, ct);
        }
        int recovered = 0;
        foreach (var id in ids)
        {
            var origin = await session.QuerySingleAsync(new DbStatement("SELECT d.campaign_id,d.user_id,d.product_stock_subscription_id,sub.product_id,d.brand_id FROM shop.telegram_deliveries d LEFT JOIN shop.product_stock_subscriptions sub ON sub.id=d.product_stock_subscription_id WHERE d.id=$1", DbValue.Uuid(id.Delivery)), r => new NoticeIdentity(Id(r, 0), Id(r, 1), Id(r, 2), Id(r, 3), r.GetGuid(4)), ct);
            await LockNoticeOriginAsync(session, origin, ct);
            bool expired = await session.QuerySingleAsync(new DbStatement("SELECT status='leased' AND lease_until<=clock_timestamp() FROM shop.jobs WHERE id=$1 FOR UPDATE", DbValue.Uuid(id.Job)), r => r.GetBoolean(0), ct);
            if (!expired) continue;
            recovered++;
            string status = await session.QuerySingleAsync(new DbStatement("SELECT status FROM shop.telegram_deliveries WHERE id=$1 FOR UPDATE", DbValue.Uuid(id.Delivery)), r => r.GetString(0), ct);
            bool uncertain = status is "leased" or "uncertain";
            if (uncertain) await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='uncertain',last_error_code='remote_outcome_unknown' WHERE id=$1", DbValue.Uuid(id.Delivery)), ct);
            string recoveredStatus = uncertain ? "failed" : status is "succeeded" or "failed" or "cancelled" ? status : "retry";
            await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,available_at=clock_timestamp(),completed_at=CASE WHEN $1='retry' THEN NULL ELSE clock_timestamp() END,last_error_code=$2 WHERE id=$3",
                DbValue.Text(recoveredStatus), DbValue.Text(uncertain ? "remote_outcome_unknown" : "pre_dispatch_recovery"), DbValue.Uuid(id.Job)), ct);
        }
        await session.CommitAsync(ct); return recovered;
    }
    private static async Task DeferAsync(DbSession session, JobLease lease, Guid delivery, int seconds, string reason, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='retry',next_attempt_at=clock_timestamp()+$1*INTERVAL '1 second',last_error_code=$2 WHERE id=$3", DbValue.Integer(seconds), DbValue.Text(reason), DbValue.Uuid(delivery)), ct);
        int changed = await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='retry',locked_by=NULL,lease_until=NULL,completed_at=NULL,available_at=clock_timestamp()+$1*INTERVAL '1 second',last_error_code=$2 WHERE id=$3 AND attempts=$4 AND status IN ('leased','failed')", DbValue.Integer(seconds), DbValue.Text(reason), DbValue.Uuid(lease.Id), DbValue.Bigint(lease.Attempt)), ct);
        if (changed != 1) throw new InvalidOperationException("Notice lease changed.");
    }
    private static async Task FinishWithoutSendAsync(DbSession session, JobLease lease, NoticeIdentity identity, string status, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status=$1 WHERE id=$2", DbValue.Text(status), DbValue.Uuid(lease.Intent.OriginId!.Value)), ct);
        await OutboxStore.CompleteAsync(session, lease, ct);
        if (identity.Subscription is Guid consent) await session.ExecuteAsync(new DbStatement("UPDATE shop.product_stock_subscriptions SET closed_at=clock_timestamp() WHERE id=$1 AND closed_at IS NULL", DbValue.Uuid(consent)), ct);
        if (identity.Campaign is Guid campaign) await CompleteCampaignAsync(session, campaign, ct);
    }
    private static Guid NoticeId(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static Task<NoticeIdentity> IdentityAsync(DbSession session, Guid delivery, BrandContext context, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("""
        SELECT d.campaign_id,d.user_id,d.product_stock_subscription_id,sub.product_id,d.brand_id FROM shop.telegram_deliveries d
        LEFT JOIN shop.product_stock_subscriptions sub ON sub.id=d.product_stock_subscription_id WHERE d.id=$1 AND d.brand_id=$2 AND d.purpose IN
        """ + NoticePurposes, DbValue.Uuid(delivery), DbValue.Uuid(context.BrandId)), r => new NoticeIdentity(Id(r, 0), Id(r, 1), Id(r, 2), Id(r, 3), r.GetGuid(4)), ct);
    private static async Task LockNoticeOriginAsync(DbSession session, NoticeIdentity data, CancellationToken ct)
    {
        if (data.Campaign is Guid campaign) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.campaigns WHERE id=$1 FOR UPDATE", DbValue.Uuid(campaign)), r => r.GetGuid(0), ct);
        if (data.Member is Guid member) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(member)), r => r.GetGuid(0), ct);
        if (data.Product is Guid product) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => r.GetGuid(0), ct);
        await session.AdmitBrandWriterAsync(data.Brand, ct);
        if (data.Subscription is Guid subscription) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.product_stock_subscriptions WHERE id=$1 FOR UPDATE", DbValue.Uuid(subscription)), r => r.GetGuid(0), ct);
    }
    private static Guid? Id(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static long? Long(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Text(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime? Time(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetUtc(i);
    private sealed record NoticeIdentity(Guid? Campaign, Guid? Member, Guid? Subscription, Guid? Product, Guid Brand);
    private sealed record NoticeState(string Status, string Recipient, long? StaffChat, string Purpose, long? Chat, bool Blocked, bool OptedOut, string Language, bool Active, long Bot,
        string? CampaignStatus, string? Text, string? Media, bool ConsentClosed, string? ProductTitle, string? CampaignLanguage, string? MemberLanguage, DateTime? ActiveSince, DateTime? LastSeen);
}
