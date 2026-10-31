using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Security;

namespace Platform.Api;

public sealed class AdministratorEndpointMetadata
{
    private AdministratorEndpointMetadata() { }
    public static AdministratorEndpointMetadata Instance { get; } = new();
}

public static class AdministratorEndpointExtensions
{
    public static TBuilder RequireAdministrator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(AdministratorEndpointMetadata.Instance));
        return builder;
    }
    public static AuthenticatedAdmin GetRequiredAdministrator(this HttpContext context) =>
        AdminGuardMiddleware.Actor(context) ?? throw new InvalidOperationException("Administrator context is required.");
}

/// <summary>Explicit bearer transport only. Database authenticator must check active sessions on every request.</summary>
public sealed class AdminGuardMiddleware(RequestDelegate next, Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>> authenticate)
{
    internal static AuthenticatedAdmin? Actor(HttpContext context) => context.Features.Get<AdminFeature>()?.Actor;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AdministratorEndpointMetadata>() is null)
        {
            await next(context);
            return;
        }
        var header = context.Request.Headers.Authorization;
        if (Actor(context) is not null || header.Count != 1 || header[0] is not { Length: 50 } value ||
            !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Deny(context);
            return;
        }
        AdminToken token;
        try { token = AdminToken.Parse(value[7..]); }
        catch (AdminAuthenticationException) { Deny(context); return; }
        using (token)
        {
            AuthenticatedAdmin? actor;
            try { actor = await authenticate(token, context.RequestAborted); }
            catch (AdminAuthenticationException) { Deny(context); return; }
            if (actor is null) { Deny(context); return; }
            context.Features.Set(new AdminFeature(actor));
            try { await next(context); }
            finally { context.Features.Set<AdminFeature>(null); }
        }
    }
    private static void Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
    }
    private sealed record AdminFeature(AuthenticatedAdmin Actor);
}
