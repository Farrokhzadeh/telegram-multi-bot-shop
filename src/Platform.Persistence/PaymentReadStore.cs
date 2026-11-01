using Platform.Core;
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
    private static Task<PaymentStatusView?> ReadAsync(DbSession session, Guid brand, Guid? member, Guid payment, CancellationToken ct) =>
        session.QuerySingleAsync<PaymentStatusView?>(new DbStatement("""
            SELECT p.id,p.order_id,p.payment_method,p.status,p.quote_currency,p.amount_due::text,p.amount_received::text,o.payment_status,o.fulfillment_status,o.review_code,
                w.movement_kind,w.signed_amount::text,c.status,c.status='released' AND d.kind='promotion' AND d.ends_at<=clock_timestamp()
            FROM (SELECT 1) seed LEFT JOIN (shop.payments p LEFT JOIN shop.orders o ON o.id=p.order_id JOIN shop.users u ON u.id=COALESCE(p.topup_user_id,o.user_id)
                LEFT JOIN shop.wallet_entries w ON w.id=p.compensating_wallet_entry_id AND w.user_id=u.id AND w.payment_id=p.id
                LEFT JOIN shop.discount_uses c ON c.order_id=o.id LEFT JOIN shop.discounts d ON d.id=c.discount_id)
                ON p.id=$1 AND u.brand_id=$2 AND ($3 IS NULL OR u.id=$3)
            """, DbValue.Uuid(payment), DbValue.Uuid(brand), member is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid)), r => r.IsDBNull(0) ? null :
            new PaymentStatusView(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), State(r.GetString(2), r.GetString(3), Text(r, 7), Text(r, 8)),
                r.GetString(4), ExactDecimal.Format(ExactDecimal.Parse(r.GetString(5))), r.IsDBNull(6) ? null : ExactDecimal.Format(ExactDecimal.Parse(r.GetString(6))), Text(r, 7), Text(r, 8), Text(r, 9),
                Text(r, 10), r.IsDBNull(11) ? null : ExactDecimal.Format(ExactDecimal.Parse(r.GetString(11))), Text(r, 12), !r.IsDBNull(13) && r.GetBoolean(13)), ct);
    private static string State(string method, string payment, string? order, string? fulfillment) => method == "bank" && payment == "rejected" ? "denied_unverified" :
        payment == "settled" && order == "paid" && fulfillment is "waiting_stock" or "requires_admin" ? "paid_unfulfilled" : payment;
    private static string? Text(DbRow r, int column) => r.IsDBNull(column) ? null : r.GetString(column);
}
