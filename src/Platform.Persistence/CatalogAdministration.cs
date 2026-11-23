using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

internal static class CatalogAdministration
{
    internal static Task<List<AdminProduct>> ProductsAsync(EfSession data, Guid? after, int count, CancellationToken ct) =>
        data.Data.Set<Entities.Product>().Where(value => after == null || value.Id.CompareTo(after.Value) > 0).OrderBy(value => value.Id).Take(count)
            .Select(value => new AdminProduct(value.Id, value.Title, value.FulfillmentKind, value.AvailabilityPolicy, value.LocalFirst, value.Active, value.Revision)).ToListAsync(ct);
    internal static async Task<ExchangeRateMetadata> FxAsync(EfSession data, BrandContext context, CancellationToken ct)
    {
        var value = await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId)
            .Select(value => new { value.CurrentFx, value.FxSource, value.FxFetchedAt, value.FxErrorCode }).SingleAsync(ct);
        return new(value.CurrentFx is decimal rate ? ExactDecimal.Format(rate) : null, value.FxSource, value.FxFetchedAt, value.FxErrorCode);
    }
    internal static async Task<AdminPage<InventoryMetadata>> InventoryAsync(EfSession data, Guid product, Guid? after, int limit, CancellationToken ct)
    {
        var rows = await data.Data.Set<Entities.InventoryItem>().Where(value => value.ProductId == product && (after == null || value.Id.CompareTo(after.Value) > 0))
            .OrderBy(value => value.Id).Take(limit + 1).Select(value => new InventoryMetadata(value.Id, value.ProductId, value.State, value.OriginKind, value.CreatedAt)).ToArrayAsync(ct);
        return new(rows.Take(limit).ToArray(), rows.Length > limit ? rows[limit - 1].Id : null);
    }
    internal static async Task<long> ProductAsync(EfSession data, AuthenticatedAdmin actor, Guid id, MasterProductEdit value, long revision, CancellationToken ct)
    {
        await data.RequireAdminAsync(actor, null, ct);
        var row = await data.Data.Set<Entities.Product>().SingleOrDefaultAsync(product => product.Id == id, ct);
        if (row is null)
        {
            if (revision != 0) data.RejectConflict("Product revision conflict.");
            row = new Entities.Product { Id = id };
            data.Data.Add(row);
        }
        else
        {
            if (row.Revision != revision) data.RejectConflict("Product revision conflict.");
            row.UpdatedAt = await data.ClockAsync(data.Data.Set<Entities.Product>().Where(product => product.Id == id), ct);
        }
        row.Title = value.Title; row.Description = value.Description; row.IconRef = value.Icon; row.Guidance = value.Guidance;
        row.FulfillmentKind = value.Fulfillment; row.AvailabilityPolicy = value.Availability; row.LocalFirst = value.LocalFirst; row.Active = value.Active;
        row.Revision = checked(revision + 1);
        return row.Revision;
    }
    internal static async Task<long> ListingAsync(EfSession data, AuthenticatedAdmin actor, BrandContext context, Guid product, ListingEdit value, long revision, CancellationToken ct)
    {
        await data.RequireAdminAsync(actor, null, ct);
        bool requireActive = data.Data is SqliteAkantiDbContext;
        if (!await data.Data.Set<Entities.Product>().AnyAsync(row => row.Id == product, ct) ||
            !await data.Data.Set<Entities.Brand>().AnyAsync(row => row.Id == context.BrandId && row.BotId == context.BotId && (!requireActive || row.Active), ct))
            data.RejectScope("The listing scope is unavailable.");
        var row = await data.Data.Set<Entities.BrandProduct>().SingleOrDefaultAsync(row => row.BrandId == context.BrandId && row.ProductId == product, ct);
        if (row is null)
        {
            if (revision != 0) data.RejectConflict("Listing revision conflict.");
            row = new Entities.BrandProduct { BrandId = context.BrandId, ProductId = product };
            data.Data.Add(row);
        }
        else if (row.Revision != revision) data.RejectConflict("Listing revision conflict.");
        row.Visible = value.Visible; row.TitleMode = value.TitleMode; row.TitleOverride = value.Title; row.DescriptionMode = value.DescriptionMode; row.DescriptionOverride = value.Description;
        row.IconMode = value.IconMode; row.IconOverride = value.Icon; row.GuidanceMode = value.GuidanceMode; row.GuidanceOverride = value.Guidance;
        row.PriceUsdt = ExactDecimal.Parse(value.PriceUsd); row.PriceToman = value.PriceToman is null ? null : ExactDecimal.Parse(value.PriceToman);
        row.AutoPriceToman = value.AutomaticToman; row.AllowWallet = value.Wallet; row.AllowCrypto = value.Crypto; row.AllowBank = value.Bank;
        row.MinQuantity = value.Minimum; row.MaxQuantity = value.Maximum; row.Revision = checked(revision + 1);
        row.UpdatedAt = await data.BrandClockAsync(context.BrandId, ct);
        return row.Revision;
    }
    internal static async Task ImportAsync(EfSession data, AuthenticatedAdmin actor, Guid product, InventoryImportItem[] items, SecretSnapshot secrets, CancellationToken ct)
    {
        await data.RequireAdminAsync(actor, null, ct);
        if (!await data.Data.Set<Entities.Product>().AnyAsync(value => value.Id == product, ct)) data.RejectScope("Inventory product is unavailable.");
        var previous = await data.Data.Set<Entities.InventoryItem>().Select(value => value.CredentialKeyId).Distinct().OrderBy(value => value).Take(33).ToArrayAsync(ct);
        if (previous.Length > 32) data.RejectScope("Inventory key history needs review.");
        string key = secrets.CurrentInventoryKeyId;
        string[] keys = previous.Append(key).Distinct(StringComparer.Ordinal).ToArray();
        var cipher = new InventoryCipher(secrets);
        int count = 0;
        foreach (var item in items)
        {
            byte[] plain = Encoding.UTF8.GetBytes(item.Credential);
            try
            {
                bool exists = false;
                foreach (string known in keys)
                {
                    byte[] fingerprint = secrets.UseInventoryKey(known, bytes => HMACSHA256.HashData(bytes, plain));
                    var duplicate = await data.Data.Set<Entities.InventoryItem>().AsNoTracking().Where(value => value.SourceFingerprint == fingerprint)
                        .Select(value => new { value.Id, value.ProductId }).FirstOrDefaultAsync(ct);
                    if (duplicate is null) continue;
                    if (duplicate.Id != item.Id || duplicate.ProductId != product) data.RejectConflict("Duplicate inventory content.");
                    exists = true;
                    break;
                }
                if (exists) continue;
                data.Data.Add(new Entities.InventoryItem
                {
                    Id = item.Id,
                    ProductId = product,
                    CredentialCiphertext = cipher.Encrypt(item.Id, plain),
                    CredentialKeyId = key,
                    SourceFingerprint = secrets.UseInventoryKey(key, bytes => HMACSHA256.HashData(bytes, plain)),
                    OriginKind = "local_import"
                });
                await data.FlushAsync(ct, false);
                if (++count % 128 == 0) data.Data.ChangeTracker.Clear();
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
}
