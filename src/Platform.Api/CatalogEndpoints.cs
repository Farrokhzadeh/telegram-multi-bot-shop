using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Api;

public sealed record AdminChange<T>(T Value, long ExpectedRevision, Guid CommandId);
public sealed record StockImport(Guid ProductId, InventoryImportItem[] Items, Guid CommandId)
{
    public override string ToString() => "StockImport [redacted]";
}
public sealed record ChannelEnforcementChange(bool Previous, bool Enabled, long ExpectedRevision, Guid CommandId);
public static class CatalogEndpoints
{
    public static void MapCatalogAdministration(this IEndpointRouteBuilder endpoints, DbSessionFactory sessions, AdminCatalogStore store, Func<Guid, BrandContext?> resolve)
        => MapCore(endpoints, sessions, store, resolve, _ => store, _ => sessions);
    public static void MapScopedCatalogAdministration(this IEndpointRouteBuilder endpoints, WriterRegistry writers, SecretSnapshot secrets)
        => MapCore(endpoints, writers.Shared, new AdminCatalogStore(writers.Shared, secrets), writers.Resolve, context => new AdminCatalogStore(writers.ForBrand(context), secrets), writers.ForBrand);
    private static void MapCore(IEndpointRouteBuilder endpoints, DbSessionFactory sessions, AdminCatalogStore store, Func<Guid, BrandContext?> resolve, Func<BrandContext, AdminCatalogStore> brandStore, Func<BrandContext, DbSessionFactory> brandSessions)
    {
        endpoints.MapGet("/v1/admin/brands", (HttpContext http, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.BrandsAsync(http.GetRequiredAdministrator(), after, limit ?? 50, http.RequestAborted), CatalogApiJsonContext.Default.AdminPageAdminBrand))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/brands/{brand:guid}/policy", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            return Results.Json(await brandStore(context).PolicyAsync(http.GetRequiredAdministrator(), context, http.RequestAborted), CatalogApiJsonContext.Default.BrandPolicyDetail);
        })).RequireAdministrator();
        endpoints.MapGet("/v1/admin/brands/{brand:guid}/exchange-rate", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            return Results.Json(await brandStore(context).FxAsync(http.GetRequiredAdministrator(), context, http.RequestAborted), CatalogApiJsonContext.Default.ExchangeRateMetadata);
        })).RequireAdministrator();
        endpoints.MapGet("/v1/admin/products", (HttpContext http, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.ProductsAsync(http.GetRequiredAdministrator(), after, limit ?? 50, http.RequestAborted), CatalogApiJsonContext.Default.AdminPageAdminProduct))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/inventory", (HttpContext http, Guid productId, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.InventoryAsync(http.GetRequiredAdministrator(), productId, after, limit ?? 50, http.RequestAborted), CatalogApiJsonContext.Default.AdminPageInventoryMetadata))).RequireAdministrator();
        endpoints.MapPatch("/v1/admin/brands/{brand:guid}", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, CatalogApiJsonContext.Default.AdminChangeBrandPolicyEdit);
            return Saved(await brandStore(context).PolicyAsync(http.GetRequiredAdministrator(), context, input.Value, input.ExpectedRevision, input.CommandId, http.RequestAborted));
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/channel-enforcement", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, CatalogApiJsonContext.Default.ChannelEnforcementChange);
            await using var session = await brandSessions(context).BeginAsync(http.RequestAborted);
            long revision = await BrandPolicyStore.SetEnforcementAsync(session, http.GetRequiredAdministrator(), context.BrandId, input.ExpectedRevision, input.Previous, input.Enabled, input.CommandId, input.CommandId, http.RequestAborted);
            await session.CommitAsync(http.RequestAborted); return Saved(revision);
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/products/{id:guid}", (HttpContext http, Guid id) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, CatalogApiJsonContext.Default.AdminChangeMasterProductEdit);
            return Saved(await store.ProductAsync(http.GetRequiredAdministrator(), id, input.Value, input.ExpectedRevision, input.CommandId, http.RequestAborted));
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/products/{id:guid}/brands/{brand:guid}", (HttpContext http, Guid id, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, CatalogApiJsonContext.Default.AdminChangeListingEdit);
            return Saved(await brandStore(context).ListingAsync(http.GetRequiredAdministrator(), context, id, input.Value, input.ExpectedRevision, input.CommandId, http.RequestAborted));
        })).RequireAdministrator();
        endpoints.MapPost("/v1/admin/inventory/import", (Func<HttpContext, Task<IResult>>)((HttpContext http) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, CatalogApiJsonContext.Default.StockImport);
            await store.ImportAsync(http.GetRequiredAdministrator(), input.ProductId, input.Items, input.CommandId, http.RequestAborted);
            return Results.NoContent();
        }))).RequireAdministrator();
    }
    private static IResult Saved(long revision) => Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AdminPage<AdminBrand>))]
[JsonSerializable(typeof(BrandPolicyDetail))]
[JsonSerializable(typeof(ExchangeRateMetadata))]
[JsonSerializable(typeof(AdminPage<AdminProduct>))]
[JsonSerializable(typeof(AdminPage<InventoryMetadata>))]
[JsonSerializable(typeof(AdminChange<BrandPolicyEdit>))]
[JsonSerializable(typeof(AdminChange<MasterProductEdit>))]
[JsonSerializable(typeof(AdminChange<ListingEdit>))]
[JsonSerializable(typeof(ChannelEnforcementChange))]
[JsonSerializable(typeof(StockImport))]
internal sealed partial class CatalogApiJsonContext : JsonSerializerContext;
