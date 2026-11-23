using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal sealed partial class EfIntegrity
{
    private async Task ValidateAuditEntitiesAsync(CancellationToken ct)
    {
        if (audits.Count == 0 && entities.Count == 0) return;
        var members = data.Set<User>();
        var ordersQuery = data.Set<Order>();
        var auditScope = data.Set<AuditEvent>().Where(audit => audits.Contains(audit.Id) || (audit.EntityId != null && entities.Contains(audit.EntityId.Value)));
        await RequireAsync(!await auditScope.AnyAsync(audit => audit.EntityKind != "platform" && !(
            (audit.EntityKind == "brand" && data.Set<Brand>().Any(value => value.Id == audit.EntityId && (audit.BrandId == null || value.Id == audit.BrandId))) ||
            (audit.EntityKind == "admin" && data.Set<Admin>().Any(value => value.Id == audit.EntityId)) ||
            (audit.EntityKind == "product" && data.Set<Product>().Any(value => value.Id == audit.EntityId)) ||
            (audit.EntityKind == "inventory_item" && data.Set<InventoryItem>().Any(value => value.Id == audit.EntityId)) ||
            (audit.EntityKind == "product_supplier_source" && data.Set<ProductSupplierSource>().Any(value => value.Id == audit.EntityId)) ||
            (audit.EntityKind == "brand_product" && data.Set<BrandProduct>().Any(value => value.ProductId == audit.EntityId && value.BrandId == audit.BrandId)) ||
            (audit.EntityKind == "user" && members.Any(value => value.Id == audit.EntityId && value.BrandId == audit.BrandId)) ||
            (audit.EntityKind == "wallet" && data.Set<Wallet>().Any(value => value.UserId == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "wallet_entry" && data.Set<WalletEntry>().Any(value => value.Id == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "order" && ordersQuery.Any(value => value.Id == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "payment" && data.Set<Payment>().Any(value => value.Id == audit.EntityId && members.Any(member => member.BrandId == audit.BrandId &&
                (member.Id == value.TopupUserId || (value.TopupUserId == null && ordersQuery.Any(order => order.Id == value.OrderId && order.UserId == member.Id)))))) ||
            (audit.EntityKind == "fulfillment" && data.Set<Fulfillment>().Any(value => value.Id == audit.EntityId && ordersQuery.Any(order => order.Id == value.OrderId && members.Any(member => member.Id == order.UserId && member.BrandId == audit.BrandId)))) ||
            (audit.EntityKind == "supplier_request" && data.Set<SupplierRequest>().Any(value => value.Id == audit.EntityId && ordersQuery.Any(order => order.Id == value.OrderId && members.Any(member => member.Id == order.UserId && member.BrandId == audit.BrandId)))) ||
            (audit.EntityKind == "campaign" && data.Set<Campaign>().Any(value => value.Id == audit.EntityId && value.BrandId == audit.BrandId)) ||
            (audit.EntityKind == "telegram_delivery" && data.Set<TelegramDelivery>().Any(value => value.Id == audit.EntityId && value.BrandId == audit.BrandId)) ||
            (audit.EntityKind == "discount" && data.Set<Discount>().Any(value => value.Id == audit.EntityId && value.BrandId == audit.BrandId)) ||
            (audit.EntityKind == "discount_revision" && data.Set<DiscountRevision>().Any(value => value.Id == audit.EntityId && data.Set<Discount>().Any(discount => discount.Id == value.DiscountId && discount.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "support_ticket" && data.Set<SupportTicket>().Any(value => value.Id == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "support_message" && data.Set<SupportMessage>().Any(value => value.Id == audit.EntityId && data.Set<SupportTicket>().Any(ticket => ticket.Id == value.TicketId && members.Any(member => member.Id == ticket.UserId && member.BrandId == audit.BrandId)))) ||
            (audit.EntityKind == "supplier_request_attempt" && data.Set<SupplierRequestAttempt>().Any(value => value.Id == audit.EntityId && data.Set<SupplierRequest>().Any(request => request.Id == value.SupplierRequestId && ordersQuery.Any(order => order.Id == request.OrderId && members.Any(member => member.Id == order.UserId && member.BrandId == audit.BrandId))))) ||
            (audit.EntityKind == "product_stock_subscription" && data.Set<ProductStockSubscription>().Any(value => value.Id == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId))) ||
            (audit.EntityKind == "bot_activity_event" && data.Set<BotActivityEvent>().Any(value => value.Id == audit.EntityId && members.Any(member => member.Id == value.UserId && member.BrandId == audit.BrandId)))
        ), ct), "audit_entity_scope");
    }
}
