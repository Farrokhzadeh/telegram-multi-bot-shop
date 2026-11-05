using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;
public sealed record LegacyCatalogImportResult(Guid Product, bool RequiresReview);
public sealed class LegacyCatalogImportWorkflow(DbSessionFactory sessions, SecretSnapshot secrets, Guid approvedMasterBrand)
{
    public async Task<LegacyCatalogImportResult> ImportAsync(BrandContext context, VerifiedImportManifest manifest, ArchivedLegacyRecord row, CancellationToken ct = default)
    {
        if (context.BrandId != approvedMasterBrand || manifest.TargetBrand != approvedMasterBrand || row.Source.Projection != LegacyProjection.ProductRecord || !new LegacyRecordArchive(secrets).Matches(context, row, row.Evidence)) throw new MigrationRejectedException();
        using var document = row.Source.Open(); var source = document.RootElement;
        var terms = Terms(row); var proof = row.Source.Proof(row.Target, context, secrets); manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        if (!await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brands WHERE id=$1 AND bot_id=$2)", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
        {
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title,description,icon_ref,guidance,active) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", DbValue.Uuid(row.Target), DbValue.Text(terms.Kind), DbValue.Text(terms.Preorder ? "local_preorder" : "immediate"), DbValue.Text(terms.Title), LegacyValues.String(terms.Description), LegacyValues.String(terms.Icon), LegacyValues.String(terms.Guidance), DbValue.Boolean(!terms.Review)), ct);
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,visible,price_usdt,price_toman,auto_price_toman) VALUES($1,$2,$3,$4,$5,$6)", DbValue.Uuid(context.BrandId), DbValue.Uuid(row.Target), DbValue.Boolean(!terms.Review), DbValue.Numeric(terms.Usd), terms.Toman > 0 ? DbValue.Numeric(terms.Toman) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric), DbValue.Boolean(terms.Auto)), ct);
            await LegacyValues.AuditAsync(session, manifest, row, "product", ct);
        }
        if (!await MatchesAsync(session, manifest, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row, "product", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct); return new(row.Target, terms.Review);
    }
    private static CatalogTerms Terms(ArchivedLegacyRecord row)
    {
        using var document = row.Source.Open(); var s = document.RootElement;
        string title = LegacyValues.Text(s, "name"), type = LegacyValues.Text(s, "product_type"); decimal usd = LegacyValues.Number(s, "price"), toman = LegacyValues.Number(s, "price_irr");
        string? description = LegacyValues.Optional(s, "description"), guidance = LegacyValues.Optional(s, "post_purchase"), icon = LegacyValues.Optional(s, "icon_custom_emoji_id");
        if (s.GetProperty("id").GetInt64() <= 0 || !s.GetProperty("active").GetBoolean() || s.GetProperty("is_removed").GetBoolean() || string.IsNullOrWhiteSpace(title) || title.Length > 160 || usd <= 0 || toman < 0 || description?.Length > 16384 || guidance?.Length > 16384 || icon?.Length > 1024 || type is not ("credential" or "service" or "api" or "preorder")) throw new MigrationRejectedException();
        bool review = !string.IsNullOrEmpty(LegacyValues.Optional(s, "rules")) || !string.IsNullOrEmpty(LegacyValues.Optional(s, "post_purchase_media_id")) || type == "service";
        return new(title, description, icon, guidance, usd, toman, s.GetProperty("auto_price").GetBoolean(), type == "service" ? "manual" : type == "api" ? "supplier" : "local", type == "preorder", review);
    }
    internal static Task<bool> MatchesAsync(DbSession session, VerifiedImportManifest manifest, ArchivedLegacyRecord row, CancellationToken ct)
    {
        var t = Terms(row);
        return session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.products p JOIN shop.brand_products b ON b.product_id=p.id WHERE p.id=$1 AND b.brand_id=$2
                AND p.fulfillment_kind=$3 AND p.availability_policy=$4 AND p.title=$5 AND p.description IS NOT DISTINCT FROM $6 AND p.icon_ref IS NOT DISTINCT FROM $7 AND p.guidance IS NOT DISTINCT FROM $8
                AND p.active=$9 AND b.visible=$9 AND b.price_usdt=$10 AND b.price_toman IS NOT DISTINCT FROM $11 AND b.auto_price_toman=$12 AND p.revision=0 AND b.revision=0
                AND p.active_supplier_source_id IS NULL AND NOT EXISTS(SELECT 1 FROM shop.inventory_items WHERE product_id=p.id))
            """, DbValue.Uuid(row.Target), DbValue.Uuid(manifest.TargetBrand), DbValue.Text(t.Kind), DbValue.Text(t.Preorder ? "local_preorder" : "immediate"), DbValue.Text(t.Title), LegacyValues.String(t.Description), LegacyValues.String(t.Icon), LegacyValues.String(t.Guidance), DbValue.Boolean(!t.Review), DbValue.Numeric(t.Usd), t.Toman > 0 ? DbValue.Numeric(t.Toman) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric), DbValue.Boolean(t.Auto)), r => r.GetBoolean(0), ct);
    }
    private sealed record CatalogTerms(string Title, string? Description, string? Icon, string? Guidance, decimal Usd, decimal Toman, bool Auto, string Kind, bool Preorder, bool Review);
}
