using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePaymentCaptureStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<PaymentCapture> CaptureAsync(BrandContext context, long revision, VerifiedPaymentNotice notice,
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, CancellationToken ct = default)
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
        byte[] encrypted = new BrandCipher(secrets).Encrypt(context, "payments", before.Id, "ipn_evidence", notice.Body.Span);
        var proof = new ProtectedPaymentEvidence(before.Id, context.BrandId,
            Convert.ToHexStringLower(notice.RawHash), secrets.CurrentBrandKeyId(context), encrypted);
        DateTime observed = DateTime.UtcNow;
        if (before.Order is not null && before.OrderStatus == "awaiting_payment" &&
            before.Deadline is DateTime due && due >= observed && Qualifies(before, notice))
        {
            PaymentCapture? staged = await PersistAsync(context, revision, notice, before, proof, null, observed, ct);
            if (staged is not null)
            {
                await ResumeArchiveAsync(context, revision, before.Id, archive, ct);
                return staged;
            }
        }
        string asset = await ArchiveAsync(archive, proof, ct);
        return await PersistAsync(context, revision, notice, before, proof, asset, observed, ct)
            ?? throw new PaymentNoticeRejectedException();
    }

    public async Task ResumeArchiveAsync(BrandContext context, long revision, Guid payment,
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ProtectedPaymentEvidence? proof = await sessions.RunAsync(false, async (_, tx, token) =>
        {
            State state = await ReadAsync(tx, context, revision, payment, null, token);
            if (!state.Pending) return null;
            using var command = SqliteWallet.Command(tx, """
                SELECT evidence_digest,pending_evidence_key_id,pending_evidence_ciphertext
                FROM payments WHERE id=$payment AND pending_evidence_ciphertext IS NOT NULL
                """, ("$payment", payment));
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.IsDBNull(0) || row.IsDBNull(1) || row.IsDBNull(2))
                throw new PaymentNoticeRejectedException();
            return new ProtectedPaymentEvidence(payment, context.BrandId, Convert.ToHexStringLower(row.GetFieldValue<byte[]>(0)),
                row.GetString(1), row.GetFieldValue<byte[]>(2));
        }, ct);
        if (proof is null) return;
        string asset = await ArchiveAsync(archive, proof, ct);
        await sessions.RunAsync(true, async (_, tx, token) =>
        {
            await ReadAsync(tx, context, revision, payment, null, token);
            using var command = SqliteWallet.Command(tx, """
                UPDATE payments SET evidence_asset_ref=$asset,pending_evidence_ciphertext=NULL,pending_evidence_key_id=NULL
                WHERE id=$payment AND evidence_digest=$digest AND pending_evidence_ciphertext IS NOT NULL
                """, ("$asset", asset), ("$payment", payment), ("$digest", Convert.FromHexString(proof.Digest)));
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new PaymentNoticeRejectedException();
            return 0;
        }, ct);
    }

    private async Task<PaymentCapture?> PersistAsync(BrandContext context, long revision, VerifiedPaymentNotice notice,
        State before, ProtectedPaymentEvidence proof, string? asset, DateTime observed, CancellationToken ct) =>
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
                using var audit = SqliteWallet.Command(tx, """
                    INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,command_sha256,correlation_id)
                    VALUES ($event,'system',$brand,'update','payment',$payment,'post_settlement_notice',$hash,$payment)
                    ON CONFLICT(id) DO NOTHING
                    """, ("$event", replayEvent), ("$brand", context.BrandId), ("$payment", state.Id),
                    ("$hash", notice.RawHash));
                await audit.ExecuteNonQueryAsync(token);
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Payment(JobKind.VerifyPayment, context.BrandId, state.Id),
                    replayEvent, observed, token);
                return new PaymentCapture(PaymentCaptureOutcome.Replayed, state.Id);
            }
            if (state.Provider is null)
            {
                using var bind = SqliteWallet.Command(tx, """
                    UPDATE payments SET provider_payment_ref=$provider,expected_network_amount=$tokens,status='awaiting'
                    WHERE id=$payment AND provider_payment_ref IS NULL AND status IN ('created','awaiting')
                    """, ("$provider", notice.ProviderPayment), ("$tokens", ExactDecimal.Format(notice.ExpectedTokens)),
                    ("$payment", state.Id));
                if (await bind.ExecuteNonQueryAsync(token) != 1) throw new PaymentNoticeRejectedException();
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
            string now = SqliteSchema.UtcText(observed);
            string? firstText = first is DateTime point ? SqliteSchema.UtcText(point) : null;
            using var update = SqliteWallet.Command(tx, """
                UPDATE payments SET status=$status,provider_status_code=$providerStatus,
                    network_amount=CASE WHEN $identity=1 AND
                        (network_amount IS NULL OR akanti_decimal_compare(network_amount,$received)<0)
                        THEN $received ELSE network_amount END,
                    provider_confirmations=CASE WHEN $confirmations IS NULL THEN provider_confirmations
                        ELSE max(coalesce(provider_confirmations,0),$confirmations) END,
                    first_payment_observed_at=$first,
                    confirmed_at=coalesce(confirmed_at,$confirmed),
                    amount_received=CASE WHEN $ready=1 THEN amount_due ELSE amount_received END,
                    ipn_key_id=$key,ipn_signature=$signature,ipn_payload_sha256=$canonical,
                    ipn_verified_at=$now,evidence_asset_ref=$asset,evidence_digest=$digest,
                    pending_evidence_ciphertext=$pending,pending_evidence_key_id=$pendingKey
                WHERE id=$payment AND status<>'settled'
                """, ("$status", decision == PaymentEvidenceDecision.Review ? "review" :
                        first is null ? "awaiting" : "confirming"),
                ("$providerStatus", notice.Status), ("$identity", identity),
                ("$received", ExactDecimal.Format(notice.ReceivedTokens)), ("$confirmations", notice.Confirmations),
                ("$first", firstText), ("$confirmed", decision == PaymentEvidenceDecision.Ready ? now : null),
                ("$ready", decision == PaymentEvidenceDecision.Ready), ("$key", notice.KeyId),
                ("$signature", notice.Signature), ("$canonical", notice.CanonicalHash), ("$now", now),
                ("$asset", asset), ("$digest", notice.RawHash),
                ("$pending", asset is null ? proof.Ciphertext.ToArray() : null),
                ("$pendingKey", asset is null ? proof.EncryptionKeyId : null), ("$payment", state.Id));
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new PaymentNoticeRejectedException();
            if (state.Order is Guid order && first is DateTime observedFirst &&
                state.Deadline is DateTime deadline && observedFirst <= deadline &&
                state.OrderStatus == "awaiting_payment")
            {
                using var orderUpdate = SqliteWallet.Command(tx, """
                    UPDATE orders SET payment_status='confirmation_pending',revision=revision+1,updated_at=$now
                    WHERE id=$order AND payment_status='awaiting_payment'
                    """, ("$now", now), ("$order", order));
                if (await orderUpdate.ExecuteNonQueryAsync(token) != 1) throw new PaymentNoticeRejectedException();
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
        Guid delivery = Guid.CreateVersion7();
        using var insert = SqliteWallet.Command(tx, """
            INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id)
            SELECT $id,$brand,'customer_dm',$member,'order_status',
                CASE WHEN a.message IS NULL THEN 'send' ELSE 'edit' END,a.message,$dedupe,$order
            FROM (SELECT 1) seed LEFT JOIN (
                SELECT telegram_message_id message FROM telegram_deliveries
                WHERE order_id=$order AND purpose='order_status' AND status='succeeded' AND telegram_message_id IS NOT NULL
                ORDER BY created_at,id LIMIT 1
            ) a ON true WHERE true
            ON CONFLICT(dedupe_key) DO NOTHING
            """, ("$id", delivery), ("$brand", context.BrandId), ("$member", member),
            ("$order", order), ("$dedupe", "instruction-processing:" + order.ToString("D")));
        await insert.ExecuteNonQueryAsync(ct);
        using var lookup = SqliteWallet.Command(tx, """
            SELECT id FROM telegram_deliveries WHERE dedupe_key=$dedupe AND brand_id=$brand AND order_id=$order
            """, ("$dedupe", "instruction-processing:" + order.ToString("D")), ("$brand", context.BrandId),
            ("$order", order));
        object? key = await lookup.ExecuteScalarAsync(ct);
        if (key is not string id) throw new PaymentNoticeRejectedException();
        Guid actual = Guid.Parse(id);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, actual), actual, DateTime.UtcNow, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, long revision,
        Guid? payment, string? origin, CancellationToken ct)
    {
        using var command = SqliteWallet.Command(tx, """
            SELECT p.id,coalesce(p.topup_user_id,o.user_id),p.order_id,p.payment_method,p.quote_currency,
                p.amount_due,p.network_asset,p.provider_payment_ref,p.expected_network_amount,p.idempotency_ref,
                p.status,p.ipn_payload_sha256,p.first_payment_observed_at,p.confirmed_at,o.payment_due_at,
                o.payment_status,p.provider_code,p.pending_evidence_ciphertext IS NOT NULL,
                p.hosted_invoice_started_at IS NOT NULL,p.hosted_invoice_ref
            FROM payments p LEFT JOIN orders o ON o.id=p.order_id
                JOIN users u ON u.id=coalesce(p.topup_user_id,o.user_id)
                JOIN brands b ON b.id=u.brand_id
            WHERE b.id=$brand AND b.bot_id=$bot AND b.active=1 AND b.policy_version=$revision
                AND (($payment IS NOT NULL AND p.id=$payment) OR ($origin IS NOT NULL AND p.idempotency_ref=$origin))
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$revision", revision),
            ("$payment", payment), ("$origin", origin));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("The payment is unavailable.");
        return new(row.GetGuid(0), row.GetGuid(1), row.IsDBNull(2) ? null : row.GetGuid(2),
            row.GetString(3), row.GetString(4), ExactDecimal.Parse(row.GetString(5)),
            row.IsDBNull(6) ? "" : row.GetString(6), row.IsDBNull(7) ? null : row.GetString(7),
            row.IsDBNull(8) ? null : ExactDecimal.Parse(row.GetString(8)), row.GetString(9), row.GetString(10),
            row.IsDBNull(11) ? null : row.GetFieldValue<byte[]>(11),
            row.IsDBNull(12) ? null : Parse(row.GetString(12)),
            row.IsDBNull(13) ? null : Parse(row.GetString(13)),
            row.IsDBNull(14) ? null : Parse(row.GetString(14)),
            row.IsDBNull(15) ? null : row.GetString(15), row.IsDBNull(16) ? null : row.GetString(16),
            row.GetBoolean(17), row.GetBoolean(18), row.IsDBNull(19) ? null : row.GetString(19));
    }

    private static DateTime Parse(string value) =>
        DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

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

    private static async Task<string> ArchiveAsync(
        Func<ProtectedPaymentEvidence, CancellationToken, Task<string>> archive, ProtectedPaymentEvidence proof,
        CancellationToken ct)
    {
        string asset = await archive(proof, ct);
        if (!asset.StartsWith("protected:", StringComparison.Ordinal) || asset.Length is < 12 or > 1024 ||
            asset.Any(char.IsControl)) throw new PaymentNoticeRejectedException();
        return asset;
    }

    private sealed record State(Guid Id, Guid Member, Guid? Order, string Method, string Currency, decimal Due, string Asset,
        string? Provider, decimal? Expected, string Origin, string Status, byte[]? Canonical, DateTime? First,
        DateTime? Confirmed, DateTime? Deadline, string? OrderStatus, string? ProviderCode, bool Pending,
        bool HostedStarted, string? HostedReference);
}
