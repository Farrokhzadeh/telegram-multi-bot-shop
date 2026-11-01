using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;
using Platform.Core;
using Platform.Security;

namespace Platform.Api;

public sealed record BotTokenChange(string Token, long ExpectedRevision, Guid CommandId)
{
    public override string ToString() => "BotTokenChange [redacted]";
}
public sealed record RequiredChannelsChange(RequiredChannel[] Channels, long ExpectedRevision, Guid CommandId);
public sealed record ConfigurationSaved(long Revision);
public static class BrandEndpoints
{
    public static void MapBrandAdministration(this IEndpointRouteBuilder endpoints, BrandAdministrationStore store, Func<Guid, BrandContext?> resolve,
        Func<BotToken, CancellationToken, Task<VerifiedBotProfile>> verifyToken,
        Func<BrandContext, IReadOnlyList<RequiredChannel>, CancellationToken, Task<bool>> verifyChannels)
    {
        endpoints.MapGet("/v1/admin/brands/{brand:guid}", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            BrandContext? context = resolve(brand);
            return context is null ? Results.NotFound() : Results.Json(await store.ReadAsync(http.GetRequiredAdministrator(), context, http.RequestAborted), BrandAdminJsonContext.Default.BrandConfiguration);
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/bot-token", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            BrandContext? context = resolve(brand);
            if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, BrandAdminJsonContext.Default.BotTokenChange);
            using var token = BotToken.Parse(input.Token);
            long revision = await store.RotateTokenAsync(http.GetRequiredAdministrator(), context, token, input.ExpectedRevision, input.CommandId, verifyToken, http.RequestAborted);
            return Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/required-channels", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            BrandContext? context = resolve(brand);
            if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, BrandAdminJsonContext.Default.RequiredChannelsChange);
            if (input.Channels is null) throw new ArgumentException("Required channels are missing.");
            long revision = await store.SetChannelsAsync(http.GetRequiredAdministrator(), context, input.Channels, input.ExpectedRevision, input.CommandId, verifyChannels, http.RequestAborted);
            return Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
        })).RequireAdministrator();
    }

}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(BotTokenChange))]
[JsonSerializable(typeof(RequiredChannelsChange))]
[JsonSerializable(typeof(BrandConfiguration))]
[JsonSerializable(typeof(ConfigurationSaved))]
internal sealed partial class BrandAdminJsonContext : JsonSerializerContext;
