using Platform.Core;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public sealed record PaymentStatusView(Guid Id, Guid? OrderId, string Method, string State, string QuoteCurrency, string AmountDue, string? AmountReceived,
    string? OrderPaymentState, string? FulfillmentState, string? ReviewCode, string? WalletMovement, string? WalletCreditUsd, string? CouponState, bool ReturnedCouponExpired);

public static class PaymentReadStore
{
    public static async Task<PaymentStatusView?> ForAdminAsync(DbSession session, AuthenticatedAdmin actor, Guid brand, Guid payment, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, null, ct);
        return await ReadAsync(session, brand, null, payment, ct);
    }
    public static async Task<PaymentStatusView?> ForCustomerAsync(DbSession session, BrandContext context, long actor, Guid payment, CancellationToken ct = default)
    {
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        return await ReadAsync(session, context.BrandId, member.Id, payment, ct);
    }
    private static async Task<PaymentStatusView?> ReadAsync(DbSession session, Guid brand, Guid? member, Guid payment, CancellationToken ct)
    {
        var row = await session.RunEfStepAsync(false, (data, token) =>
            (from value in data.Data.Set<Entities.Payment>()
             join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
             from order in orders.DefaultIfEmpty()
             join user in data.Data.Set<Entities.User>() on (value.TopupUserId ?? (Guid?)order.UserId) equals user.Id
             join entry in data.Data.Set<Entities.WalletEntry>() on value.CompensatingWalletEntryId equals entry.Id into entries
             from entry in entries.Where(entry => entry.UserId == user.Id && entry.PaymentId == value.Id).DefaultIfEmpty()
             join claim in data.Data.Set<Entities.DiscountUse>() on order.Id equals claim.OrderId into claims
             from claim in claims.DefaultIfEmpty()
             join discount in data.Data.Set<Entities.Discount>() on claim.DiscountId equals discount.Id into discounts
             from discount in discounts.DefaultIfEmpty()
             where value.Id == payment && user.BrandId == brand && (member == null || user.Id == member)
             select new
             {
                 value.Id,
                 value.OrderId,
                 Method = value.PaymentMethod,
                 value.Status,
                 value.QuoteCurrency,
                 value.AmountDue,
                 value.AmountReceived,
                 OrderPayment = order == null ? null : order.PaymentStatus,
                 Fulfillment = order == null ? null : order.FulfillmentStatus,
                 Review = order == null ? null : order.ReviewCode,
                 Movement = entry == null ? null : entry.MovementKind,
                 Credit = entry == null ? (decimal?)null : entry.SignedAmount,
                 Coupon = claim == null ? null : claim.Status,
                 Expired = claim != null && claim.Status == "released" && discount.Kind == "promotion" && discount.EndsAt <= EfValues.UtcNow()
             }).SingleOrDefaultAsync(token), ct);
        return row is null ? null : new(row.Id, row.OrderId, row.Method, State(row.Method, row.Status, row.OrderPayment, row.Fulfillment), row.QuoteCurrency, ExactDecimal.Format(row.AmountDue), row.AmountReceived is decimal received ? ExactDecimal.Format(received) : null, row.OrderPayment, row.Fulfillment, row.Review, row.Movement, row.Credit is decimal credit ? ExactDecimal.Format(credit) : null, row.Coupon, row.Expired);
    }
    private static string State(string method, string payment, string? order, string? fulfillment) => method == "bank" && payment == "rejected" ? "denied_unverified" :
        payment == "settled" && order == "paid" && fulfillment is "waiting_stock" or "requires_admin" ? "paid_unfulfilled" : payment;
}
