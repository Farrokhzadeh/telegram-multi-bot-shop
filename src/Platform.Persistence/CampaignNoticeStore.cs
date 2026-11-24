using Platform.Security;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

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
    private static readonly string[] NoticePurposes = ["campaign", "restock_notice", "staff_supplier_case", "staff_supplier_bonus"];
    public async Task<JobLease?> ClaimNoticeAsync(Guid worker, CancellationToken ct = default)
    {
        if (worker == Guid.Empty) throw new ArgumentException("Worker identity is required.");
        await using var session = await sessions.BeginAsync(ct);
        Guid? scopeBrand = await session.AdmitClaimWriterAsync(true, ct);
        var lease = await session.RunEfStepAsync(true, (data, token) => data.ClaimNoticeAsync(scopeBrand, worker, NoticePurposes, token), ct);
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
            var data = await ReadNoticeAsync(session, delivery, ct);
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
            long wait = await session.RunEfStepAsync(false, async (data, token) =>
            {
                var query = data.Data.Set<Entities.TelegramDelivery>().Where(row => row.BrandId == context.BrandId);
                DateTime? attempted = await query.MaxAsync(row => row.LastAttemptAt, token);
                DateTime? limited = await query.Where(row => row.LastErrorCode == "rate_limited").MaxAsync(row => (DateTime?)row.NextAttemptAt, token);
                DateTime now = await data.BrandClockAsync(context.BrandId, token);
                DateTime next = attempted?.AddMilliseconds(Math.Ceiling(spacing.TotalMilliseconds)) ?? now;
                if (limited > next) next = limited.Value;
                return (long)Math.Ceiling(Math.Max(0, (next - now).TotalSeconds));
            }, ct);
            if (wait > 0) { await DeferAsync(session, lease, delivery, Math.Max(1, checked((int)wait)), "bot_backoff", ct); await session.CommitAsync(ct); return null; }
            string? text = data.Text, media = data.Media;
            if (data.Purpose == "restock_notice") { text = data.ProductTitle; media = null; }
            if (data.Recipient == "staff_chat")
            {
                Guid order = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join d in data.Data.Set<Entities.TelegramDelivery>() on r.Id equals d.SupplierRequestId where d.Id == delivery select r.OrderId).SingleAsync(token), ct);
                text = "Supplier review required. Order " + order.ToString("D"); media = null;
            }
            await ChangeNoticeAsync(session, context.BrandId, delivery, (row, now) => { row.Status = "leased"; row.Attempts = lease.Attempt; row.LastAttemptAt = now; row.EligibilityCheckedAt = now; }, ct);
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
            var attempt = await session.RunEfStepAsync(false, async (data, token) => { await data.LockJobAsync(lease.Id, token); return await data.Data.Set<Entities.Job>().Where(row => row.Id == lease.Id && row.TelegramDeliveryId == delivery).Select(row => new { Attempt = row.Attempts, row.Status }).SingleAsync(token); }, ct);
            var current = await session.RunEfStepAsync(false, async (data, token) => { await data.LockMessageAsync(delivery, token); return await data.Data.Set<Entities.TelegramDelivery>().Where(row => row.Id == delivery).Select(row => new { row.Status, Attempt = row.Attempts, Message = row.TelegramMessageId }).SingleAsync(token); }, ct);
            if (current.Status == "succeeded") { if (outcome.Kind == NoticeOutcomeKind.Sent && outcome.MessageId != current.Message) throw new InvalidOperationException("Known receipt identity conflict."); return; }
            if (attempt.Attempt != lease.Attempt || current.Attempt != lease.Attempt || current.Status is not ("leased" or "uncertain")) throw new InvalidOperationException("Stale notice outcome.");
            bool canRetry = true;
            if (identity.Campaign is Guid campaign) canRetry = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Campaign>().Where(row => row.Id == campaign).Select(row => row.Status == "approved" || row.Status == "running").SingleAsync(token), ct);
            if (identity.Subscription is Guid subscription) canRetry &= await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.ProductStockSubscription>().Where(row => row.Id == subscription).Select(row => row.ClosedAt == null && row.CancelledAt == null).SingleAsync(token), ct);
            if (outcome.Kind == NoticeOutcomeKind.RateLimited && canRetry)
                await DeferAsync(session, lease, delivery, outcome.RetryAfterSeconds!.Value, "rate_limited", ct);
            else
            {
                string state = outcome.Kind switch { NoticeOutcomeKind.Sent => "succeeded", NoticeOutcomeKind.Unknown => "uncertain", NoticeOutcomeKind.RateLimited => "cancelled", _ => "failed" };
                string error = outcome.Kind switch { NoticeOutcomeKind.Blocked => "recipient_blocked", NoticeOutcomeKind.Unknown => "remote_outcome_unknown", NoticeOutcomeKind.Failed => "permanent_failure", _ => "none" };
                await ChangeNoticeAsync(session, context.BrandId, delivery, (row, now) => { row.Status = state; row.TelegramMessageId = outcome.MessageId; if (state == "succeeded") row.SentAt = now; row.LastErrorCode = error; }, ct);
                await ChangeNoticeJobAsync(session, context.BrandId, lease.Id, row => row.Attempts == lease.Attempt, (row, now) => { row.Status = state == "succeeded" ? "succeeded" : state == "cancelled" ? "cancelled" : "failed"; row.LockedBy = null; row.LeaseUntil = null; row.CompletedAt = now; row.LastErrorCode = error; }, ct);
                if (outcome.Kind == NoticeOutcomeKind.Blocked && identity.Member is Guid member) await session.RunEfStepAsync(true, async (data, token) => { await data.RequireJobCommandAsync(context.BrandId, token); var row = await data.Data.Set<Entities.User>().SingleAsync(row => row.Id == member, token); row.BlockedAt ??= await data.BrandClockAsync(context.BrandId, token); return true; }, ct);
                if (state != "uncertain" && identity.Subscription is Guid consent) await CloseNoticeSubscriptionAsync(session, context.BrandId, consent, ct);
                if (identity.Campaign is Guid id) await CompleteCampaignAsync(session, id, ct);
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<int> RecoverExpiredNoticesAsync(CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        Guid? scope = session.WriterBrand;
        var ids = await session.RunEfStepAsync(false, (data, token) => (from j in data.Data.Set<Entities.Job>() join d in data.Data.Set<Entities.TelegramDelivery>() on j.TelegramDeliveryId equals d.Id where (scope == null || d.BrandId == scope) && j.Status == "leased" && j.LeaseUntil <= EfValues.UtcNow() && NoticePurposes.Contains(d.Purpose) orderby j.Id select new { Job = j.Id, Delivery = d.Id }).Take(100).ToListAsync(token), ct);
        if (session.IsWriterScoped)
        {
            var origins = new List<NoticeIdentity>();
            foreach (var id in ids) origins.Add(await ReadNoticeIdentityAsync(session, id.Delivery, null, ct));
            await session.RunEfStepAsync(false, async (data, token) => { foreach (Guid campaign in origins.Where(v => v.Campaign != null).Select(v => v.Campaign!.Value).Distinct().Order()) await data.LockCampaignAsync(campaign, token); foreach (Guid member in origins.Where(v => v.Member != null).Select(v => v.Member!.Value).Distinct().Order()) await data.LockNoticeMemberAsync(member, token); foreach (Guid product in origins.Where(v => v.Product != null).Select(v => v.Product!.Value).Distinct().Order()) await data.LockProductAsync(product, token); return true; }, ct);
            if (session.WriterBrand is Guid brand) await session.AdmitBrandWriterAsync(brand, ct);
        }
        int recovered = 0;
        foreach (var id in ids)
        {
            var origin = await ReadNoticeIdentityAsync(session, id.Delivery, null, ct);
            await LockNoticeOriginAsync(session, origin, ct);
            bool changed = await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireJobCommandAsync(origin.Brand, token); await data.LockJobAsync(id.Job, token);
                var job = await data.Data.Set<Entities.Job>().SingleAsync(row => row.Id == id.Job, token);
                DateTime now = await data.BrandClockAsync(origin.Brand, token); if (job.Status != "leased" || job.LeaseUntil > now) return false;
                await data.LockMessageAsync(id.Delivery, token); var row = await data.Data.Set<Entities.TelegramDelivery>().SingleAsync(row => row.Id == id.Delivery, token);
                bool uncertain = row.Status is "leased" or "uncertain"; string status = row.Status;
                if (uncertain) { row.Status = "uncertain"; row.LastErrorCode = "remote_outcome_unknown"; }
                string recoveredStatus = uncertain ? "failed" : status is "succeeded" or "failed" or "cancelled" ? status : "retry";
                job.Status = recoveredStatus; job.LockedBy = null; job.LeaseUntil = null; job.AvailableAt = now; job.CompletedAt = recoveredStatus == "retry" ? null : now; job.LastErrorCode = uncertain ? "remote_outcome_unknown" : "pre_dispatch_recovery"; return true;
            }, ct);
            if (changed) recovered++;
        }
        await session.CommitAsync(ct); return recovered;
    }
    private static async Task DeferAsync(DbSession session, JobLease lease, Guid delivery, int seconds, string reason, CancellationToken ct)
    {
        Guid brand = lease.Intent.BrandId!.Value;
        await ChangeNoticeAsync(session, brand, delivery, (row, now) => { row.Status = "retry"; row.NextAttemptAt = now.AddSeconds(seconds); row.LastErrorCode = reason; }, ct);
        bool changed = await ChangeNoticeJobAsync(session, brand, lease.Id, row => row.Attempts == lease.Attempt && row.Status is "leased" or "failed", (row, now) => { row.Status = "retry"; row.LockedBy = null; row.LeaseUntil = null; row.CompletedAt = null; row.AvailableAt = now.AddSeconds(seconds); row.LastErrorCode = reason; }, ct);
        if (!changed) throw new InvalidOperationException("Notice lease changed.");
    }
    private static async Task FinishWithoutSendAsync(DbSession session, JobLease lease, NoticeIdentity identity, string status, CancellationToken ct)
    {
        await ChangeNoticeAsync(session, identity.Brand, lease.Intent.OriginId!.Value, (row, now) => row.Status = status, ct);
        await OutboxStore.CompleteAsync(session, lease, ct);
        if (identity.Subscription is Guid consent) await CloseNoticeSubscriptionAsync(session, identity.Brand, consent, ct);
        if (identity.Campaign is Guid campaign) await CompleteCampaignAsync(session, campaign, ct);
    }
    private static Guid NoticeId(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static Task<NoticeIdentity> IdentityAsync(DbSession session, Guid delivery, BrandContext context, CancellationToken ct) => ReadNoticeIdentityAsync(session, delivery, context.BrandId, ct);
    private static Task<NoticeIdentity> ReadNoticeIdentityAsync(DbSession session, Guid delivery, Guid? brand, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => (from d in data.Data.Set<Entities.TelegramDelivery>() join sub in data.Data.Set<Entities.ProductStockSubscription>() on d.ProductStockSubscriptionId equals sub.Id into subscriptions from sub in subscriptions.DefaultIfEmpty() where d.Id == delivery && (brand == null || d.BrandId == brand && NoticePurposes.Contains(d.Purpose)) select new NoticeIdentity(d.CampaignId, d.UserId, d.ProductStockSubscriptionId, sub == null ? null : (Guid?)sub.ProductId, d.BrandId)).SingleAsync(token), ct);
    private static async Task LockNoticeOriginAsync(DbSession session, NoticeIdentity origin, CancellationToken ct)
    {
        await session.RunEfStepAsync(false, async (data, token) => { if (origin.Campaign is Guid campaign) await data.LockCampaignAsync(campaign, token); if (origin.Member is Guid member) await data.LockNoticeMemberAsync(member, token); if (origin.Product is Guid product) await data.LockProductAsync(product, token); return true; }, ct);
        await session.AdmitBrandWriterAsync(origin.Brand, ct);
        if (origin.Subscription is Guid subscription) await session.RunEfStepAsync(false, (data, token) => data.LockNoticeSubscriptionAsync(subscription, token), ct);
    }
    private static Task<bool> ChangeNoticeAsync(DbSession session, Guid brand, Guid id, Action<Entities.TelegramDelivery, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { await data.RequireJobCommandAsync(brand, token); await data.LockMessageAsync(id, token); var row = await data.Data.Set<Entities.TelegramDelivery>().SingleAsync(row => row.Id == id && row.BrandId == brand, token); change(row, await data.BrandClockAsync(brand, token)); return true; }, ct);
    private static Task<bool> ChangeNoticeJobAsync(DbSession session, Guid brand, Guid id, Func<Entities.Job, bool> predicate, Action<Entities.Job, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { await data.RequireJobCommandAsync(brand, token); await data.LockJobAsync(id, token); var row = await data.Data.Set<Entities.Job>().SingleAsync(row => row.Id == id, token); if (!predicate(row)) return false; change(row, await data.BrandClockAsync(brand, token)); return true; }, ct);
    private static Task<bool> CloseNoticeSubscriptionAsync(DbSession session, Guid brand, Guid id, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { await data.RequireJobCommandAsync(brand, token); var row = await data.Data.Set<Entities.ProductStockSubscription>().SingleAsync(row => row.Id == id, token); row.ClosedAt ??= await data.BrandClockAsync(brand, token); return true; }, ct);
    private static async Task<NoticeState> ReadNoticeAsync(DbSession session, Guid id, CancellationToken ct) => await session.RunEfStepAsync(false, async (data, token) =>
    {
        await data.LockMessageAsync(id, token);
        var row = await (from d in data.Data.Set<Entities.TelegramDelivery>() join b in data.Data.Set<Entities.Brand>() on d.BrandId equals b.Id join u in data.Data.Set<Entities.User>() on d.UserId equals u.Id into users from u in users.DefaultIfEmpty() join c in data.Data.Set<Entities.Campaign>() on d.CampaignId equals c.Id into campaigns from c in campaigns.DefaultIfEmpty() join sub in data.Data.Set<Entities.ProductStockSubscription>() on d.ProductStockSubscriptionId equals sub.Id into subscriptions from sub in subscriptions.DefaultIfEmpty() join p in data.Data.Set<Entities.Product>() on sub.ProductId equals p.Id into products from p in products.DefaultIfEmpty() join bp in data.Data.Set<Entities.BrandProduct>() on new { Product = (Guid?)p.Id, Brand = d.BrandId } equals new { Product = (Guid?)bp.ProductId, Brand = bp.BrandId } into listings from bp in listings.DefaultIfEmpty() where d.Id == id select new { d.Status, d.RecipientKind, d.StaffChatId, d.Purpose, Chat = u == null ? null : (long?)u.TelegramId, Blocked = u != null && (u.BlockedAt != null || u.BannedAt != null), OptedOut = u != null && u.MarketingOptOutAt != null, d.RecipientLanguage, MemberLanguage = u == null ? null : u.Language, b.Active, b.BotId, CampaignStatus = c == null ? null : c.Status, Text = c == null ? null : c.MessageText, Media = c == null ? null : c.MediaRef, Closed = sub != null && (sub.ClosedAt != null || sub.CancelledAt != null), ProductTitle = bp != null && bp.TitleMode == "override" ? bp.TitleOverride : p == null ? null : p.Title, CampaignLanguage = c == null ? null : c.Language, ActiveSince = c == null ? null : c.ActiveSince, LastSeen = u == null ? null : (DateTime?)u.LastSeenAt }).SingleAsync(token);
        string? language = row.MemberLanguage?.ToLowerInvariant().Split('-')[0];
        return new NoticeState(row.Status, row.RecipientKind, row.StaffChatId, row.Purpose, row.Chat, row.Blocked, row.OptedOut, row.RecipientLanguage ?? language ?? "en", row.Active, row.BotId, row.CampaignStatus, row.Text, row.Media, row.Closed, row.ProductTitle, row.CampaignLanguage, language, row.ActiveSince, row.LastSeen);
    }, ct);
    private sealed record NoticeIdentity(Guid? Campaign, Guid? Member, Guid? Subscription, Guid? Product, Guid Brand);
    private sealed record NoticeState(string Status, string Recipient, long? StaffChat, string Purpose, long? Chat, bool Blocked, bool OptedOut, string Language, bool Active, long Bot,
        string? CampaignStatus, string? Text, string? Media, bool ConsentClosed, string? ProductTitle, string? CampaignLanguage, string? MemberLanguage, DateTime? ActiveSince, DateTime? LastSeen);
}
