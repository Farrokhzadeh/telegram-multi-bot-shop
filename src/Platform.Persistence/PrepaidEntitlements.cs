using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal static class PrepaidEntitlements
{
    internal sealed record Sale(Guid Member, Guid? Product, int Quantity, string Kind, string Status, string Method, decimal Unit, decimal Percent, string Currency, decimal? Toman, bool Automatic, decimal? Fx, string? FxSource, DateTime? Fetched, string Version, decimal Step);
    internal static async Task<Sale?> ReadAsync(EfSession data, BrandContext context, Guid source, CancellationToken ct)
    {
        await data.LockOrderAsync(source, ct);
        return await (from order in data.Data.Set<Order>()
                      join member in data.Data.Set<User>() on order.UserId equals member.Id
                      join brand in data.Data.Set<Entities.Brand>() on member.BrandId equals brand.Id
                      where order.Id == source && brand.Id == context.BrandId && brand.BotId == context.BotId && brand.Active
                      select new Sale(member.Id, order.ProductId, order.Quantity, order.OrderKind, order.PaymentStatus, order.PaymentMethod, order.UnitPriceUsdt, order.DiscountPctSnapshot ?? 0m, order.Currency, order.UnitPriceToman, order.AutoPriceTomanSnapshot, order.FxRateSnapshot, order.FxSourceSnapshot, order.FxFetchedAtSnapshot, order.PriceCalculationVersion, order.PricingRoundingStepSnapshot)).SingleOrDefaultAsync(ct);
    }
    internal static Task<Guid?> PriorAsync(EfSession data, Guid source, CancellationToken ct) => data.Data.Set<Discount>().Where(value => value.SourceOrderId == source && value.Kind == "prepaid_entitlement").Select(value => (Guid?)value.Id).SingleOrDefaultAsync(ct);
    internal static Money Due(Sale sale) => sale.Currency == "TOMAN" ? PricingPolicy.TomanTotal(sale.Unit, sale.Toman, sale.Quantity, sale.Percent, sale.Automatic, sale.Fx is decimal rate ? new FxRate(rate, sale.FxSource!, sale.Fetched!.Value) : null) : PricingPolicy.DiscountTotal(sale.Unit, sale.Quantity, sale.Percent, Currency.Usd);
    internal static Task<bool> EvidenceAsync(EfSession data, Sale sale, Guid source, decimal due, CancellationToken ct) => sale.Method == "wallet"
        ? data.Data.Set<WalletEntry>().AnyAsync(value => value.OrderId == source && value.UserId == sale.Member && value.MovementKind == "purchase_debit" && value.IsPosting && value.SignedAmount == -due, ct)
        : data.Data.Set<Payment>().AnyAsync(value => value.OrderId == source && value.PaymentMethod == sale.Method && value.Status == "settled" && value.ConfirmedAt != null && value.AmountDue == due && value.AmountReceived >= value.AmountDue, ct);
    internal static async Task<Guid> CreateAsync(EfSession data, BrandContext context, Guid source, Sale sale, CancellationToken ct)
    {
        await data.RequireOrderCommandAsync(context, source, ct);
        Guid coupon = Guid.CreateVersion7(), audit = Guid.CreateVersion7();
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        byte[] lookup = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + context.BrandId.ToString("D") + ":" + code), digest;
        try { digest = SHA256.HashData(lookup); }
        finally { CryptographicOperations.ZeroMemory(lookup); }
        byte[] plain = Encoding.UTF8.GetBytes(code);
        data.Data.Add(new AuditEvent { Id = audit, ActorKind = "system", BrandId = context.BrandId, Action = "coupon_issue", EntityKind = "discount", EntityId = coupon, Reason = "paid_source_entitlement", NewRevision = 0, CorrelationId = source });
        await data.FlushAsync(ct, false);
        data.Data.Add(new Discount { Id = coupon, BrandId = context.BrandId, CodeDigest = digest, CodeContent = plain, DisplayPrefix = code[..3], IssuanceBatchId = coupon, Kind = "prepaid_entitlement", PercentOff = 100m, ProductId = sale.Product, EligibleUserId = sale.Member, SourceOrderId = source, MaxTotalUses = sale.Quantity });
        await data.FlushAsync(ct, false);
        data.Data.Add(new DiscountRevision { Id = Guid.CreateVersion7(), DiscountId = coupon, RevisionNo = 0, AuditEventId = audit, PercentOff = 100m, MaxTotalUses = sale.Quantity, Active = true, ProductId = sale.Product, EligibleUserId = sale.Member });
        var order = await data.Data.Set<Order>().SingleAsync(value => value.Id == source && value.UserId == sale.Member && value.ProductId == sale.Product && value.OrderKind == "prepaid_units" && value.PaymentStatus == "paid", ct);
        order.FulfillmentStatus = "completed"; order.Revision = checked(order.Revision + 1); order.UpdatedAt = await data.BrandClockAsync(context.BrandId, ct);
        return coupon;
    }
}
