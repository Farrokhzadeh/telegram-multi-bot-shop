using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;

namespace Platform.Api;

public static class BotActivityEndpoints
{
    public static void MapBotActivityAdministration(this IEndpointRouteBuilder endpoints, BotActivityStore store)
    {
        endpoints.MapGet("/v1/admin/bot-activity", (HttpContext http, Guid? brandId, Guid? memberId, DateTime? from, DateTime? until, string? action, string? cursor, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await store.ReadAsync(http.GetRequiredAdministrator(), new(brandId, memberId, from, until, action), cursor, limit ?? 50, http.RequestAborted), BotActivityJsonContext.Default.BotActivityPage))).RequireAdministrator();
    }
}

[JsonSerializable(typeof(BotActivityPage))]
internal sealed partial class BotActivityJsonContext : JsonSerializerContext;
