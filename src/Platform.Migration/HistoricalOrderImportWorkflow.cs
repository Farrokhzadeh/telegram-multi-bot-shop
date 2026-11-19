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
        bool member = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id WHERE u.id=$1 AND b.id=$2 AND b.bot_id=$3 AND u.telegram_id=$4)", DbValue.Uuid(row.User), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(row.Source.UserId)), r => r.GetBoolean(0), ct);
        if (!member || !await LegacyValues.HasHistoricalDebitAsync(session, manifest, row.Source.Id, row.User, row.Source.Total, ct)) throw new MigrationRejectedException();
        bool first = await LegacyMapStore.EnsureAsync(session, manifest, proofs[0], ct);
        bool projection = await LegacyMapStore.EnsureAsync(session, manifest, proofs[1], ct);
        bool alias = await LegacyMapStore.EnsureAsync(session, manifest, proofs[2], ct);
        if (first != projection || first != alias) throw new MigrationRejectedException();
        if (first)
        {
            await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,quantity,order_kind,payment_status,fulfillment_status,currency,payment_method,unit_price_usdt,
                auto_price_toman_snapshot,pricing_rounding_step_snapshot,price_calculation_version,payment_confirmed_at,brand_public_name_snapshot,
                product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,review_code,created_at,updated_at)
            VALUES($1,$2,$3,'product_purchase','paid','requires_admin','USDT','wallet',$4,false,5000,'legacy-wallet-v1',$5,$6,$7,$8,'local',false,false,'credential_recovery',$5,$5)
            """, DbValue.Uuid(row.Target), DbValue.Uuid(row.User), DbValue.Integer(row.Source.Quantity), DbValue.Numeric(terms.UnitPrice), DbValue.Utc(row.Source.CreatedAt), DbValue.Text(row.HistoricalBrandName), DbValue.Text(terms.Title), DbValue.Text(row.Source.CheckoutSnapshot)), ct);
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,correlation_id) VALUES ($1,'system',$2,'legacy_import','order',$3,'Manifest-bound historical wallet order retained for review',$4)", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(context.BrandId), DbValue.Uuid(row.Target), DbValue.Uuid(manifest.Batch)), ct);
        }
        if (!await MatchesAsync(session, row, ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static Task<bool> MatchesAsync(DbSession session, LegacyHistoricalOrder row, CancellationToken ct)
    {
        var terms = row.Terms();
        return session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.orders WHERE id=$1 AND user_id=$2 AND quantity=$3 AND product_id IS NULL
                AND order_kind='product_purchase' AND payment_status='paid' AND fulfillment_status='requires_admin' AND currency='USDT'
                AND payment_method='wallet' AND unit_price_usdt=$4 AND payment_confirmed_at=$5 AND created_at=$5 AND updated_at=$5
                AND brand_public_name_snapshot=$6 AND product_title_snapshot=$7 AND payment_terms_snapshot=$8
                AND fulfillment_kind_snapshot='local' AND NOT local_first_snapshot AND NOT is_preorder AND review_code='credential_recovery'
                AND NOT auto_price_toman_snapshot AND pricing_rounding_step_snapshot=5000 AND price_calculation_version='legacy-wallet-v1'
                AND unit_price_toman IS NULL AND fx_rate_snapshot IS NULL AND discount_pct_snapshot IS NULL AND discount_units_snapshot IS NULL
                AND delivery_generation IS NULL AND release_authorized_at IS NULL AND resolution_kind IS NULL
                AND NOT EXISTS(SELECT 1 FROM shop.fulfillments WHERE order_id=$1)
                AND NOT EXISTS(SELECT 1 FROM shop.wallet_entries WHERE order_id=$1)
                AND NOT EXISTS(SELECT 1 FROM shop.payments WHERE order_id=$1)
                AND NOT EXISTS(SELECT 1 FROM shop.supplier_requests WHERE order_id=$1))
            """, DbValue.Uuid(row.Target), DbValue.Uuid(row.User), DbValue.Integer(row.Source.Quantity), DbValue.Numeric(terms.UnitPrice), DbValue.Utc(row.Source.CreatedAt), DbValue.Text(row.HistoricalBrandName), DbValue.Text(terms.Title), DbValue.Text(row.Source.CheckoutSnapshot)), r => r.GetBoolean(0), ct);
    }
}
