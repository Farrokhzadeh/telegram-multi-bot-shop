using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Persistence;

namespace Platform.Api;

public static class ManualDepositEndpoints
{
    public static IEndpointConventionBuilder MapManualDepositStatus(this IEndpointRouteBuilder endpoints, DbSessionFactory sessions) =>
        endpoints.MapGet("/v1/admin/manual-deposits/{id:guid}", (HttpContext context, Guid id, Guid brandId) => PaymentEndpoints.ReadAsync(context, sessions, id, brandId, true)).RequireAdministrator();
}
