using static Platform.Persistence.PaymentEvidenceArchive;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Platform.Core;

namespace Platform.Persistence;

public enum PaymentCaptureOutcome { Pending, Ready, Review, Replayed }
public sealed record PaymentCapture(PaymentCaptureOutcome Outcome, Guid PaymentId);

public sealed partial class PaymentEvidenceStore(DbSessionFactory sessions)
{
    public Task<Guid> CreateTopupAsync(BrandContext context, long actor, Guid payment, Money amount, CryptoAsset network, CancellationToken ct = default)
        => CreateTopupCoreAsync(context, actor, payment, amount, network, null, ct);
    public Task<Guid> CreateTopupForBrandAsync(BrandContext context, long expectedRevision, long actor, Guid payment, Money amount, CryptoAsset network, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return CreateTopupCoreAsync(context, actor, payment, amount, network, expectedRevision, ct);
    }
    private async Task<Guid> CreateTopupCoreAsync(BrandContext context, long actor, Guid payment, Money amount, CryptoAsset network, long? writerRevision, CancellationToken ct)
    {
        if (payment == Guid.Empty || amount.Currency != Currency.Usd || amount.Amount <= 0 || decimal.Round(amount.Amount, 2) != amount.Amount || !Enum.IsDefined(network)) throw new ArgumentException("Invalid topup intent.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await LockMemberAsync(session, context, actor, ct);
            BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(BinaryPrimitives.ReadInt64BigEndian(payment.ToByteArray(bigEndian: true)))), ct);
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
            else await session.AdmitWriterAsync(context, ct);
            var prior = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Payment>().Where(value => value.Id == payment && value.TopupUserId != null).Select(value => new Topup(value.TopupUserId!.Value, value.AmountDue, value.NetworkAsset!)).SingleOrDefaultAsync(token), ct);
            string asset = network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20";
            if (prior is not null)
            {
                if (prior.Member != member.Id || prior.Amount != amount.Amount || prior.Asset != asset) Reject(session);
                return payment;
            }
            var policy = await session.RunEfStepAsync(false, async (data, token) =>
            {
                await data.LockBrandUpdateAsync(context.BrandId, token);
                return await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active).Select(value => new { Crypto = value.AllowCrypto, Topup = value.AllowWalletTopup, Zero = value.ZeroBalanceTopup, Balance = data.Data.Set<Entities.Wallet>().Where(wallet => wallet.UserId == member.Id).Select(wallet => (decimal?)wallet.Balance).SingleOrDefault() ?? 0m }).SingleAsync(token);
            }, ct);
            if (member.Blocked || member.Banned || !policy.Crypto || !policy.Topup || (!policy.Zero && policy.Balance <= 0)) Reject(session);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireMembershipAsync(context, actor, false, false, token);
                data.Data.Add(new Entities.Payment { Id = payment, TopupUserId = member.Id, PaymentMethod = "crypto", QuoteCurrency = "USD", AmountDue = amount.Amount, IdempotencyRef = "topup:" + payment.ToString("D"), NetworkAsset = asset });
                return true;
            }, ct);
            await session.CommitAsync(ct);
            return payment;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public Task BindInvoiceAsync(DbSession session, BrandContext context, long actor, Guid payment, string providerPayment, decimal expectedTokens, CancellationToken ct = default)
        => BindInvoiceCoreAsync(session, context, actor, payment, providerPayment, expectedTokens, null, ct);
    public Task BindInvoiceForBrandAsync(DbSession session, BrandContext context, long expectedRevision, long actor, Guid payment, string providerPayment, decimal expectedTokens, CancellationToken ct = default)
        => BindInvoiceCoreAsync(session, context, actor, payment, providerPayment, expectedTokens, expectedRevision, ct);
    private async Task BindInvoiceCoreAsync(DbSession session, BrandContext context, long actor, Guid payment, string providerPayment, decimal expectedTokens, long? writerRevision, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(providerPayment) || providerPayment.Length > 64 || !providerPayment.All(char.IsAsciiDigit) || expectedTokens <= 0) throw new ArgumentException("Invalid trusted invoice binding.");
            await LockMemberAsync(session, context, actor, ct);
            BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
            else await session.AdmitWriterAsync(context, ct);
            Payment data = await LoadAsync(session, context, payment, null, true, ct);
            if (data.Member != member.Id || data.Method != "crypto" || data.Currency != "USD") Reject(session);
            if (data.Provider is not null)
            {
                if (data.ProviderCode != "nowpayments" || data.Provider != providerPayment || data.Expected != expectedTokens) Reject(session);
                return;
            }
            if (data.HostedStarted || data.Status is not ("created" or "awaiting")) Reject(session);
            await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequirePaymentCommandAsync(context, payment, token);
                var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == payment, token);
                row.ProviderCode = "nowpayments"; row.ProviderPaymentRef = providerPayment; row.ExpectedNetworkAmount = expectedTokens; row.Status = "awaiting";
                return true;
            }, ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public Task<PaymentCapture> CaptureAsync(BrandContext context, VerifiedPaymentNotice notice,
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, CancellationToken ct = default) => CaptureCoreAsync(context, notice, PaymentEvidenceArchive.Plain(archive), null, ct);
    public Task<PaymentCapture> CaptureForBrandAsync(BrandContext context, long expectedRevision, VerifiedPaymentNotice notice,
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return CaptureCoreAsync(context, notice, PaymentEvidenceArchive.Plain(archive), expectedRevision, ct);
    }

    private async Task<PaymentCapture> CaptureCoreAsync(BrandContext context, VerifiedPaymentNotice notice,
        Func<Evidence, CancellationToken, Task<string>> archive, long? writerRevision, CancellationToken ct)
    {
        Payment before;
        DateTime observedAt;
        await using (var preflight = await sessions.BeginAsync(ct))
        {
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(preflight, context, revision, ct);
            else await preflight.AdmitWriterAsync(context, ct);
            before = await LoadAsync(preflight, context, null, notice.Origin, false, ct);
            observedAt = await preflight.RunEfStepAsync(false, (data, token) => data.BrandClockAsync(context.BrandId, token), ct);
        }
        if (!Matches(before, notice)) throw new PaymentNoticeRejectedException();
        byte[] content = notice.Body.ToArray();
        var proof = new Evidence(before.Id, context.BrandId, Convert.ToHexStringLower(notice.RawHash), content);
        try
        {
            if (before.ArchivePending)
                await ResumeCoreAsync(context, before.Id, archive, writerRevision, ct);
            if (before.OrderStatus == "awaiting_payment" && before.Deadline >= observedAt && Qualifies(before, notice))
            {
                var staged = await PersistAsync(context, notice, before, observedAt, null, proof, writerRevision, ct);
                if (staged is not null)
                {
                    await ResumeCoreAsync(context, before.Id, archive, writerRevision, ct);
                    return staged;
                }
            }
            string asset = await PaymentEvidenceArchive.SaveAsync(archive, proof, ct);
            return await PersistAsync(context, notice, before, observedAt, asset, proof, writerRevision, ct) ?? throw new PaymentNoticeRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private async Task<PaymentCapture?> PersistAsync(BrandContext context, VerifiedPaymentNotice notice, Payment before, DateTime observedAt,
        string? asset, Evidence proof, long? writerRevision, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await session.RunEfStepAsync(false, (data, token) => data.LockUserAsync(before.Member, context.BrandId, token), ct);
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
            else await session.AdmitWriterAsync(context, ct);
            if (before.Order is Guid order) await session.RunEfStepAsync(false, (data, token) => data.LockOrderAsync(order, token), ct);
            Payment data = await LoadAsync(session, context, before.Id, null, true, ct);
            if (data.Member != before.Member || !Matches(data, notice)) Reject(session);
            if (data.Canonical is not null && CryptographicOperations.FixedTimeEquals(data.Canonical, notice.CanonicalHash))
                return new(PaymentCaptureOutcome.Replayed, data.Id);
            if (data.ArchivePending) throw new PaymentNoticeRejectedException();
            if (asset is null && (data.OrderStatus != "awaiting_payment" || data.Deadline < observedAt)) return null;
            if (data.Provider is null)
            {
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequirePaymentCommandAsync(context, data.Id, token);
                    var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == data.Id, token);
                    row.ProviderPaymentRef = notice.ProviderPayment; row.ExpectedNetworkAmount = notice.ExpectedTokens; row.Status = "awaiting";
                    return true;
                }, ct);
                data = data with { Provider = notice.ProviderPayment, Expected = notice.ExpectedTokens, Status = "awaiting" };
            }
            string expectedAsset = data.Asset switch { "USDTTRC20" => "usdttrc20", "USDTBSC" => "usdtbsc", _ => throw new PaymentNoticeRejectedException() };
            var decision = PaymentEvidencePolicy.Evaluate(new(data.Provider!, data.Origin, data.Due, expectedAsset, data.Expected!.Value), notice);
            DateTime now = await session.RunEfStepAsync(false, (ef, token) => ef.BrandClockAsync(context.BrandId, token), ct);
            Guid eventId = new(notice.CanonicalHash.AsSpan(0, 16));
            if (data.Status == "settled")
            {
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequirePaymentCommandAsync(context, data.Id, token);
                    if (!await ef.Data.Set<Entities.AuditEvent>().AnyAsync(value => value.Id == eventId, token)) ef.Data.Add(new Entities.AuditEvent { Id = eventId, ActorKind = "system", BrandId = context.BrandId, Action = "update", EntityKind = "payment", EntityId = data.Id, Reason = "post_settlement_notice", CommandSha256 = notice.RawHash, CorrelationId = data.Id });
                    return true;
                }, ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, data.Id), eventId, now, ct);
                await session.CommitAsync(ct);
                return new(PaymentCaptureOutcome.Replayed, data.Id);
            }
            if (data.Status == "review") decision = PaymentEvidenceDecision.Review;
            bool identity = notice.QuoteCurrency == "usd" && notice.QuoteAmount == data.Due && notice.Asset == expectedAsset && notice.ExpectedTokens == data.Expected;
            DateTime? first = data.First ?? (identity && notice.ReceivedTokens > 0 ? observedAt : null);
            if (data.Confirmed is not null && decision == PaymentEvidenceDecision.Pending) return new(PaymentCaptureOutcome.Replayed, data.Id);
            await session.RunEfStepAsync(true, async (ef, token) =>
            {
                await ef.RequirePaymentCommandAsync(context, data.Id, token);
                var row = await ef.Data.Set<Entities.Payment>().SingleAsync(value => value.Id == data.Id, token);
                row.Status = decision == PaymentEvidenceDecision.Review ? "review" : first is null ? "awaiting" : "confirming"; row.ProviderStatusCode = notice.Status;
                if (identity) row.NetworkAmount = Math.Max(row.NetworkAmount ?? 0m, notice.ReceivedTokens);
                if (notice.Confirmations is long confirmations) row.ProviderConfirmations = Math.Max(row.ProviderConfirmations ?? 0, confirmations);
                row.FirstPaymentObservedAt = first;
                if (decision == PaymentEvidenceDecision.Ready) { row.ConfirmedAt ??= observedAt; row.AmountReceived = row.AmountDue; }
                row.IpnKeyId = notice.KeyId; row.IpnSignature = notice.Signature; row.IpnPayloadSha256 = notice.CanonicalHash; row.IpnVerifiedAt = observedAt; row.EvidenceAssetRef = asset; row.EvidenceDigest = notice.RawHash;
                row.PendingEvidence = asset is null ? proof.Content.ToArray() : null;
                return true;
            }, ct);
            if (data.Order is Guid live && first is DateTime observed && data.Deadline is DateTime deadline && observed <= deadline && data.OrderStatus == "awaiting_payment")
            {
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequireOrderCommandAsync(context, live, token);
                    var row = await ef.Data.Set<Entities.Order>().SingleOrDefaultAsync(value => value.Id == live && value.PaymentStatus == "awaiting_payment", token);
                    if (row is not null) { row.PaymentStatus = "confirmation_pending"; row.Revision++; row.UpdatedAt = await ef.BrandClockAsync(context.BrandId, token); }
                    return true;
                }, ct);
                await PaymentInstructionStore.EnqueueAsync(session, context, live, "processing", ct);
            }
            await OutboxStore.EnqueueAsync(session, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, data.Id), eventId, now, ct);
            await session.CommitAsync(ct);
            return new(decision switch { PaymentEvidenceDecision.Ready => PaymentCaptureOutcome.Ready, PaymentEvidenceDecision.Review => PaymentCaptureOutcome.Review, _ => PaymentCaptureOutcome.Pending }, data.Id);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public Task ResumeArchiveAsync(BrandContext context, Guid payment, Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive,
        long? writerRevision = null, CancellationToken ct = default) =>
        ResumeCoreAsync(context, payment, PaymentEvidenceArchive.Plain(archive), writerRevision, ct);

    private async Task ResumeCoreAsync(BrandContext context, Guid payment, Func<Evidence, CancellationToken, Task<string>> archive,
        long? writerRevision, CancellationToken ct)
    {
        Evidence? proof;
        await using (var read = await sessions.BeginAsync(ct))
        {
            await AdmitAsync(read, context, writerRevision, ct);
            var pending = await read.RunEfStepAsync(false, (data, token) =>
            {
                var rows = from value in data.Data.Set<Entities.Payment>()
                           join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id
                           join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id
                           join brand in data.Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
                           where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId &&
                               value.PendingEvidence != null
                           select value;
                return rows.Select(value => new Pending(value.EvidenceDigest, value.PendingEvidence)).SingleOrDefaultAsync(token);
            }, ct);
            if (pending is null) return;
            if (pending.Digest is null || pending.Content is null) throw new PaymentNoticeRejectedException();
            proof = new(payment, context.BrandId, Convert.ToHexStringLower(pending.Digest), pending.Content);
        }
        try
        {
            string asset = await PaymentEvidenceArchive.SaveAsync(archive, proof, ct);
            await using var session = await sessions.BeginAsync(ct);
            await AdmitAsync(session, context, writerRevision, ct);
            byte[] digest = Convert.FromHexString(proof.Digest);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequirePaymentCommandAsync(context, payment, token);
                var row = await data.Data.Set<Entities.Payment>().SingleOrDefaultAsync(value =>
                    value.Id == payment && value.EvidenceDigest == digest && value.PendingEvidence != null, token);
                if (row is not null)
                {
                    row.EvidenceAssetRef = asset; row.PendingEvidence = null;
                }
                return true;
            }, ct);
            await session.CommitAsync(ct);
        }
        finally { CryptographicOperations.ZeroMemory(proof.Content); }
    }

    private static bool Qualifies(Payment payment, VerifiedPaymentNotice notice) =>
        notice.ReceivedTokens > 0 && notice.QuoteCurrency == "usd" && payment.Due == notice.QuoteAmount &&
        string.Equals(payment.Asset, notice.Asset, StringComparison.OrdinalIgnoreCase) &&
        (payment.Expected is null || payment.Expected == notice.ExpectedTokens);

    private static bool Matches(Payment payment, VerifiedPaymentNotice notice) =>
        payment.Method == "crypto" && payment.Currency == "USD" && payment.ProviderCode == "nowpayments" &&
        payment.Origin == notice.Origin && (!payment.HostedStarted || payment.HostedReference is not null && payment.HostedReference == notice.ProviderInvoice) &&
        (payment.Provider is null
            ? payment.HostedReference is not null && notice.QuoteCurrency == "usd" && payment.Due == notice.QuoteAmount &&
                string.Equals(payment.Asset, notice.Asset, StringComparison.OrdinalIgnoreCase)
            : payment.Provider == notice.ProviderPayment && payment.Expected is not null);
    private static async Task AdmitAsync(DbSession session, BrandContext context, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
        else await session.AdmitWriterAsync(context, ct);
    }

    private static Task<int> LockMemberAsync(DbSession session, BrandContext context, long actor, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(context.BrandId, actor, token), ct);
    private static async Task<Payment> LoadAsync(DbSession session, BrandContext context, Guid? payment, string? origin, bool locked, CancellationToken ct)
    {
        Payment? result = await session.RunEfStepAsync(false, async (data, token) =>
        {
            var query = from value in data.Data.Set<Entities.Payment>()
                        join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
                        from order in orders.DefaultIfEmpty()
                        join member in data.Data.Set<Entities.User>() on (value.TopupUserId ?? (Guid?)order.UserId) equals member.Id
                        join brand in data.Data.Set<Entities.Brand>() on member.BrandId equals brand.Id
                        where brand.Id == context.BrandId && brand.BotId == context.BotId && ((payment != null && value.Id == payment) || (origin != null && value.IdempotencyRef == origin))
                        select new { Value = value, Member = member.Id, Deadline = order == null ? (DateTime?)null : order.PaymentDueAt, OrderStatus = order == null ? null : order.PaymentStatus };
            if (locked)
            {
                Guid? id = await query.Select(value => (Guid?)value.Value.Id).SingleOrDefaultAsync(token);
                if (id is Guid found) await data.LockPaymentAsync(found, token);
            }
            return await query.Select(value => new Payment(value.Value.Id, value.Member, value.Value.OrderId, value.Value.PaymentMethod, value.Value.QuoteCurrency, value.Value.AmountDue, value.Value.NetworkAsset ?? "", value.Value.ProviderPaymentRef, value.Value.ExpectedNetworkAmount, value.Value.IdempotencyRef, value.Value.Status, value.Value.IpnPayloadSha256, value.Value.FirstPaymentObservedAt, value.Value.ConfirmedAt, value.Deadline, value.OrderStatus, value.Value.ProviderCode, value.Value.PendingEvidence != null, value.Value.HostedInvoiceStartedAt != null, value.Value.HostedInvoiceRef, value.Value.HostedInvoiceUrl)).SingleOrDefaultAsync(token);
        }, ct);
        if (result is null) Reject(session);
        return result;
    }
    private sealed record Topup(Guid Member, decimal Amount, string Asset);
    private sealed record Payment(Guid Id, Guid Member, Guid? Order, string Method, string Currency, decimal Due, string Asset, string? Provider, decimal? Expected, string Origin, string Status, byte[]? Canonical, DateTime? First, DateTime? Confirmed, DateTime? Deadline, string? OrderStatus, string? ProviderCode, bool ArchivePending, bool HostedStarted, string? HostedReference, string? HostedUrl);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Payment origin or evidence is unavailable."); }
}
