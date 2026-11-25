using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Text.Json;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyPaymentRecord(ArchivedLegacyRecord Record, Guid Member, Guid? Order)
{
    internal LegacyPaymentTerms Terms(VerifiedImportManifest manifest) => Terms(manifest.Proofs);
    internal LegacyPaymentTerms Terms(IReadOnlyList<LegacyMapProof> proofs)
    {
        if (Member == Guid.Empty || Order == Guid.Empty || Record.Source.Projection is not (LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit)) throw new MigrationRejectedException();
        try
        {
            using var document = Record.Source.Open(); var row = document.RootElement;
            bool bank = Record.Source.Projection == LegacyProjection.ManualDeposit;
            string? sourceOrder = LegacyValues.Optional(row, "fulfilled_order_id");
            if ((Order is null) != (sourceOrder is null) || (sourceOrder is not null && !proofs.Any(p => p.Projection is LegacyProjection.Order or LegacyProjection.OrderRecord && p.LegacyId == sourceOrder && p.Target == Order)) ||
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
        if (row.Order is Guid order && !await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Order>().AnyAsync(o => o.Id == order && o.UserId == row.Member && o.PaymentMethod == (terms.Bank ? "bank" : "crypto") && o.Currency == (terms.Bank ? "TOMAN" : "USDT") && o.PriceCalculationVersion == "legacy-net-v1" && o.FulfillmentStatus == "requires_admin" && o.ReleaseAuthorizedAt == null && (terms.Bank ? o.UnitPriceToman : o.UnitPriceUsdt) * o.Quantity == terms.Due, token), ct)) throw new MigrationRejectedException();
        if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
        {
            await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new Payment { Id = row.Record.Target, OrderId = row.Order, TopupUserId = row.Order == null ? row.Member : null, PaymentMethod = terms.Bank ? "bank" : "crypto", QuoteCurrency = terms.Bank ? "TOMAN" : "USD", AmountDue = terms.Due, ProviderCode = terms.Provider == null ? null : "nowpayments", ProviderPaymentRef = terms.Provider, IdempotencyRef = "legacy-payment:" + row.Record.Target.ToString("D"), NetworkAsset = terms.Asset, Status = terms.Status, ProviderStatusCode = terms.ProviderStatus, ConfirmedAt = terms.Confirmed, EvidenceAssetRef = row.Record.Reference, EvidenceDigest = row.Record.Digest, LegacyKind = terms.Bank ? "manual_deposit" : "payment_attempt", CreatedAt = terms.Created }); return Task.FromResult(true); }, ct);
            await LegacyValues.AuditAsync(session, manifest, row.Record, "payment", ct);
        }
        if (!await MatchesAsync(session, manifest, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row.Record, "payment", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, VerifiedImportManifest manifest, LegacyPaymentRecord row, CancellationToken ct)
    {
        var t = row.Terms(manifest);
        Guid? topup = row.Order == null ? row.Member : null; string idempotency = "legacy-payment:" + row.Record.Target.ToString("D");
        return session.RunEfStepAsync(false, (data, token) => data.Data.Set<Payment>().AnyAsync(p => p.Id == row.Record.Target && p.OrderId == row.Order && p.TopupUserId == topup && p.PaymentMethod == (t.Bank ? "bank" : "crypto") && p.QuoteCurrency == (t.Bank ? "TOMAN" : "USD") && p.AmountDue == t.Due && p.ProviderCode == (t.Provider == null ? null : "nowpayments") && p.ProviderPaymentRef == t.Provider && p.IdempotencyRef == idempotency && p.NetworkAsset == t.Asset && p.Status == t.Status && p.ProviderStatusCode == t.ProviderStatus && p.ConfirmedAt == t.Confirmed && p.EvidenceAssetRef == row.Record.Reference && p.EvidenceDigest == row.Record.Digest && p.LegacyKind == (t.Bank ? "manual_deposit" : "payment_attempt") && p.CreatedAt == t.Created && p.AmountReceived == null && p.BankDestinationCiphertext == null && p.ReviewAdminId == null && p.ReceiptAssetRef == null && p.CompensatingWalletEntryId == null && p.ProviderConfirmations == null && p.ExpectedNetworkAmount == null && p.NetworkAmount == null && p.FirstPaymentObservedAt == null && p.IpnKeyId == null, token), ct);
    }
}
