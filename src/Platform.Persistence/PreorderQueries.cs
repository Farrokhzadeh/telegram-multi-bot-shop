using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

internal sealed record PreorderHead(Guid Order, Guid Member, Guid Brand, int Quantity, string Kind, bool Blocked, bool Units);
internal static class PreorderQueries
{
    internal static Task<PreorderHead?> HeadAsync(EfSession data, Guid product, CancellationToken ct) =>
        (from order in data.Data.Set<Entities.Order>()
         join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id
         where order.ProductId == product && order.IsPreorder && (order.PaymentStatus == "paid" || order.PaymentStatus == "not_required") && (order.FulfillmentStatus == "waiting_stock" || order.FulfillmentStatus == "requires_admin")
         orderby order.PaymentConfirmedAt, order.Id
         select new PreorderHead(order.Id, user.Id, user.BrandId, order.Quantity, order.FulfillmentKindSnapshot, order.HoldAt != null || order.ReviewCode != null || order.ResolutionKind != null || user.BlockedAt != null || user.BannedAt != null, data.Data.Set<Entities.Fulfillment>().Any(value => value.OrderId == order.Id))).FirstOrDefaultAsync(ct);
}
