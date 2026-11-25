using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
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
        if (!await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Brand>().AnyAsync(row => row.Id == context.BrandId && row.BotId == context.BotId, token), ct)) throw new MigrationRejectedException();
        if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct))
        {
            await session.RunEfStepAsync(true, (data, token) => { data.RequireImportCommand(); data.Data.Add(new Product { Id = row.Target, FulfillmentKind = terms.Kind, AvailabilityPolicy = terms.Preorder ? "local_preorder" : "immediate", Title = terms.Title, Description = terms.Description, IconRef = terms.Icon, Guidance = terms.Guidance, Active = !terms.Review }); data.Data.Add(new BrandProduct { BrandId = context.BrandId, ProductId = row.Target, Visible = !terms.Review, PriceUsdt = terms.Usd, PriceToman = terms.Toman > 0 ? terms.Toman : null, AutoPriceToman = terms.Auto }); return Task.FromResult(true); }, ct);
            await LegacyValues.AuditAsync(session, manifest, row, "product", ct);
        }
        if (!await MatchesAsync(session, manifest, row, ct) || !await LegacyValues.MatchesAuditAsync(session, manifest, row, "product", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct); return new(row.Target, terms.Review);
    }
    internal static bool RequiresReview(ArchivedLegacyRecord row) => Terms(row).Review;
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
        decimal? toman = t.Toman > 0 ? t.Toman : null;
        return session.RunEfStepAsync(false, (data, token) => (from p in data.Data.Set<Product>() join b in data.Data.Set<BrandProduct>() on p.Id equals b.ProductId where p.Id == row.Target && b.BrandId == manifest.TargetBrand && p.FulfillmentKind == t.Kind && p.AvailabilityPolicy == (t.Preorder ? "local_preorder" : "immediate") && p.Title == t.Title && p.Description == t.Description && p.IconRef == t.Icon && p.Guidance == t.Guidance && p.Active == !t.Review && b.Visible == !t.Review && b.PriceUsdt == t.Usd && b.PriceToman == toman && b.AutoPriceToman == t.Auto && p.Revision == 0 && b.Revision == 0 && p.ActiveSupplierSourceId == null && !data.Data.Set<InventoryItem>().Any(i => i.ProductId == p.Id) select p.Id).AnyAsync(token), ct);
    }
    private sealed record CatalogTerms(string Title, string? Description, string? Icon, string? Guidance, decimal Usd, decimal Toman, bool Auto, string Kind, bool Preorder, bool Review);
}
