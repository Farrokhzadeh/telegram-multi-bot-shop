using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;
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
        endpoints.MapGet("/v1/admin/brands/{brand:guid}", async Task<IResult> (HttpContext http, Guid brand) =>
        {
            BrandContext? context = resolve(brand);
            return context is null ? Results.NotFound() : Results.Json(await store.ReadAsync(http.GetRequiredAdministrator(), context, http.RequestAborted), BrandAdminJsonContext.Default.BrandConfiguration);
        }).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/bot-token", async Task<IResult> (HttpContext http, Guid brand) =>
        {
            BrandContext? context = resolve(brand);
            if (context is null) return Results.NotFound();
            var input = await ReadAsync(http, BrandAdminJsonContext.Default.BotTokenChange);
            using var token = BotToken.Parse(input.Token);
            long revision = await store.RotateTokenAsync(http.GetRequiredAdministrator(), context, token, input.ExpectedRevision, input.CommandId, verifyToken, http.RequestAborted);
            return Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
        }).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/required-channels", async Task<IResult> (HttpContext http, Guid brand) =>
        {
            BrandContext? context = resolve(brand);
            if (context is null) return Results.NotFound();
            var input = await ReadAsync(http, BrandAdminJsonContext.Default.RequiredChannelsChange);
            if (input.Channels is null) throw new ArgumentException("Required channels are missing.");
            long revision = await store.SetChannelsAsync(http.GetRequiredAdministrator(), context, input.Channels, input.ExpectedRevision, input.CommandId, verifyChannels, http.RequestAborted);
            return Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
        }).RequireAdministrator();
    }
    private static async Task<T> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> type)
    {
        if (!http.Request.HasJsonContentType()) throw new BadHttpRequestException("JSON content is required.", StatusCodes.Status415UnsupportedMediaType);
        byte[] bytes = new byte[1048577];
        try
        {
            int length = 0, count;
            while (length < bytes.Length && (count = await http.Request.Body.ReadAsync(bytes.AsMemory(length), http.RequestAborted)) != 0) length += count;
            if (length == bytes.Length) throw new BadHttpRequestException("Request is too large.", StatusCodes.Status413PayloadTooLarge);
            return JsonSerializer.Deserialize(bytes.AsSpan(0, length), type) ?? throw new ArgumentException("Request is required.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BotTokenChange))]
[JsonSerializable(typeof(RequiredChannelsChange))]
[JsonSerializable(typeof(BrandConfiguration))]
[JsonSerializable(typeof(ConfigurationSaved))]
internal sealed partial class BrandAdminJsonContext : JsonSerializerContext;
