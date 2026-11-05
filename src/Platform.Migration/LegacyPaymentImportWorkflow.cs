using System.Text.Json;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyPaymentRecord(ArchivedLegacyRecord Record, Guid Member, Guid? Order)
{
    internal LegacyPaymentTerms Terms(VerifiedImportManifest manifest)
    {
        if (Member == Guid.Empty || Order == Guid.Empty || Record.Source.Projection is not (LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit)) throw new MigrationRejectedException();
        try
        {
            using var document = Record.Source.Open(); var row = document.RootElement;
            bool bank = Record.Source.Projection == LegacyProjection.ManualDeposit;
            string? sourceOrder = LegacyValues.Optional(row, "fulfilled_order_id");
            if ((Order is null) != (sourceOrder is null) || (sourceOrder is not null && !manifest.Proofs.Any(p => p.Projection is LegacyProjection.Order or LegacyProjection.OrderRecord && p.LegacyId == sourceOrder && p.Target == Order)) ||
                (!bank && (LegacyValues.Text(row, "expected_currency") != "USD" || (Order is null ? LegacyValues.Text(row, "kind") != "wallet_topup" : LegacyValues.Text(row, "kind") != "direct_checkout"))) || (bank && Order is null)) throw new MigrationRejectedException();
            decimal due = LegacyValues.Number(row, bank ? "amount" : "expected_amount"); string status = LegacyValues.Text(row, "status");
            DateTime? confirmed = null; string targetStatus;
            if (bank)
            {
                targetStatus = status switch { "pending" => "review", "approved" => "settled", "denied" => "rejected", _ => throw new MigrationRejectedException() };
                if (status == "approved") confirmed = LegacyValues.Time(row, "reviewed_at");
            }
            else
            {
                targetStatus = status switch { "created" => "created", "invoice_created" => "awaiting", "processing" => "confirming", "fulfilled" or "credited" => "settled", "requires_review" => "review", "failed" => "rejected", _ => throw new MigrationRejectedException() };
                if (targetStatus == "settled") confirmed = LegacyValues.Time(row, "paid_at");
            }
            string? asset = bank ? null : LegacyValues.Optional(row, "pay_currency")?.ToLowerInvariant() switch { "usdttrc20" => "USDTTRC20", "usdtbsc" => "USDTBSC", _ => throw new MigrationRejectedException() };
            DateTime created = LegacyValues.Time(row, "created_at");
            string? provider = bank ? null : LegacyValues.Optional(row, "provider_payment_id"), providerStatus = bank ? null : LegacyValues.Optional(row, "provider_status");
            if ((targetStatus == "settled" && !bank && string.IsNullOrWhiteSpace(provider)) || due <= 0 || confirmed < created || provider?.Length > 160 || providerStatus?.Length > 160 || provider?.Any(char.IsControl) == true || providerStatus?.Any(char.IsControl) == true) throw new MigrationRejectedException();
            return new(row.GetProperty("user_id").GetInt64(), due, bank, asset, provider, providerStatus, targetStatus, created, confirmed);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { throw new MigrationRejectedException(); }
    }
    public override string ToString() => "LegacyPaymentRecord [redacted]";
}
internal sealed record LegacyPaymentTerms(long Telegram, decimal Due, bool Bank, string? Asset, string? Provider, string? ProviderStatus, string Status, DateTime Created, DateTime? Confirmed);
public sealed class LegacyPaymentImportWorkflow(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyPaymentRecord row, CancellationToken ct = default)
    {
        var terms = row.Terms(manifest); var proof = row.Record.Source.Proof(row.Record.Target, context, secrets);
        if (manifest.TargetBrand != context.BrandId || !new LegacyRecordArchive(secrets).Matches(context, row.Record, row.Record.Evidence)) throw new MigrationRejectedException(); manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        await LegacyValues.RequireMemberAsync(session, context, manifest, row.Member, terms.Telegram, ct);
        if (row.Order is Guid order && !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.orders WHERE id=$1 AND user_id=$2 AND payment_method=$3 AND currency=$4 AND price_calculation_version='legacy-net-v1' AND fulfillment_status='requires_admin' AND release_authorized_at IS NULL AND (CASE WHEN $6 THEN unit_price_toman ELSE unit_price_usdt END)*quantity=$5)", DbValue.Uuid(order), DbValue.Uuid(row.Member), DbValue.Text(terms.Bank ? "bank" : "crypto"), DbValue.Text(terms.Bank ? "TOMAN" : "USDT"), DbValue.Numeric(terms.Due), DbValue.Boolean(terms.Bank)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
        {
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.payments(id,order_id,topup_user_id,payment_method,quote_currency,amount_due,provider_code,provider_payment_ref,idempotency_ref,network_asset,
                    status,provider_status_code,confirmed_at,evidence_asset_ref,evidence_digest,legacy_kind,created_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
                """, DbValue.Uuid(row.Record.Target), LegacyValues.Uuid(row.Order), LegacyValues.Uuid(row.Order is null ? row.Member : null), DbValue.Text(terms.Bank ? "bank" : "crypto"), DbValue.Text(terms.Bank ? "TOMAN" : "USD"), DbValue.Numeric(terms.Due),
                LegacyValues.String(terms.Provider is null ? null : "nowpayments"), LegacyValues.String(terms.Provider), DbValue.Text("legacy-payment:" + row.Record.Target.ToString("D")), LegacyValues.String(terms.Asset), DbValue.Text(terms.Status), LegacyValues.String(terms.ProviderStatus),
                LegacyValues.Date(terms.Confirmed), DbValue.Text(row.Record.Reference), DbValue.Binary(row.Record.Digest), DbValue.Text(terms.Bank ? "manual_deposit" : "payment_attempt"), DbValue.Utc(terms.Created)), ct);
            await LegacyValues.AuditAsync(session, manifest, row.Record, "payment", ct);
        }
        if (!await MatchesAsync(session, manifest, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row.Record, "payment", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, VerifiedImportManifest manifest, LegacyPaymentRecord row, CancellationToken ct)
    {
        var t = row.Terms(manifest);
        return session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.payments WHERE id=$1 AND order_id IS NOT DISTINCT FROM $2 AND topup_user_id IS NOT DISTINCT FROM $3 AND payment_method=$4 AND quote_currency=$5
                AND amount_due=$6 AND provider_code IS NOT DISTINCT FROM $7 AND provider_payment_ref IS NOT DISTINCT FROM $8 AND idempotency_ref=$9 AND network_asset IS NOT DISTINCT FROM $10
                AND status=$11 AND provider_status_code IS NOT DISTINCT FROM $12 AND confirmed_at IS NOT DISTINCT FROM $13 AND evidence_asset_ref=$14 AND evidence_digest=$15 AND legacy_kind=$16 AND created_at=$17
                AND amount_received IS NULL AND bank_destination_ciphertext IS NULL AND review_admin_id IS NULL AND receipt_asset_ref IS NULL AND compensating_wallet_entry_id IS NULL
                AND provider_confirmations IS NULL AND expected_network_amount IS NULL AND network_amount IS NULL AND first_payment_observed_at IS NULL AND ipn_key_id IS NULL)
            """, DbValue.Uuid(row.Record.Target), LegacyValues.Uuid(row.Order), LegacyValues.Uuid(row.Order is null ? row.Member : null), DbValue.Text(t.Bank ? "bank" : "crypto"), DbValue.Text(t.Bank ? "TOMAN" : "USD"), DbValue.Numeric(t.Due),
            LegacyValues.String(t.Provider is null ? null : "nowpayments"), LegacyValues.String(t.Provider), DbValue.Text("legacy-payment:" + row.Record.Target.ToString("D")), LegacyValues.String(t.Asset), DbValue.Text(t.Status), LegacyValues.String(t.ProviderStatus), LegacyValues.Date(t.Confirmed),
            DbValue.Text(row.Record.Reference), DbValue.Binary(row.Record.Digest), DbValue.Text(t.Bank ? "manual_deposit" : "payment_attempt"), DbValue.Utc(t.Created)), r => r.GetBoolean(0), ct);
    }
}
