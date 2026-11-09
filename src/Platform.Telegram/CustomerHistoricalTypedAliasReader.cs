using Platform.Core;
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
            var batch = await session.QueryManyAsync(new DbStatement("""
                SELECT m.legacy_id,o.created_at,o.quantity,
                    (CASE WHEN o.payment_status='not_required' THEN 0::numeric
                        WHEN o.currency='TOMAN' THEN o.unit_price_toman*o.quantity
                        ELSE o.unit_price_usdt*o.quantity END)::text,
                    CASE WHEN o.currency='TOMAN' THEN 'IRR' ELSE 'USD' END,
                    o.product_title_snapshot,
                    CASE WHEN o.payment_method='no_payment_required' THEN NULL ELSE o.payment_method END
                FROM shop.legacy_id_map m
                JOIN shop.orders o ON o.id=m.target_id
                JOIN shop.users u ON u.id=o.user_id
                JOIN shop.brands b ON b.id=u.brand_id
                WHERE m.source_table='orders.record' AND m.source_key_kind='primary' AND m.target_kind='order'
                    AND m.target_brand_id=$1 AND u.brand_id=$1 AND u.id=$2 AND b.bot_id=$3 AND b.active
                    AND u.blocked_at IS NULL AND u.banned_at IS NULL AND o.order_kind='product_purchase'
                    AND o.price_calculation_version='legacy-net-v1' AND o.review_code='credential_recovery'
                    AND o.fulfillment_status='requires_admin' AND o.payment_status IN ('paid','not_required')
                    AND o.currency IN ('USDT','TOMAN') AND o.product_id IS NULL AND o.release_authorized_at IS NULL
                    AND o.delivery_generation IS NULL AND o.resolution_kind IS NULL
                    AND ($4='' OR m.legacy_id COLLATE "C" > $4 COLLATE "C")
                ORDER BY m.legacy_id COLLATE "C" LIMIT 1000
                """, DbValue.Uuid(brand.BrandId), DbValue.Uuid(member), DbValue.Bigint(brand.BotId), DbValue.Text(cursor)),
                r => new CustomerHistoricalOrder(r.GetString(0), r.GetUtc(1), r.GetInt32(2), ExactDecimal.Parse(r.GetString(3)),
                    r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), false), 1000, ct);
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
