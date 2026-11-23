using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal sealed partial class EfIntegrity
{
    private async Task ValidateCommerceAsync(CancellationToken ct)
    {
        if (orders.Count != 0)
        {
            var scoped = data.Set<Order>().Where(order => orders.Contains(order.Id));
            var items = from item in data.Set<InventoryItem>() join unit in data.Set<Fulfillment>() on item.AssignedFulfillmentId equals unit.Id select new { Item = item, Unit = unit };
            var requests = data.Set<SupplierRequest>();
            var units = data.Set<Fulfillment>();
            var uses = data.Set<DiscountUse>();
            await RequireAsync(!await scoped.AnyAsync(order => items.Count(value => value.Unit.OrderId == order.Id) +
                (requests.Where(value => value.OrderId == order.Id && value.Status != "failed" && value.Status != "resolved")
                    .Select(value => (long?)value.RequestedQuantity).Sum() ?? 0L) > order.Quantity, ct), "order_split_quantity");
            await RequireAsync(!await scoped.AnyAsync(order => order.OrderKind == "prepaid_units" && (units.Any(value => value.OrderId == order.Id) ||
                requests.Any(value => value.OrderId == order.Id && value.Status != "failed" && value.Status != "resolved")), ct), "order_prepaid_no_goods");
            await RequireAsync(!await scoped.AnyAsync(order => order.IsPreorder && items.Any(value => value.Unit.OrderId == order.Id) &&
                items.Count(value => value.Unit.OrderId == order.Id) != order.Quantity, ct), "order_preorder_whole_allocation");
            await RequireAsync(!await scoped.AnyAsync(order => order.IsPreorder && (order.PaymentStatus == "paid" || order.PaymentStatus == "not_required") &&
                order.ResolutionKind == null && order.FulfillmentStatus != "waiting_stock" && order.FulfillmentStatus != "requires_admin" &&
                !items.Any(value => value.Unit.OrderId == order.Id), ct), "order_preorder_fifo");
            await RequireAsync(!await units.AnyAsync(unit => orders.Contains(unit.OrderId) && unit.Kind == "local" &&
                (unit.Status == "acquired" || unit.Status == "ready" || unit.Status == "released") &&
                !data.Set<InventoryItem>().Any(value => value.AssignedFulfillmentId == unit.Id), ct), "order_unit_allocation");
            var released = scoped.Where(order => order.ReleaseAuthorizedAt != null || order.FulfillmentStatus == "ready" ||
                order.FulfillmentStatus == "delivering" || order.FulfillmentStatus == "completed");
            await RequireAsync(!await released.AnyAsync(order => (order.PaymentStatus != "paid" && order.PaymentStatus != "not_required") ||
                order.HoldAt != null || order.ReviewCode != null || (order.ResolutionKind != null && order.ResolutionKind != "manual_fulfilled"), ct), "order_release_state");
            await RequireAsync(!await released.AnyAsync(order =>
                (order.OrderKind == "prepaid_units" && (order.ReleaseAuthorizedAt != null || !data.Set<Discount>().Any(value => value.Kind == "prepaid_entitlement" && value.SourceOrderId == order.Id))) ||
                (order.OrderKind == "product_purchase" && (order.ReleaseAuthorizedAt == null || order.DeliveryGeneration == null ||
                    units.Count(value => value.OrderId == order.Id && (value.Status == "ready" || value.Status == "released")) != order.Quantity ||
                    requests.Any(value => value.OrderId == order.Id && value.Status != "succeeded" && value.Status != "resolved") ||
                    uses.Any(value => value.OrderId == order.Id && value.Status != "redeemed"))), ct), "order_complete_manifest");
            await RequireAsync(!await (from unit in units
                                       join order in scoped on unit.OrderId equals order.Id
                                       where unit.Status == "released" && (order.ReleaseAuthorizedAt == null || unit.ManifestGeneration != order.DeliveryGeneration)
                                       select unit.Id).AnyAsync(ct), "order_release_generation");
            await RequireAsync(!await scoped.AnyAsync(order => order.ResolutionWalletEntryId != null && !data.Set<WalletEntry>().Any(entry =>
                entry.Id == order.ResolutionWalletEntryId && entry.UserId == order.UserId && entry.OrderId == order.Id && entry.MovementKind == order.ResolutionKind &&
                entry.IsPosting && (order.ResolutionKind != "supplier_wallet_credit" || (entry.AdminId == order.ResolutionAdminId && entry.AuditEventId == order.ResolutionAuditEventId))), ct), "order_resolution_ledger");
            await RequireAsync(!await scoped.AnyAsync(order => (order.ResolutionKind == "local_preorder_cancellation" || order.ResolutionKind == "immediate_crypto_shortage_credit" ||
                order.ResolutionKind == "supplier_wallet_credit") && (order.ReleaseAuthorizedAt != null || uses.Any(value => value.OrderId == order.Id && value.Status == "held")), ct), "order_resolution_hold");
            await RequireAsync(!await (from use in uses
                                       join order in scoped on use.OrderId equals order.Id
                                       where use.Status == "redeemed" && !(order.ReleaseAuthorizedAt != null || (order.OrderKind == "prepaid_units" && order.FulfillmentStatus == "completed") ||
                                           (order.OrderKind == "product_purchase" && !order.IsPreorder && (order.PaymentStatus == "paid" || order.PaymentStatus == "not_required") &&
                                               (order.PaymentMethod == "wallet" || order.PaymentMethod == "no_payment_required") &&
                                               items.Count(value => value.Unit.OrderId == order.Id) == order.Quantity && units.Count(value => value.OrderId == order.Id) == order.Quantity &&
                                               !requests.Any(value => value.OrderId == order.Id) && items.Count(value => value.Unit.OrderId == order.Id && value.Unit.Kind == "local" &&
                                                   (value.Unit.Status == "acquired" || value.Unit.Status == "ready" || value.Unit.Status == "released") && value.Item.State == "allocated") == order.Quantity))
                                       select use.OrderId).AnyAsync(ct), "coupon_redeemed_obligation");
        }
        if (discounts.Count != 0)
        {
            var scoped = data.Set<Discount>().Where(discount => discounts.Contains(discount.Id));
            await RequireAsync(!await scoped.AnyAsync(discount => !data.Set<DiscountRevision>().Any(value => value.DiscountId == discount.Id && value.RevisionNo == discount.Revision), ct), "coupon_revision_required");
            await RequireAsync(!await scoped.AnyAsync(discount => discount.RedeemedCount != (data.Set<DiscountUse>().Where(value => value.DiscountId == discount.Id && value.Status == "redeemed")
                .Select(value => (long?)value.UnitsCovered).Sum() ?? 0L), ct), "coupon_redemption_count");
        }
    }
}
