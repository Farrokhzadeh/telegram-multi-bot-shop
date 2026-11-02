using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Api;

public sealed class AiReadEndpointMetadata
{
    private AiReadEndpointMetadata() { }
    public static AiReadEndpointMetadata Instance { get; } = new();
}
public static class AiContextEndpoints
{
    public static void MapAiContext(this IEndpointRouteBuilder endpoints, AiContextStore store)
    {
        endpoints.MapGet("/v1/ai/context", async (HttpContext http) =>
        {
            var scope = AiReadGuardMiddleware.Scope(http) ?? throw new UnauthorizedAccessException();
            var result = await store.ReadAsync(scope, http.RequestAborted);
            return result is null ? Failure(http, 404, "not_found") : Results.Json(result, AiContextJson.Default.AiShopContext);
        }).AddEndpointFilter((context, next) => next(context)).WithMetadata(AiReadEndpointMetadata.Instance);
    }
    internal static IResult Failure(HttpContext http, int status, string code) => Results.Json(new ApiFailure(code, DiagnosticsMiddleware.Correlation(http) ?? Guid.NewGuid()), DiagnosticsJsonContext.Default.ApiFailure, statusCode: status);
}
public sealed class AiReadGuardMiddleware(RequestDelegate next, AiScopeRegistry registry)
{
    private sealed record ScopeFeature(AiReadScope Value);
    internal static AiReadScope? Scope(HttpContext http) => http.Features.Get<ScopeFeature>()?.Value;
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<AiReadEndpointMetadata>() is null) { await next(http); return; }
        var auth = http.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
        var scope = auth?.Succeeded == true && auth.Ticket is not null ? registry.Resolve(auth.Ticket.AuthenticationScheme, auth.Ticket.Principal, DateTime.UtcNow) : null;
        if (scope is null) { await Deny(http, 401, "unauthenticated"); return; }
        if (http.Request.QueryString.HasValue || http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding") || http.Request.Headers.ContainsKey("X-Brand-Id") || http.Request.Headers.ContainsKey("X-User-Id") || http.Request.Headers.ContainsKey("X-Telegram-Id"))
        { await Deny(http, 400, "validation_failed"); return; }
        http.Features.Set(new ScopeFeature(scope));
        try { await next(http); }
        finally { http.Features.Set<ScopeFeature>(null); }
    }
    private static async Task Deny(HttpContext http, int status, string code)
    {
        Guid id = DiagnosticsMiddleware.Correlation(http) ?? Guid.NewGuid();
        http.Response.StatusCode = status; http.Response.ContentType = "application/json"; http.Response.Headers["X-Request-Id"] = id.ToString("D");
        if (status == 401) http.Response.Headers.WWWAuthenticate = AiScopeRegistry.Scheme;
        await http.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ApiFailure(code, id), DiagnosticsJsonContext.Default.ApiFailure), http.RequestAborted);
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiShopContext))]
public sealed partial class AiContextJson : JsonSerializerContext;
