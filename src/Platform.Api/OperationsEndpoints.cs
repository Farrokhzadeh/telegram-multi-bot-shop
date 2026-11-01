using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;

namespace Platform.Api;

public static class OperationsEndpoints
{
    public static void MapAdministrationOperations(this IEndpointRouteBuilder endpoints, OperationsStore store)
    {
        endpoints.MapGet("/v1/admin/audit", (HttpContext http, Guid? brandId, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.AuditAsync(http.GetRequiredAdministrator(), brandId, after, limit ?? 50, http.RequestAborted), OperationsJsonContext.Default.AdminPageAuditMetadata))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/operations", (HttpContext http, Guid? brandId) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.BacklogAsync(http.GetRequiredAdministrator(), brandId, http.RequestAborted), OperationsJsonContext.Default.WorkBacklogArray))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/campaigns", (HttpContext http, Guid? brandId, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.CampaignsAsync(http.GetRequiredAdministrator(), brandId, after, limit ?? 50, http.RequestAborted), OperationsJsonContext.Default.AdminPageCampaignMetadata))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/coupons", (HttpContext http, Guid brandId, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.CouponsAsync(http.GetRequiredAdministrator(), brandId, after, limit ?? 50, http.RequestAborted), OperationsJsonContext.Default.AdminPageCouponMetadata))).RequireAdministrator();
        endpoints.MapGet("/v1/admin/coupons/{id:guid}/revisions", (HttpContext http, Guid id, Guid brandId, long? afterRevision, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.RevisionsAsync(http.GetRequiredAdministrator(), brandId, id, afterRevision, limit ?? 50, http.RequestAborted), OperationsJsonContext.Default.CouponRevisionPage))).RequireAdministrator();
    }
}

[JsonSerializable(typeof(AdminPage<AuditMetadata>))]
[JsonSerializable(typeof(WorkBacklog[]))]
[JsonSerializable(typeof(AdminPage<CampaignMetadata>))]
[JsonSerializable(typeof(AdminPage<CouponMetadata>))]
[JsonSerializable(typeof(CouponRevisionPage))]
internal sealed partial class OperationsJsonContext : JsonSerializerContext;
