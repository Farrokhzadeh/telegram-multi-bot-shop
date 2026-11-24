using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
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
        Guid? scopeBrand = await session.AdmitClaimWriterAsync(true, ct);
        var lease = await session.RunEfStepAsync(true, (data, token) => data.ClaimPaymentMessageAsync(scopeBrand, worker, token), ct);
        await session.CommitAsync(ct); return lease;
    }
    public async Task<bool> BeginAsync(BrandContext context, JobLease lease, TimeSpan spacing, CancellationToken ct = default)
    {
        if (spacing < TimeSpan.FromMilliseconds(100) || spacing > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(spacing));
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        if (data.Status is "succeeded" or "cancelled" or "failed" or "uncertain" or "leased") return false;
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
        if (!data.Reachable) { await FinishAsync(session, context, lease, id, "cancelled", "recipient_blocked", ct); await session.CommitAsync(ct); return false; }
        long inFlight = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.TelegramDelivery>().LongCountAsync(value => value.OrderId == data.Order && value.Purpose == "order_status" && value.Id != id && value.Status == "leased", token), ct);
        if (inFlight > 0) { await DeferAsync(session, context, lease, id, 1, "order_backoff", ct); await session.CommitAsync(ct); return false; }
        bool uncertain = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.TelegramDelivery>().AnyAsync(value => value.OrderId == data.Order && value.Purpose == "order_status" && value.Id != id && value.Status == "uncertain" && value.Action == "send", token), ct);
        if (uncertain && data.Target is null && data.Audit is null) { await FinishAsync(session, context, lease, id, "failed", "recovery_required", ct); await session.CommitAsync(ct); return false; }
        var timing = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId).Select(value => new { Last = ef.Data.Set<Entities.TelegramDelivery>().Where(delivery => delivery.BrandId == value.Id).Max(delivery => delivery.LastAttemptAt), Rate = ef.Data.Set<Entities.TelegramDelivery>().Where(delivery => delivery.BrandId == value.Id && delivery.LastErrorCode == "rate_limited").Select(delivery => (DateTime?)delivery.NextAttemptAt).Max(), Now = EfValues.UtcNow() }).SingleAsync(token), ct);
        DateTime next = timing.Last?.AddMilliseconds(Math.Ceiling(spacing.TotalMilliseconds)) ?? timing.Now;
        if (timing.Rate is DateTime rate && rate > next) next = rate;
        long wait = (long)Math.Max(0, Math.Ceiling((next - timing.Now).TotalSeconds));
        if (wait > 0) { await DeferAsync(session, context, lease, id, checked((int)Math.Min(wait, 3600)), "bot_backoff", ct); await session.CommitAsync(ct); return false; }
        await PaymentMessageQueries.ChangeAsync(session, context, id, lease, (row, _, now) => { row.Status = "leased"; row.Attempts = lease.Attempt; row.LastAttemptAt = now; }, ct);
        await session.CommitAsync(ct); return true;
    }
    public async Task<PaymentMessageDispatch?> ReadForSendAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
        if (data.Status != "leased" || data.Attempt != lease.Attempt) throw new InvalidOperationException("Stale payment message dispatch.");
        if (!data.Reachable) { await FinishAsync(session, context, lease, id, "cancelled", "recipient_blocked", ct); await session.CommitAsync(ct); return null; }
        bool manifestOwnsMessage = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Order>().Where(value => value.Id == data.Order).Select(value => value.ReleaseAuthorizedAt != null).SingleAsync(token), ct);
        if (manifestOwnsMessage)
        {
            bool unknown = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.TelegramDelivery>().Where(value => value.Id == id).Select(value => value.LastErrorCode == "remote_outcome_unknown").SingleAsync(token), ct);
            await FinishAsync(session, context, lease, id, unknown ? "uncertain" : "cancelled", unknown ? "remote_outcome_unknown" : "credential_delivery_active", ct); await session.CommitAsync(ct); return null;
        }
        bool recovered = data.Audit is Guid audit && await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.TelegramDelivery>().AnyAsync(value => value.OrderId == data.Order && value.Id != id && value.Purpose == "order_status" && value.Status == "succeeded" && ef.Data.Set<Entities.AuditEvent>().Any(entry => entry.Id == audit && value.SentAt >= entry.CreatedAt), token), ct);
        if (recovered) { await FinishAsync(session, context, lease, id, "cancelled", "known_success", ct); await session.CommitAsync(ct); return null; }
        if (data.Target is null && data.Audit is null)
        {
            long? anchor = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.TelegramDelivery>().Where(value => value.OrderId == data.Order && value.Purpose == "order_status" && value.Status == "succeeded" && value.TelegramMessageId != null).OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Select(value => value.TelegramMessageId).FirstOrDefaultAsync(token), ct);
            if (anchor is long target)
            {
                await FinishAsync(session, context, lease, id, "cancelled", "saved_anchor", ct);
                Guid redirected = await PaymentMessageQueries.CreateAsync(session, context, data.Member, data.Order, "instruction-anchor:" + id.ToString("D"), target, null, ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, redirected), redirected, DateTime.UtcNow, ct); await session.CommitAsync(ct); return null;
            }
        }
        var current = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Order>().Where(value => value.Id == data.Order).Select(value => new { State = value.PaymentStatus, Resolution = value.ResolutionKind, Due = value.PaymentDueAt!.Value, Now = EfValues.UtcNow(), Invoice = ef.Data.Set<Entities.Payment>().Where(payment => payment.OrderId == value.Id && payment.IdempotencyRef == "order:" + value.Id.ToString() && payment.PaymentMethod == "crypto").Select(payment => payment.HostedInvoiceUrl).FirstOrDefault() }).SingleAsync(token), ct);
        if (current.State == "awaiting_payment" && current.Now > current.Due)
        { await DeferAsync(session, context, lease, id, 1, "expiry_pending", ct); await session.CommitAsync(ct); return null; }
        await session.RunEfStepAsync(true, async (ef, token) =>
        {
            await ef.RequireOrderCommandAsync(context, data.Order, token);
            var others = await ef.Data.Set<Entities.TelegramDelivery>().Where(value => value.OrderId == data.Order && value.Purpose == "order_status" && value.Id != id && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
            if (others.Length > 1000) ef.RejectScope("Payment message projection bound exceeded.");
            Guid[] ids = others.Select(value => value.Id).ToArray();
            var jobs = await ef.Data.Set<Entities.Job>().Where(value => value.TelegramDeliveryId != null && ids.Contains(value.TelegramDeliveryId.Value) && (value.Status == "pending" || value.Status == "retry")).Take(1001).ToArrayAsync(token);
            if (jobs.Length > 1000) ef.RejectScope("Payment message job bound exceeded.");
            DateTime now = await ef.BrandClockAsync(context.BrandId, token);
            foreach (var job in jobs) { job.Status = "cancelled"; job.CompletedAt = now; }
            foreach (var row in others) { row.Status = "cancelled"; row.LastErrorCode = "latest_projection"; }
            var active = await ef.Data.Set<Entities.TelegramDelivery>().SingleAsync(value => value.Id == id, token); active.EligibilityCheckedAt = now; return true;
        }, ct);
        string text = PaymentDeadlinePolicy.Text(data.Order, current.State, current.Resolution, current.Due, data.Language);
        if (current.State == "awaiting_payment" && current.Invoice is string checkout) text += "\n" + checkout;
        await session.CommitAsync(ct); return new(id, data.Order, context.BotId, data.Chat, data.Target, text);
    }
    public async Task RecordAsync(BrandContext context, JobLease lease, PaymentMessageOutcome outcome, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(outcome.Kind) || (outcome.Kind == PaymentMessageOutcomeKind.Sent) != outcome.MessageId.HasValue || (outcome.Kind == PaymentMessageOutcomeKind.RateLimited) != outcome.RetryAfterSeconds.HasValue || outcome.MessageId <= 0 || outcome.RetryAfterSeconds is < 1 or > 3600) throw new ArgumentException("Invalid payment message outcome.");
        Guid id = Origin(context, lease); await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, id, ct);
        var attempt = await session.RunEfStepAsync(false, async (ef, token) => { await ef.LockJobAsync(lease.Id, token); return await ef.Data.Set<Entities.Job>().Where(value => value.Id == lease.Id && value.TelegramDeliveryId == id).Select(value => value.Attempts).SingleAsync(token); }, ct);
        if (data.Status == "succeeded") { if (outcome.Kind == PaymentMessageOutcomeKind.Sent && outcome.MessageId != data.Message) throw new InvalidOperationException("Known message receipt conflict."); return; }
        if (data.Attempt != lease.Attempt || attempt != lease.Attempt || data.Status is not ("leased" or "uncertain" or "retry" or "failed")) throw new InvalidOperationException("Stale payment message outcome.");
        if (data.Status is "retry" or "failed" && outcome.Kind != PaymentMessageOutcomeKind.Sent) throw new InvalidOperationException("Only a late known receipt can resolve a finished attempt.");
        if (outcome.Kind == PaymentMessageOutcomeKind.Sent && data.Target is long target && outcome.MessageId != target) throw new InvalidOperationException("Wrong saved edit target.");
        if (outcome.Kind == PaymentMessageOutcomeKind.RateLimited || (outcome.Kind == PaymentMessageOutcomeKind.Unknown && data.Target is not null))
            await DeferAsync(session, context, lease, id, outcome.RetryAfterSeconds ?? 1, outcome.Kind == PaymentMessageOutcomeKind.RateLimited ? "rate_limited" : "remote_outcome_unknown", ct);
        else
        {
            string state = outcome.Kind switch { PaymentMessageOutcomeKind.Sent => "succeeded", PaymentMessageOutcomeKind.Blocked => "cancelled", PaymentMessageOutcomeKind.Unknown => "uncertain", _ => "failed" };
            string error = outcome.Kind switch { PaymentMessageOutcomeKind.Uneditable => "uneditable", PaymentMessageOutcomeKind.Blocked => "recipient_blocked", PaymentMessageOutcomeKind.Unknown => "remote_outcome_unknown", _ => "none" };
            await PaymentMessageQueries.ChangeAsync(session, context, id, lease, (row, job, now) =>
            {
                row.Status = state; row.TelegramMessageId = outcome.MessageId;
                if (state == "succeeded") row.SentAt = now;
                row.LastErrorCode = error; job.Status = state == "uncertain" ? "failed" : state; job.LockedBy = null; job.LeaseUntil = null; job.CompletedAt = now; job.LastErrorCode = error;
            }, ct);
            if (outcome.Kind == PaymentMessageOutcomeKind.Blocked) await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequireOrderCommandAsync(context, data.Order, token);
                var member = await ef.Data.Set<Entities.User>().SingleAsync(value => value.Id == data.Member, token); member.BlockedAt ??= await ef.BrandClockAsync(context.BrandId, token); return true;
            }, ct);
        }
        await session.CommitAsync(ct);
    }
    public async Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct); var data = await LockedAsync(session, context, delivery, ct);
        string state = data.Status == "leased" ? data.Target is null ? "uncertain" : "retry" : data.Status;
        await session.RunEfStepAsync(true, async (ef, token) =>
        {
            await ef.RequireOrderCommandAsync(context, data.Order, token); await ef.LockDeliveryJobsAsync(delivery, token);
            var jobs = await ef.Data.Set<Entities.Job>().Where(value => value.TelegramDeliveryId == delivery && value.Status == "leased" && value.LeaseUntil <= EfValues.UtcNow()).Take(1001).ToArrayAsync(token);
            if (jobs.Length > 1000) ef.RejectScope("Payment recovery job bound exceeded.");
            DateTime now = await ef.BrandClockAsync(context.BrandId, token); string target = state == "uncertain" ? "failed" : state;
            foreach (var job in jobs) { job.Status = target; job.LockedBy = null; job.LeaseUntil = null; job.AvailableAt = now; job.CompletedAt = target == "failed" ? now : null; job.LastErrorCode = "remote_outcome_unknown"; }
            if (jobs.Length > 0) { var row = await ef.Data.Set<Entities.TelegramDelivery>().SingleAsync(value => value.Id == delivery, token); row.Status = state; row.NextAttemptAt = now; row.LastErrorCode = "remote_outcome_unknown"; }
            return true;
        }, ct);
        await session.CommitAsync(ct);
    }
    public async Task<Guid> AuthorizeFallbackAsync(BrandContext context, AuthenticatedAdmin actor, Guid failedDelivery, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var data = await LockedAsync(session, context, failedDelivery, ct);
        bool fresh = await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.DeliveryRecovery, AuditEntity.Order, data.Order, context.BrandId, AuditReason.Reconciliation, Guid.NewGuid(), AdminCommand.Hash("payment-status-fallback", failedDelivery.ToString("D"), data.Order.ToString("D"))), ct);
        if (fresh && data.Status is not ("failed" or "uncertain")) throw new InvalidOperationException("Payment message needs no fallback.");
        Guid id = await PaymentMessageQueries.CreateAsync(session, context, data.Member, data.Order, "instruction-recovery:" + command.ToString("D"), null, command, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct); await session.CommitAsync(ct); return id;
    }
    private static Guid Origin(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static async Task<State> LockedAsync(DbSession session, BrandContext context, Guid id, CancellationToken ct)
    {
        var before = await ReadAsync(session, context, id, ct);
        await session.RunEfStepAsync(false, (ef, token) => ef.LockUserAsync(before.Member, context.BrandId, token), ct);
        await session.AdmitWriterAsync(context, ct);
        await session.RunEfStepAsync(false, async (ef, token) => { await ef.LockBrandUpdateAsync(context.BrandId, token); await ef.LockOrderAsync(before.Order, token); return await ef.LockMessageAsync(id, token); }, ct);
        return await ReadAsync(session, context, id, ct);
    }
    private static async Task<State> ReadAsync(DbSession session, BrandContext context, Guid id, CancellationToken ct)
    {
        State? state = await session.RunEfStepAsync(false, (ef, token) =>
            (from value in ef.Data.Set<Entities.TelegramDelivery>()
             join user in ef.Data.Set<Entities.User>() on value.UserId equals user.Id
             join brand in ef.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             join order in ef.Data.Set<Entities.Order>() on value.OrderId equals order.Id
             where value.Id == id && value.BrandId == context.BrandId && brand.BotId == context.BotId && value.Purpose == "order_status" && order.UserId == user.Id && order.PaymentDueAt != null
             select new State(order.Id, user.Id, user.TelegramId, value.TargetMessageId, value.TelegramMessageId, value.Status, value.Attempts, user.Language, brand.Active && user.BlockedAt == null && user.BannedAt == null, value.AuditEventId)).SingleOrDefaultAsync(token), ct);
        return state ?? throw new UnauthorizedAccessException();
    }
    private static async Task DeferAsync(DbSession session, BrandContext context, JobLease lease, Guid delivery, int seconds, string reason, CancellationToken ct) => await PaymentMessageQueries.ChangeAsync(session, context, delivery, lease, (row, job, now) =>
    {
        row.Status = "retry"; row.NextAttemptAt = now.AddSeconds(seconds); row.LastErrorCode = reason;
        job.Status = "retry"; job.LockedBy = null; job.LeaseUntil = null; job.AvailableAt = now.AddSeconds(seconds); job.LastErrorCode = reason;
    }, ct);
    private static async Task FinishAsync(DbSession session, BrandContext context, JobLease lease, Guid delivery, string state, string reason, CancellationToken ct) => await PaymentMessageQueries.ChangeAsync(session, context, delivery, lease, (row, job, now) =>
    {
        row.Status = state; row.LastErrorCode = reason; job.Status = state == "uncertain" ? "failed" : state; job.LockedBy = null; job.LeaseUntil = null; job.CompletedAt = now; job.LastErrorCode = reason;
    }, ct);
    private sealed record State(Guid Order, Guid Member, long Chat, long? Target, long? Message, string Status, long Attempt, string Language, bool Reachable, Guid? Audit);
}
