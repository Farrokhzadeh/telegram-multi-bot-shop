using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Fulfillment.Providers;
using Platform.Persistence;

namespace Platform.Api;

public sealed record AkundingSourceChoice(long ProductRevision, long ExternalProductId, Guid SourceId, Guid CommandId);
public static class SupplierCatalogEndpoints
{
    public static void MapAkundingSupplierAdministration(this IEndpointRouteBuilder endpoints, AkundingSourceInspection inspection, SupplierSourceSelectionStore sources)
    {
        endpoints.MapGet("/v1/admin/products/{product:guid}/suppliers/akunding/catalog", (HttpContext http, Guid product, long productRevision, string? search, long? after, int? limit) => HandleAsync(http, async () =>
            Results.Json(await inspection.BrowseAsync(http.GetRequiredAdministrator(), product, productRevision, search ?? "", after, limit ?? 50, http.RequestAborted), SupplierCatalogJsonContext.Default.AkundingCatalogPage))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/products/{product:guid}/supplier-sources", (HttpContext http, Guid product, Guid? after, int? limit) => HandleAsync(http, async () =>
            Results.Json(await sources.SourcesAsync(http.GetRequiredAdministrator(), product, after, limit ?? 50, http.RequestAborted), SupplierCatalogJsonContext.Default.AdminPageSupplierSourceMetadata))).RequireAdministrator();
        endpoints.MapPost("/v1/admin/products/{product:guid}/suppliers/akunding/sources", (HttpContext http, Guid product) => HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, SupplierCatalogJsonContext.Default.AkundingSourceChoice);
            return Results.Json(await inspection.AddCandidateAsync(http.GetRequiredAdministrator(), product, input.ProductRevision, input.ExternalProductId, input.SourceId, input.CommandId, http.RequestAborted), SupplierCatalogJsonContext.Default.AkundingSourcePreview);
        })).RequireAdministrator();
        endpoints.MapGet("/v1/admin/products/{product:guid}/suppliers/akunding/sources/{source:guid}", (HttpContext http, Guid product, Guid source, long sourceRevision) => HandleAsync(http, async () =>
            Results.Json(await inspection.InspectAsync(http.GetRequiredAdministrator(), product, source, sourceRevision, http.RequestAborted), SupplierCatalogJsonContext.Default.AkundingSourcePreview))).RequireAdministrator();
    }
    private static Task<IResult> HandleAsync(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        return AdminHttp.HandleAsync(http, action);
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AkundingCatalogPage))]
[JsonSerializable(typeof(AkundingSourcePreview))]
[JsonSerializable(typeof(AkundingSourceChoice))]
[JsonSerializable(typeof(AdminPage<SupplierSourceMetadata>))]
internal sealed partial class SupplierCatalogJsonContext : JsonSerializerContext;
