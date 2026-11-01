using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;

namespace Platform.Api;

public static class PaymentEndpoints
{
    public static IEndpointConventionBuilder MapPaymentStatus(this IEndpointRouteBuilder endpoints, DbSessionFactory sessions) =>
        endpoints.MapGet("/v1/admin/payments/{id:guid}", (HttpContext context, Guid id, Guid brandId) => AdminHttp.HandleAsync(context, () => ReadAsync(context, sessions, id, brandId, false))).RequireAdministrator();
    internal static async Task<IResult> ReadAsync(HttpContext context, DbSessionFactory sessions, Guid id, Guid brand, bool bank)
    {
        await using var session = await sessions.BeginAsync(context.RequestAborted);
        PaymentStatusView? data = await PaymentReadStore.ForAdminAsync(session, context.GetRequiredAdministrator(), brand, id, context.RequestAborted);
        return data is null || (bank && data.Method != "bank") ? Results.NotFound() : Results.Json(data, PaymentApiJsonContext.Default.PaymentStatusView);
    }
}

[JsonSerializable(typeof(PaymentStatusView))]
internal sealed partial class PaymentApiJsonContext : JsonSerializerContext;
