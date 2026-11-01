using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;

namespace Platform.Api;

public sealed record CampaignLaunch(Guid BatchId, CampaignDraft[] Variants);
public sealed record CampaignTransition(Guid CommandId, string Reason);
public static class CampaignEndpoints
{
    public static void MapCampaignAdministration(this IEndpointRouteBuilder endpoints, CampaignStore store)
    {
        endpoints.MapPost("/v1/admin/campaigns", (HttpContext http) => AdminHttp.HandleAsync(async () =>
        {
            var input = await AdminHttp.ReadAsync(http, CampaignJsonContext.Default.CampaignLaunch);
            Guid id = await store.CreateAsync(http.GetRequiredAdministrator(), input.BatchId, input.Variants, http.RequestAborted);
            return Results.Json(new AdminIdentity(id), OrderApiJsonContext.Default.AdminIdentity);
        })).RequireAdministrator();
        foreach (bool cancel in new[] { false, true })
            endpoints.MapPost("/v1/admin/campaign-batches/{id:guid}/" + (cancel ? "cancel" : "send"), (HttpContext http, Guid id) => AdminHttp.HandleAsync(async () =>
            {
                var input = await AdminHttp.ReadAsync(http, CampaignJsonContext.Default.CampaignTransition);
                await store.TransitionAsync(http.GetRequiredAdministrator(), id, cancel, input.CommandId, input.Reason, http.RequestAborted); return Results.NoContent();
            })).RequireAdministrator();
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CampaignLaunch))]
[JsonSerializable(typeof(CampaignTransition))]
internal sealed partial class CampaignJsonContext : JsonSerializerContext;
