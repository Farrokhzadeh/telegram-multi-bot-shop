using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Payments;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Api;

public sealed record ReceiptDecision(Guid BrandId, bool Approve, long ExpectedRevision, Guid CommandId, string Reason);
public sealed record CouponEditCommand(Guid BrandId, CouponOwnerEdit Value, long ExpectedRevision, AuditReason Reason, Guid CommandId);
public sealed record WalletCommand(Guid BrandId, WalletAdjustment Value);
public sealed record ResolutionCommand(Guid BrandId, OrderResolution Value);
public sealed record BankDestinationCommand(string Number, string Holder, long ExpectedRevision, Guid CommandId)
{
    public override string ToString() => "BankDestinationCommand [redacted]";
}
public sealed record AdminIdentity(Guid Id);
public sealed class BrandAdministrationServices : IDisposable
{
    public BrandAdministrationServices(WriterRegistry writers, BrandContext context, CatalogStore catalog, ReadOnlySpan<byte> fingerprintKey)
    {
        var sessions = writers.ForBrand(context);
        Orders = new(sessions); Pricing = new(sessions);
        Deposits = new(sessions, new PaymentSettlementStore(sessions, catalog));
        Bank = new(sessions, fingerprintKey);
    }
    public OrderAdministrationStore Orders { get; }
    public PricingStore Pricing { get; }
    public ManualDepositService Deposits { get; }
    public BankDestinationService Bank { get; }
    public void Dispose() => Bank.Dispose();
}

public static class OrderAndWalletEndpoints
{
    public static void MapOrderAdministration(this IEndpointRouteBuilder endpoints, OrderAdministrationStore store, PricingStore pricing, ManualDepositService deposits,
        BankDestinationService bank, Func<Guid, BrandContext?> resolve)
        => MapCore(endpoints, store, _ => store, _ => pricing, _ => deposits, _ => bank, resolve);
    public static void MapScopedOrderAdministration(this IEndpointRouteBuilder endpoints, WriterRegistry writers, Func<BrandContext, BrandAdministrationServices> services)
        => MapCore(endpoints, new OrderAdministrationStore(writers.Shared), context => services(context).Orders, context => services(context).Pricing, context => services(context).Deposits, context => services(context).Bank, writers.Resolve);
    private static void MapCore(IEndpointRouteBuilder endpoints, OrderAdministrationStore search, Func<BrandContext, OrderAdministrationStore> orders,
        Func<BrandContext, PricingStore> pricing, Func<BrandContext, ManualDepositService> deposits, Func<BrandContext, BankDestinationService> bank, Func<Guid, BrandContext?> resolve)
    {
        endpoints.MapGet("/v1/admin/orders", (HttpContext http, Guid? brandId, Guid? memberId, Guid? orderId, Guid? after, int? limit) => AdminHttp.HandleAsync(http, async () =>
            Results.Json(await search.SearchAsync(http.GetRequiredAdministrator(), brandId, memberId, orderId, after, limit ?? 50, http.RequestAborted), OrderApiJsonContext.Default.AdminPageAdminOrder))).RequireAdministrator();
        endpoints.MapPost("/v1/admin/wallet-adjustments", (Func<HttpContext, Task<IResult>>)((HttpContext http) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, OrderApiJsonContext.Default.WalletCommand);
            var context = resolve(input.BrandId); if (context is null) return Results.NotFound();
            Guid entry = await orders(context).AdjustAsync(http.GetRequiredAdministrator(), context, input.Value, http.RequestAborted);
            return Results.Json(new AdminIdentity(entry), OrderApiJsonContext.Default.AdminIdentity);
        }))).RequireAdministrator();
        endpoints.MapPost("/v1/admin/orders/{id:guid}/resolution", (HttpContext http, Guid id) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, OrderApiJsonContext.Default.ResolutionCommand);
            var context = resolve(input.BrandId); if (context is null) return Results.NotFound();
            await orders(context).ResolveAsync(http.GetRequiredAdministrator(), context, id, input.Value, http.RequestAborted); return Results.NoContent();
        })).RequireAdministrator();
        endpoints.MapPost("/v1/admin/manual-deposits/{id:guid}/decision", (HttpContext http, Guid id) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, OrderApiJsonContext.Default.ReceiptDecision);
            var context = resolve(input.BrandId); if (context is null) return Results.NotFound();
            await deposits(context).DecideAsync(context, http.GetRequiredAdministrator(), id, input.Approve, input.ExpectedRevision, input.CommandId, input.Reason, http.RequestAborted);
            return Results.NoContent();
        })).RequireAdministrator();
        endpoints.MapPatch("/v1/admin/coupons/{id:guid}", (HttpContext http, Guid id) => AdminHttp.HandleAsync(http, async () =>
        {
            var input = await AdminHttp.ReadAsync(http, OrderApiJsonContext.Default.CouponEditCommand);
            var context = resolve(input.BrandId); if (context is null) return Results.NotFound();
            long revision = await pricing(context).EditAsync(context, http.GetRequiredAdministrator(), id, input.ExpectedRevision, input.Value, input.Reason, input.CommandId, http.RequestAborted);
            return Results.Json(new ConfigurationSaved(revision), BrandAdminJsonContext.Default.ConfigurationSaved);
        })).RequireAdministrator();
        endpoints.MapPut("/v1/admin/brands/{brand:guid}/bank-destination", (HttpContext http, Guid brand) => AdminHttp.HandleAsync(http, async () =>
        {
            var context = resolve(brand); if (context is null) return Results.NotFound();
            var input = await AdminHttp.ReadAsync(http, OrderApiJsonContext.Default.BankDestinationCommand);
            await bank(context).SaveAsync(context, http.GetRequiredAdministrator(), input.Number, input.Holder, input.ExpectedRevision, input.CommandId, http.RequestAborted); return Results.NoContent();
        })).RequireAdministrator();
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AdminPage<AdminOrder>))]
[JsonSerializable(typeof(WalletCommand))]
[JsonSerializable(typeof(ResolutionCommand))]
[JsonSerializable(typeof(ReceiptDecision))]
[JsonSerializable(typeof(CouponEditCommand))]
[JsonSerializable(typeof(BankDestinationCommand))]
[JsonSerializable(typeof(AdminIdentity))]
internal sealed partial class OrderApiJsonContext : JsonSerializerContext;
