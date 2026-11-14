using System.Buffers.Binary;
using System.Security.Cryptography;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum PaymentCaptureOutcome { Pending, Ready, Review, Replayed }
public sealed record ProtectedPaymentEvidence(Guid PaymentId, Guid BrandId, string Digest, string EncryptionKeyId, ReadOnlyMemory<byte> Ciphertext)
{
    public override string ToString() => "ProtectedPaymentEvidence [redacted]";
}
public sealed record PaymentCapture(PaymentCaptureOutcome Outcome, Guid PaymentId);

public sealed class PaymentEvidenceStore(DbSessionFactory sessions, SecretSnapshot secrets)
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
            var prior = await session.QuerySingleAsync(new DbStatement("SELECT topup_user_id,amount_due::text,network_asset FROM (SELECT 1) seed LEFT JOIN shop.payments ON id=$1", DbValue.Uuid(payment)),
                r => r.IsDBNull(0) ? null : new Topup(r.GetGuid(0), ExactDecimal.Parse(r.GetString(1)), r.GetString(2)), ct);
            string asset = network == CryptoAsset.UsdtBsc ? "USDTBSC" : "USDTTRC20";
            if (prior is not null)
            {
                if (prior.Member != member.Id || prior.Amount != amount.Amount || prior.Asset != asset) Reject(session);
                return payment;
            }
            var policy = await session.QuerySingleAsync(new DbStatement("""
                SELECT b.allow_crypto,b.allow_wallet_topup,b.zero_balance_topup,COALESCE(w.balance,0)::text FROM shop.brands b
                LEFT JOIN shop.wallets w ON w.user_id=$3 WHERE b.id=$1 AND b.bot_id=$2 AND b.active FOR UPDATE OF b
                """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Uuid(member.Id)), r => (Crypto: r.GetBoolean(0), Topup: r.GetBoolean(1), Zero: r.GetBoolean(2), Balance: ExactDecimal.Parse(r.GetString(3))), ct);
            if (member.Blocked || member.Banned || !policy.Crypto || !policy.Topup || (!policy.Zero && policy.Balance <= 0)) Reject(session);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.payments(id,topup_user_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset)
                VALUES ($1,$2,'crypto','USD',$3,$4,$5)
                """, DbValue.Uuid(payment), DbValue.Uuid(member.Id), DbValue.Numeric(amount.Amount), DbValue.Text("topup:" + payment.ToString("D")), DbValue.Text(asset)), ct);
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
            Payment data = await LoadAsync(session, context, payment, null, true, ct);
            if (data.Member != member.Id || data.Method != "crypto" || data.Currency != "USD") Reject(session);
            if (data.Provider is not null)
            {
                if (data.ProviderCode != "nowpayments" || data.Provider != providerPayment || data.Expected != expectedTokens) Reject(session);
                return;
            }
            if (data.Status is not ("created" or "awaiting")) Reject(session);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET provider_code='nowpayments',provider_payment_ref=$1,expected_network_amount=$2,status='awaiting' WHERE id=$3",
                DbValue.Text(providerPayment), DbValue.Numeric(expectedTokens), DbValue.Uuid(payment)), ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    public Task<PaymentCapture> CaptureAsync(BrandContext context, VerifiedPaymentNotice notice,
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, CancellationToken ct = default) => CaptureCoreAsync(context, notice, archive, null, ct);
    public Task<PaymentCapture> CaptureForBrandAsync(BrandContext context, long expectedRevision, VerifiedPaymentNotice notice,
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return CaptureCoreAsync(context, notice, archive, expectedRevision, ct);
    }
    private async Task<PaymentCapture> CaptureCoreAsync(BrandContext context, VerifiedPaymentNotice notice,
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, long? writerRevision, CancellationToken ct)
    {
        Payment before;
        DateTime observedAt;
        await using (var preflight = await sessions.BeginAsync(ct))
        {
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(preflight, context, revision, ct);
            before = await LoadAsync(preflight, context, null, notice.Origin, false, ct);
            observedAt = await preflight.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), r => r.GetUtc(0), ct);
        }
        if (before.Method != "crypto" || before.Currency != "USD" || before.ProviderCode != "nowpayments" || before.Provider != notice.ProviderPayment || before.Expected is null) throw new PaymentNoticeRejectedException();
        byte[] encrypted = new BrandCipher(secrets).Encrypt(context, "payments", before.Id, "ipn_evidence", notice.Body.Span);
        string asset = await archive(new(before.Id, context.BrandId, Convert.ToHexStringLower(notice.RawHash), secrets.CurrentBrandKeyId(context), encrypted), ct);
        if (!asset.StartsWith("protected:", StringComparison.Ordinal) || asset.Length is < 12 or > 1024 || asset.Any(char.IsControl)) throw new PaymentNoticeRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 FOR UPDATE) u", DbValue.Uuid(before.Member), DbValue.Uuid(context.BrandId)), r => r.GetInt64(0), ct);
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
            if (before.Order is Guid order) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE) o", DbValue.Uuid(order)), r => r.GetInt64(0), ct);
            Payment data = await LoadAsync(session, context, before.Id, null, true, ct);
            if (data.Provider != notice.ProviderPayment || data.Origin != notice.Origin || data.Expected != before.Expected || data.Member != before.Member) Reject(session);
            if (data.Canonical is not null && CryptographicOperations.FixedTimeEquals(data.Canonical, notice.CanonicalHash)) return new(PaymentCaptureOutcome.Replayed, data.Id);
            string expectedAsset = data.Asset switch { "USDTTRC20" => "usdttrc20", "USDTBSC" => "usdtbsc", _ => throw new PaymentNoticeRejectedException() };
            var decision = PaymentEvidencePolicy.Evaluate(new(data.Provider!, data.Origin, data.Due, expectedAsset, data.Expected!.Value), notice);
            DateTime now = await session.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), r => r.GetUtc(0), ct);
            Guid eventId = new(notice.CanonicalHash.AsSpan(0, 16));
            if (data.Status == "settled")
            {
                await session.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id)
                    VALUES ($1,'system',$2,'update','payment',$3,'post_settlement_notice',$4,$3) ON CONFLICT(id) DO NOTHING
                    """, DbValue.Uuid(eventId), DbValue.Uuid(context.BrandId), DbValue.Uuid(data.Id), DbValue.Binary(notice.RawHash)), ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, data.Id), eventId, now, ct);
                await session.CommitAsync(ct);
                return new(PaymentCaptureOutcome.Replayed, data.Id);
            }
            if (data.Status == "review") decision = PaymentEvidenceDecision.Review;
            bool identity = notice.QuoteCurrency == "usd" && notice.QuoteAmount == data.Due && notice.Asset == expectedAsset && notice.ExpectedTokens == data.Expected;
            DateTime? first = data.First ?? (identity && notice.ReceivedTokens > 0 ? observedAt : null);
            if (data.Confirmed is not null && decision == PaymentEvidenceDecision.Pending) return new(PaymentCaptureOutcome.Replayed, data.Id);
            await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.payments SET status=$1,provider_status_code=$2,network_amount=CASE WHEN $15 THEN GREATEST(COALESCE(network_amount,0),$3) ELSE network_amount END,
                    provider_confirmations=CASE WHEN $4 IS NULL THEN provider_confirmations ELSE GREATEST(COALESCE(provider_confirmations,0),$4) END,first_payment_observed_at=$5,
                    confirmed_at=COALESCE(confirmed_at,$6),amount_received=CASE WHEN $7 THEN amount_due ELSE amount_received END,
                    ipn_key_id=$8,ipn_signature=$9,ipn_payload_sha256=$10,ipn_verified_at=$11,evidence_asset_ref=$12,evidence_digest=$13
                WHERE id=$14
                """, DbValue.Text(decision == PaymentEvidenceDecision.Review ? "review" : first is null ? "awaiting" : "confirming"), DbValue.Text(notice.Status), DbValue.Numeric(notice.ReceivedTokens),
                notice.Confirmations is long n ? DbValue.Bigint(n) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), Time(first), decision == PaymentEvidenceDecision.Ready ? DbValue.Utc(observedAt) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.TimestampTz),
                DbValue.Boolean(decision == PaymentEvidenceDecision.Ready), DbValue.Text(notice.KeyId), DbValue.Binary(notice.Signature), DbValue.Binary(notice.CanonicalHash), DbValue.Utc(observedAt), DbValue.Text(asset), DbValue.Binary(notice.RawHash), DbValue.Uuid(data.Id), DbValue.Boolean(identity)), ct);
            if (data.Order is Guid live && first is DateTime observed && data.Deadline is DateTime deadline && observed <= deadline && data.OrderStatus == "awaiting_payment")
                await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='confirmation_pending',revision=revision+1,updated_at=clock_timestamp() WHERE id=$1 AND payment_status='awaiting_payment'", DbValue.Uuid(live)), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, data.Id), eventId, now, ct);
            await session.CommitAsync(ct);
            return new(decision switch { PaymentEvidenceDecision.Ready => PaymentCaptureOutcome.Ready, PaymentEvidenceDecision.Review => PaymentCaptureOutcome.Review, _ => PaymentCaptureOutcome.Pending }, data.Id);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    private static Task LockMemberAsync(DbSession session, BrandContext context, long actor, CancellationToken ct) =>
        session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), r => r.GetInt64(0), ct);
    private static async Task<Payment> LoadAsync(DbSession session, BrandContext context, Guid? payment, string? origin, bool locked, CancellationToken ct)
    {
        Payment? data = await session.QuerySingleAsync<Payment?>(new DbStatement("""
            SELECT p.id,p.member,p.order_id,p.payment_method,p.quote_currency,p.amount_due::text,p.network_asset,p.provider_payment_ref,p.expected_network_amount::text,
                p.idempotency_ref,p.status,p.ipn_payload_sha256,p.first_payment_observed_at,p.confirmed_at,p.payment_due_at,p.order_status,p.provider_code FROM (SELECT 1) seed
            LEFT JOIN LATERAL (
                SELECT p.*,u.id AS member,o.payment_due_at,o.payment_status AS order_status FROM shop.payments p LEFT JOIN shop.orders o ON o.id=p.order_id
                JOIN shop.users u ON u.id=COALESCE(p.topup_user_id,o.user_id) JOIN shop.brands b ON b.id=u.brand_id
                WHERE b.id=$1 AND b.bot_id=$2 AND (($3 IS NOT NULL AND p.id=$3) OR ($4 IS NOT NULL AND p.idempotency_ref=$4))
            """ + (locked ? " FOR UPDATE OF p" : "") + ") p ON true", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId),
            payment is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid), origin is string text ? DbValue.Text(text) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text)), r => r.IsDBNull(0) ? null :
                new Payment(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), ExactDecimal.Parse(r.GetString(5)), r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : ExactDecimal.Parse(r.GetString(8)), r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetBinary(11), r.IsDBNull(12) ? null : r.GetUtc(12), r.IsDBNull(13) ? null : r.GetUtc(13), r.IsDBNull(14) ? null : r.GetUtc(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16)), ct);
        if (data is null) Reject(session);
        return data;
    }
    private static DbValue Time(DateTime? value) => value is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.TimestampTz);
    private sealed record Topup(Guid Member, decimal Amount, string Asset);
    private sealed record Payment(Guid Id, Guid Member, Guid? Order, string Method, string Currency, decimal Due, string Asset, string? Provider, decimal? Expected, string Origin, string Status, byte[]? Canonical, DateTime? First, DateTime? Confirmed, DateTime? Deadline, string? OrderStatus, string? ProviderCode);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Payment origin or evidence is unavailable."); }
}
