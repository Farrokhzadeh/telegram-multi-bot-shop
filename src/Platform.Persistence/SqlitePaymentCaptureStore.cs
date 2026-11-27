using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePaymentCaptureStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions;

    public async Task<PaymentCapture> CaptureAsync(BrandContext context, long revision, VerifiedPaymentNotice notice,
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        State before = await sessions.RunAsync(false, (_, tx, token) =>
            ReadAsync(tx, context, revision, null, notice.Origin, token), ct);
        if (!Matches(before, notice)) throw new PaymentNoticeRejectedException();
        if (before.Pending)
            await ResumeArchiveAsync(context, revision, before.Id, archive, ct);
        if (before.Canonical is not null &&
            CryptographicOperations.FixedTimeEquals(before.Canonical, notice.CanonicalHash))
            return new PaymentCapture(PaymentCaptureOutcome.Replayed, before.Id);
        byte[] content = notice.Body.ToArray();
        try
        {
            DateTime observed = DateTime.UtcNow;
            if (before.Order is not null && before.OrderStatus == "awaiting_payment" &&
                before.Deadline is DateTime due && due >= observed && Qualifies(before, notice))
            {
                PaymentCapture? staged = await PersistAsync(context, revision, notice, before, content, null, observed, ct);
                if (staged is not null)
                {
                    await ResumeArchiveAsync(context, revision, before.Id, archive, ct);
                    return staged;
                }
            }
            string asset = await PaymentEvidenceArchive.SavePlaintextAsync(archive, before.Id, context.BrandId, notice.RawHash, content, ct);
            return await PersistAsync(context, revision, notice, before, content, asset, observed, ct)
                ?? throw new PaymentNoticeRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    public async Task ResumeArchiveAsync(BrandContext context, long revision, Guid payment,
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Pending? proof = await sessions.RunAsync<Pending?>(false, async (_, tx, token) =>
        {
            State state = await ReadAsync(tx, context, revision, payment, null, token);
            if (!state.Pending) return null;
            var pending = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) =>
                data.Data.Set<Entities.Payment>().Where(value => value.Id == payment)
                    .Select(value => new Pending(value.EvidenceDigest, value.PendingEvidence)).SingleOrDefaultAsync(cancel), token);
            if (pending?.Digest is null || pending.Content is null) throw new PaymentNoticeRejectedException();
            return pending;
        }, ct);
        if (proof is not { Digest: { } digest, Content: { } content }) return;
        try
        {
            string asset = await PaymentEvidenceArchive.SavePlaintextAsync(archive, payment, context.BrandId, digest, content, ct);
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                State current = await ReadAsync(tx, context, revision, payment, null, token);
                await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
                {
                    await data.RequirePaymentCommandAsync(context, payment, cancel);
                    var row = await data.Data.Set<Entities.Payment>().SingleOrDefaultAsync(value => value.Id == payment && value.EvidenceDigest == digest, cancel);
                    if (row is null || !current.Pending && row.EvidenceAssetRef != asset) throw new PaymentNoticeRejectedException();
                    if (!current.Pending) return true;
                    row.EvidenceAssetRef = asset;
                    row.PendingEvidence = null;
                    return true;
                }, token);
                return 0;
            }, ct);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private async Task<PaymentCapture?> PersistAsync(BrandContext context, long revision, VerifiedPaymentNotice notice,
        State before, byte[] content, string? asset, DateTime observed, CancellationToken ct) =>
        await sessions.RunAsync(true, async (_, tx, token) =>
        {
            State state = await ReadAsync(tx, context, revision, before.Id, null, token);
            if (state.Member != before.Member || !Matches(state, notice) || state.Pending)
                throw new PaymentNoticeRejectedException();
            if (state.Canonical is not null &&
                CryptographicOperations.FixedTimeEquals(state.Canonical, notice.CanonicalHash))
                return new PaymentCapture(PaymentCaptureOutcome.Replayed, state.Id);
            if (asset is null && (state.OrderStatus != "awaiting_payment" || state.Deadline < observed))
                return null;
            if (state.Status == "settled")
            {
                Guid replayEvent = new(notice.CanonicalHash.AsSpan(0, 16));
                await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
                {
                    await data.RequirePaymentCommandAsync(context, state.Id, cancel);
                    if (!await data.Data.Set<Entities.AuditEvent>().AnyAsync(value => value.Id == replayEvent, cancel)) data.Data.Add(new Entities.AuditEvent { Id = replayEvent, ActorKind = "system", BrandId = context.BrandId, Action = "update", EntityKind = "payment", EntityId = state.Id, Reason = "post_settlement_notice", CommandSha256 = notice.RawHash, CorrelationId = state.Id });
                    return true;
                }, token);
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, state.Id),
                    replayEvent, observed, token);
                return new PaymentCapture(PaymentCaptureOutcome.Replayed, state.Id);
            }
            if (state.Provider is null)
            {
                await SqlitePaymentOperations.PaymentAsync(tx, context, state.Id, row =>
                {
                    if (row.ProviderPaymentRef != null || row.Status is not ("created" or "awaiting")) throw new PaymentNoticeRejectedException();
                    row.ProviderPaymentRef = notice.ProviderPayment; row.ExpectedNetworkAmount = notice.ExpectedTokens; row.Status = "awaiting";
                }, token);
                state = state with { Provider = notice.ProviderPayment, Expected = notice.ExpectedTokens, Status = "awaiting" };
            }
            string assetCode = state.Asset switch
            {
                "USDTBSC" => "usdtbsc",
                "USDTTRC20" => "usdttrc20",
                _ => throw new PaymentNoticeRejectedException()
            };
            var invoice = new InvoiceExpectation(state.Provider!, state.Origin, state.Due, assetCode, state.Expected!.Value);
            PaymentEvidenceDecision decision = PaymentEvidencePolicy.Evaluate(invoice, notice);
            if (state.Status == "review") decision = PaymentEvidenceDecision.Review;
            bool identity = notice.QuoteCurrency == "usd" && notice.QuoteAmount == state.Due &&
                notice.Asset == assetCode && notice.ExpectedTokens == state.Expected;
            DateTime? first = state.First ?? (identity && notice.ReceivedTokens > 0 ? observed : null);
            if (state.Confirmed is not null && decision == PaymentEvidenceDecision.Pending)
                return new PaymentCapture(PaymentCaptureOutcome.Replayed, state.Id);
            await SqlitePaymentOperations.PaymentAsync(tx, context, state.Id, row =>
            {
                if (row.Status == "settled") throw new PaymentNoticeRejectedException();
                row.Status = decision == PaymentEvidenceDecision.Review ? "review" : first is null ? "awaiting" : "confirming"; row.ProviderStatusCode = notice.Status;
                if (identity && (row.NetworkAmount is null || row.NetworkAmount < notice.ReceivedTokens)) row.NetworkAmount = notice.ReceivedTokens;
                if (notice.Confirmations is long confirmations) row.ProviderConfirmations = Math.Max(row.ProviderConfirmations ?? 0, confirmations);
                row.FirstPaymentObservedAt = first;
                if (decision == PaymentEvidenceDecision.Ready) { row.ConfirmedAt ??= observed; row.AmountReceived = row.AmountDue; }
                row.IpnKeyId = notice.KeyId; row.IpnSignature = notice.Signature; row.IpnPayloadSha256 = notice.CanonicalHash; row.IpnVerifiedAt = observed; row.EvidenceAssetRef = asset; row.EvidenceDigest = notice.RawHash;
                row.PendingEvidence = asset is null ? content.ToArray() : null;
            }, token);
            if (state.Order is Guid order && first is DateTime observedFirst &&
                state.Deadline is DateTime deadline && observedFirst <= deadline &&
                state.OrderStatus == "awaiting_payment")
            {
                await SqlitePaymentOperations.OrderAsync(tx, order, row =>
                {
                    if (row.PaymentStatus != "awaiting_payment") throw new PaymentNoticeRejectedException();
                    row.PaymentStatus = "confirmation_pending";
                }, observed, token);
                await QueueInstructionAsync(tx, context, state.Member, order, token);
            }
            Guid eventId = new(notice.CanonicalHash.AsSpan(0, 16));
            await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, state.Id),
                eventId, observed, token);
            return new PaymentCapture(decision switch
            {
                PaymentEvidenceDecision.Ready => PaymentCaptureOutcome.Ready,
                PaymentEvidenceDecision.Review => PaymentCaptureOutcome.Review,
                _ => PaymentCaptureOutcome.Pending
            }, state.Id);
        }, ct);

    private static async Task QueueInstructionAsync(SqliteTransaction tx, BrandContext context, Guid member,
        Guid order, CancellationToken ct)
    {
        Guid actual = await SqliteSessionFactory.StepEfAsync(tx, true, (data, token) => PaymentInstructionQueries.EnqueueAsync(data, context, order, "processing", token), ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, actual), actual, DateTime.UtcNow, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, long revision,
        Guid? payment, string? origin, CancellationToken ct)
    {
        var state = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) =>
            (from value in data.Data.Set<Entities.Payment>()
             join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
             from order in orders.DefaultIfEmpty()
             join user in data.Data.Set<Entities.User>() on (value.TopupUserId ?? (Guid?)order.UserId) equals user.Id
             join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
             where brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active && brand.PolicyVersion == revision && ((payment != null && value.Id == payment) || (origin != null && value.IdempotencyRef == origin))
             select new State(value.Id, user.Id, value.OrderId, value.PaymentMethod, value.QuoteCurrency, value.AmountDue, value.NetworkAsset ?? "", value.ProviderPaymentRef, value.ExpectedNetworkAmount, value.IdempotencyRef, value.Status, value.IpnPayloadSha256, value.FirstPaymentObservedAt, value.ConfirmedAt, order == null ? null : order.PaymentDueAt, order == null ? null : order.PaymentStatus, value.ProviderCode, value.PendingEvidence != null, value.HostedInvoiceStartedAt != null, value.HostedInvoiceRef)).SingleOrDefaultAsync(token), ct);
        return state ?? throw new UnauthorizedAccessException("The payment is unavailable.");
    }

    private static bool Qualifies(State payment, VerifiedPaymentNotice notice) =>
        notice.ReceivedTokens > 0 && notice.QuoteCurrency == "usd" && payment.Due == notice.QuoteAmount &&
        string.Equals(payment.Asset, notice.Asset, StringComparison.OrdinalIgnoreCase) &&
        (payment.Expected is null || payment.Expected == notice.ExpectedTokens);

    private static bool Matches(State payment, VerifiedPaymentNotice notice) =>
        payment.Method == "crypto" && payment.Currency == "USD" && payment.ProviderCode == "nowpayments" &&
        payment.Origin == notice.Origin &&
        (!payment.HostedStarted || payment.HostedReference is not null && payment.HostedReference == notice.ProviderInvoice) &&
        (payment.Provider is null
            ? payment.HostedReference is not null && notice.QuoteCurrency == "usd" &&
                payment.Due == notice.QuoteAmount && string.Equals(payment.Asset, notice.Asset, StringComparison.OrdinalIgnoreCase)
            : payment.Provider == notice.ProviderPayment && payment.Expected is not null);

    private sealed record Pending(byte[]? Digest, byte[]? Content)
    {
        public override string ToString() => "Pending payment evidence [redacted]";
    }

    private sealed record State(Guid Id, Guid Member, Guid? Order, string Method, string Currency, decimal Due, string Asset,
        string? Provider, decimal? Expected, string Origin, string Status, byte[]? Canonical, DateTime? First,
        DateTime? Confirmed, DateTime? Deadline, string? OrderStatus, string? ProviderCode, bool Pending,
        bool HostedStarted, string? HostedReference);
}
