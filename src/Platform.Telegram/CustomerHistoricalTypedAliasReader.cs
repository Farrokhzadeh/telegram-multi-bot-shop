using Platform.Core;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public static class CustomerHistoricalTypedAliasReader
{
    public static async Task<IReadOnlyList<CustomerHistoricalOrder>> ReadAsync(DbSession session, BrandContext brand,
        Guid member, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(brand);
        if (member == Guid.Empty) throw new UnauthorizedAccessException("Historical order membership rejected.");
        var result = new List<CustomerHistoricalOrder>();
        string cursor = "";
        while (true)
        {
            var batch = await session.RunEfStepAsync(false, (data, token) => (from m in data.Data.Set<LegacyIdMap>() join o in data.Data.Set<Order>() on m.TargetId equals o.Id join u in data.Data.Set<User>() on o.UserId equals u.Id join b in data.Data.Set<Brand>() on u.BrandId equals b.Id where m.SourceTable == "orders.record" && m.SourceKeyKind == "primary" && m.TargetKind == "order" && m.TargetBrandId == brand.BrandId && u.BrandId == brand.BrandId && u.Id == member && b.BotId == brand.BotId && b.Active && u.BlockedAt == null && u.BannedAt == null && o.OrderKind == "product_purchase" && o.PriceCalculationVersion == "legacy-net-v1" && o.ReviewCode == "credential_recovery" && o.FulfillmentStatus == "requires_admin" && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && (o.Currency == "USDT" || o.Currency == "TOMAN") && o.ProductId == null && o.ReleaseAuthorizedAt == null && o.DeliveryGeneration == null && o.ResolutionKind == null && (cursor == "" || EF.Functions.Collate(m.LegacyId, "C").CompareTo(EF.Functions.Collate(cursor, "C")) > 0) orderby EF.Functions.Collate(m.LegacyId, "C") select new CustomerHistoricalOrder(m.LegacyId, o.CreatedAt, o.Quantity, o.PaymentStatus == "not_required" ? 0m : o.Currency == "TOMAN" ? o.UnitPriceToman!.Value * o.Quantity : o.UnitPriceUsdt * o.Quantity, o.Currency == "TOMAN" ? "IRR" : "USD", o.ProductTitleSnapshot, o.PaymentMethod == "no_payment_required" ? null : o.PaymentMethod, false)).Take(1000).ToListAsync(token), ct);
            if (result.Count + batch.Count > 10000) throw new UnauthorizedAccessException("Historical order count rejected.");
            result.AddRange(batch);
            if (batch.Count < 1000) break;
            string next = batch[^1].OriginalReference;
            if (string.CompareOrdinal(next, cursor) <= 0) throw new UnauthorizedAccessException("Historical order cursor rejected.");
            cursor = next;
        }
        return result.AsReadOnly();
    }
}
