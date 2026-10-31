using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Platform.Security;

namespace Platform.Api;

/// <summary>Server-owned endpoint metadata. Admin endpoints use their separate authorization boundary.</summary>
public sealed class CustomerBrandEndpointMetadata
{
    private CustomerBrandEndpointMetadata() { }
    public static CustomerBrandEndpointMetadata Instance { get; } = new();
}

public static class CustomerBrandEndpointExtensions
{
    public static TBuilder RequireCustomerBrandContext<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(CustomerBrandEndpointMetadata.Instance));
        return builder;
    }

    public static BrandContext GetRequiredBrandContext(this HttpContext context) =>
        BrandContextMiddleware.GetBoundContext(context) ?? throw new InvalidOperationException("Authenticated brand context is required.");
}

/// <summary>
/// Run after routing and authentication. Only a successful registered bot authentication ticket can
/// bind context. Customer headers, route/query values and JSON properties cannot choose any brand.
/// JSON is inspected in bounded memory, preserving exact bytes for downstream source-generated DTO readers.
/// </summary>
public sealed class BrandContextMiddleware
{
    private readonly RequestDelegate next;
    private readonly BotBrandRegistry registry;
    private readonly int maximumBodyBytes;

    public BrandContextMiddleware(RequestDelegate next, BotBrandRegistry registry, int maximumBodyBytes = 1048576)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(registry);
        if (maximumBodyBytes is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        this.next = next;
        this.registry = registry;
        this.maximumBodyBytes = maximumBodyBytes;
    }

    internal static BrandContext? GetBoundContext(HttpContext context) => context.Features.Get<BoundContext>()?.Context;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CustomerBrandEndpointMetadata>() is null)
        {
            await next(context);
            return;
        }

        // HttpContext.User alone is deliberately insufficient: use the authentication handler's ticket.
        var result = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
        if (GetBoundContext(context) is not null || result is not { Succeeded: true, Ticket: not null } ||
            !registry.TryResolveAuthenticated(result.Ticket.AuthenticationScheme, result.Ticket.Principal, out var brand))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var request = context.Request;
        if (request.Headers.Keys.Any(name => BrandSelector.IsReservedName(name, header: true)) ||
            request.Query.Keys.Any(name => BrandSelector.IsReservedName(name)) ||
            request.RouteValues.Keys.Any(name => BrandSelector.IsReservedName(name)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (request.ContentLength > maximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var original = request.Body;
        var originalPosition = original.CanSeek ? original.Position : (long?)null;
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await original.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                    maximumBodyBytes - checked((int)buffered.Length) + 1)), context.RequestAborted);
                if (read == 0) break;
                if (buffered.Length + read > maximumBodyBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                await buffered.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
            if (buffered.Length > 0)
            {
                if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding"))
                {
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    return;
                }
                try
                {
                    using var document = JsonDocument.Parse(buffered.GetBuffer().AsMemory(0, checked((int)buffered.Length)),
                        new JsonDocumentOptions { MaxDepth = 64 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object || ContainsSelector(document.RootElement))
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }

            buffered.Position = 0;
            request.Body = buffered;
            context.Features.Set(new BoundContext(brand!));
            await next(context);
        }
        finally
        {
            context.Features.Set<BoundContext>(null);
            request.Body = original;
            if (originalPosition.HasValue) original.Position = originalPosition.Value;
            CryptographicOperations.ZeroMemory(buffer);
            if (buffered.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }

    private static bool ContainsSelector(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (BrandSelector.IsReservedName(property.Name) || ContainsSelector(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsSelector(item)) return true;
            }
        }
        return false;
    }

    private sealed record BoundContext(BrandContext Context);
}
