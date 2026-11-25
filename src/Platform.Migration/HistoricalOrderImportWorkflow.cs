using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyHistoricalOrder(LegacySourceOrder Source, Guid Target, Guid User, string HistoricalBrandName)
{
    public (string Title, decimal UnitPrice) Terms()
    {
        try
        {
            using var document = JsonDocument.Parse(Source.CheckoutSnapshot);
            var root = document.RootElement;
            if (Target == Guid.Empty || User == Guid.Empty || string.IsNullOrWhiteSpace(HistoricalBrandName) || HistoricalBrandName.Length > 160 || Source.CheckoutSnapshot.Length > 16384 ||
                string.IsNullOrEmpty(Source.Id) || Source.Id.Length != 16 || !Source.Id.StartsWith("ORD-", StringComparison.Ordinal) || Source.Id.Skip(4).Any(c => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) ||
                Source.Currency != "USD" || Source.Total <= 0 || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("product_id").GetInt64() != Source.ProductId ||
                root.GetProperty("quantity").GetInt32() != Source.Quantity || root.GetProperty("currency").GetString() != "USD" || root.GetProperty("payment_method").GetString() != "wallet" ||
                root.GetProperty("product_type").GetString() != "credential" || root.GetProperty("discount_code").ValueKind != JsonValueKind.Null || root.GetProperty("discount_pct").ValueKind != JsonValueKind.Null || root.GetProperty("discount_quantity").GetInt32() != 0 ||
                ExactDecimal.Parse(root.GetProperty("original_usd").GetString()!) != Source.Total || ExactDecimal.Parse(root.GetProperty("total_usd").GetString()!) != Source.Total ||
                ExactDecimal.Parse(root.GetProperty("savings_usd").GetString()!) != 0 || ExactDecimal.Parse(root.GetProperty("savings_toman").GetString()!) != 0 ||
                ExactDecimal.Parse(root.GetProperty("original_toman").GetString()!) != ExactDecimal.Parse(root.GetProperty("total_toman").GetString()!)) throw new MigrationRejectedException();
            string title = root.GetProperty("product_name").GetString()!;
            decimal unit = ExactDecimal.Divide(Source.Total, Source.Quantity);
            if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || ExactDecimal.Multiply(unit, Source.Quantity) != Source.Total) throw new MigrationRejectedException();
            return (title, unit);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArithmeticException or ArgumentNullException) { throw new MigrationRejectedException(); }
    }
    public IReadOnlyList<LegacyMapProof> Proofs(Guid brand)
    {
        _ = Terms();
        var source = Source.Proof(Target, brand);
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("historical-order-target-v1"); writer.Write(source.Fingerprint.Span); writer.Write(Target.ToByteArray()); writer.Write(User.ToByteArray()); writer.Write(HistoricalBrandName); writer.Flush();
        return [source, new(LegacyProjection.HistoricalOrder, Source.Id, Target, brand, SHA256.HashData(bytes.ToArray())), new(LegacyProjection.OrderAlias, Source.Id, Target, brand, source.Fingerprint.Span)];
    }
    public override string ToString() => "LegacyHistoricalOrder [redacted]";
}
public sealed class HistoricalOrderImportWorkflow(DbSessionFactory sessions)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, LegacyHistoricalOrder row, CancellationToken ct = default)
    {
        var terms = row.Terms(); var proofs = row.Proofs(context.BrandId);
        if (manifest.TargetBrand != context.BrandId || (manifest.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba" && !StagingWalletSnapshot.Matches(manifest)) ||
            !manifest.Proofs.Any(p => p.Projection == LegacyProjection.User && p.Target == row.User && p.LegacyId == row.Source.UserId.ToString(CultureInfo.InvariantCulture))) throw new MigrationRejectedException();
        foreach (var proof in proofs) manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct);
        await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        bool member = await session.RunEfStepAsync(false, (data, token) => (from u in data.Data.Set<User>() join b in data.Data.Set<Brand>() on u.BrandId equals b.Id where u.Id == row.User && b.Id == context.BrandId && b.BotId == context.BotId && u.TelegramId == row.Source.UserId select u.Id).AnyAsync(token), ct);
        if (!member || !await LegacyValues.HasHistoricalDebitAsync(session, manifest, row.Source.Id, row.User, row.Source.Total, ct)) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        bool projection = await LegacyMapStore.EnsureAsync(session, manifest, proofs[1], ct);
        bool alias = await LegacyMapStore.EnsureAsync(session, manifest, proofs[2], ct);
        if (first != projection || first != alias) throw new MigrationRejectedException();
        if (first)
        {
            await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new Order { Id = row.Target, UserId = row.User, Quantity = row.Source.Quantity, OrderKind = "product_purchase", PaymentStatus = "paid", FulfillmentStatus = "requires_admin", Currency = "USDT", PaymentMethod = "wallet", UnitPriceUsdt = terms.UnitPrice, AutoPriceTomanSnapshot = false, PricingRoundingStepSnapshot = 5000m, PriceCalculationVersion = "legacy-wallet-v1", PaymentConfirmedAt = row.Source.CreatedAt, BrandPublicNameSnapshot = row.HistoricalBrandName, ProductTitleSnapshot = terms.Title, PaymentTermsSnapshot = row.Source.CheckoutSnapshot, FulfillmentKindSnapshot = "local", LocalFirstSnapshot = false, IsPreorder = false, ReviewCode = "credential_recovery", CreatedAt = row.Source.CreatedAt, UpdatedAt = row.Source.CreatedAt }); data.Data.Add(new AuditEvent { Id = Guid.NewGuid(), ActorKind = "system", BrandId = context.BrandId, Action = "legacy_import", EntityKind = "order", EntityId = row.Target, Reason = "Manifest-bound historical wallet order retained for review", CorrelationId = manifest.Batch }); return Task.FromResult(true); }, ct);
        }
        if (!await MatchesAsync(session, row, ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, LegacyHistoricalOrder row, CancellationToken ct)
    {
        var terms = row.Terms();
        return session.RunEfStepAsync(false, (data, token) => data.Data.Set<Order>().AnyAsync(o => o.Id == row.Target && o.UserId == row.User && o.Quantity == row.Source.Quantity && o.ProductId == null && o.OrderKind == "product_purchase" && o.PaymentStatus == "paid" && o.FulfillmentStatus == "requires_admin" && o.Currency == "USDT" && o.PaymentMethod == "wallet" && o.UnitPriceUsdt == terms.UnitPrice && o.PaymentConfirmedAt == row.Source.CreatedAt && o.CreatedAt == row.Source.CreatedAt && o.UpdatedAt == row.Source.CreatedAt && o.BrandPublicNameSnapshot == row.HistoricalBrandName && o.ProductTitleSnapshot == terms.Title && o.PaymentTermsSnapshot == row.Source.CheckoutSnapshot && o.FulfillmentKindSnapshot == "local" && !o.LocalFirstSnapshot && !o.IsPreorder && o.ReviewCode == "credential_recovery" && !o.AutoPriceTomanSnapshot && o.PricingRoundingStepSnapshot == 5000m && o.PriceCalculationVersion == "legacy-wallet-v1" && o.UnitPriceToman == null && o.FxRateSnapshot == null && o.DiscountPctSnapshot == null && o.DiscountUnitsSnapshot == null && o.DeliveryGeneration == null && o.ReleaseAuthorizedAt == null && o.ResolutionKind == null && !data.Data.Set<Fulfillment>().Any(f => f.OrderId == o.Id) && !data.Data.Set<WalletEntry>().Any(w => w.OrderId == o.Id) && !data.Data.Set<Payment>().Any(p => p.OrderId == o.Id) && !data.Data.Set<SupplierRequest>().Any(r => r.OrderId == o.Id), token), ct);
    }
}
