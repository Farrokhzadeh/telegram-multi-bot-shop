using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public static class SqliteDiscountClaimStore
{
    internal static async Task HoldAsync(SqliteTransaction tx, BrandContext context, Guid member,
        Guid order, Guid product, CheckoutQuote quote, CancellationToken ct)
    {
        QuoteTerms terms = quote.Terms;
        if (terms.DiscountId is not Guid coupon || terms.DiscountRevisionId is not Guid revision ||
            terms.DiscountPercent is not decimal percent || terms.DiscountUnits != terms.Quantity ||
            percent is <= 0 or >= 100 || terms.Quantity <= 0)
            throw new PricingRejectedException();
        using var lookup = SqliteWallet.Command(tx, """
            SELECT r.id,d.percent_off,d.active,d.starts_at,d.ends_at,d.product_id,d.eligible_user_id,
                d.kind,d.source_order_id,o.quantity,o.discount_pct_snapshot,o.discount_units_snapshot,
                o.price_calculation_version,o.pricing_rounding_step_snapshot,o.product_id,o.user_id
            FROM discounts d JOIN discount_revisions r ON r.discount_id=d.id AND r.revision_no=d.revision
            JOIN orders o ON o.id=$order
            JOIN users u ON u.id=o.user_id
            WHERE d.id=$coupon AND d.brand_id=$brand AND u.brand_id=$brand AND u.id=$member
            """, ("$coupon", coupon), ("$order", order), ("$brand", context.BrandId), ("$member", member));
        await using var row = await lookup.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.GetGuid(0) != revision ||
            ExactDecimal.Parse(row.GetString(1)) != percent || !row.GetBoolean(2) ||
            row.GetInt32(9) != terms.Quantity || row.IsDBNull(10) ||
            ExactDecimal.Parse(row.GetString(10)) != percent || row.IsDBNull(11) ||
            row.GetInt32(11) != terms.Quantity || row.GetString(12) != terms.Version ||
            ExactDecimal.Parse(row.GetString(13)) != terms.RoundingStep ||
            row.GetGuid(14) != product || row.GetGuid(15) != member ||
            !row.IsDBNull(5) && row.GetGuid(5) != product ||
            !row.IsDBNull(6) && row.GetGuid(6) != member)
            throw new PricingRejectedException();
        string now = SqliteSchema.UtcText(DateTime.UtcNow);
        if (!row.IsDBNull(3) && string.CompareOrdinal(row.GetString(3), now) > 0 ||
            !row.IsDBNull(4) && string.CompareOrdinal(row.GetString(4), now) <= 0)
            throw new PricingRejectedException();
        if (row.GetString(7) == "prepaid_entitlement")
        {
            if (row.IsDBNull(8)) throw new PricingRejectedException();
            Guid source = row.GetGuid(8);
            await row.CloseAsync();
            using var payment = SqliteWallet.Command(tx, """
                SELECT 1 FROM orders WHERE id=$source AND user_id=$member AND product_id=$product
                    AND order_kind='prepaid_units' AND payment_status='paid' LIMIT 1
                """, ("$source", source), ("$member", member), ("$product", product));
            if (await payment.ExecuteScalarAsync(ct) is null) throw new PricingRejectedException();
        }
        else await row.CloseAsync();
        using var quota = SqliteWallet.Command(tx, """
            SELECT d.max_total_uses,d.max_uses_per_user,d.redeemed_count,d.usage_adjustment_units,
                (SELECT coalesce(sum(x.units_covered),0) FROM discount_uses x
                    WHERE x.discount_id=$coupon AND x.status='held'),
                (SELECT coalesce(sum(x.units_covered),0) FROM discount_uses x JOIN orders o ON o.id=x.order_id
                    WHERE x.discount_id=$coupon AND o.user_id=$member AND x.status IN ('held','redeemed'))
            FROM discounts d WHERE d.id=$coupon
            """, ("$coupon", coupon), ("$member", member));
        await using var cap = await quota.ExecuteReaderAsync(ct);
        if (!await cap.ReadAsync(ct)) throw new PricingRejectedException();
        long redeemed = cap.GetInt64(2), adjustment = cap.GetInt64(3), held = cap.GetInt64(4),
            used = cap.GetInt64(5);
        if (!cap.IsDBNull(0) &&
            (System.Numerics.BigInteger)cap.GetInt64(0) + adjustment - redeemed - held < terms.Quantity ||
            !cap.IsDBNull(1) && (System.Numerics.BigInteger)cap.GetInt64(1) - used < terms.Quantity)
            throw new PricingRejectedException();
        await cap.CloseAsync();
        Money before = terms.Currency == Currency.Toman
            ? PricingPolicy.TomanTotal(terms.UnitUsdt, terms.UnitToman, terms.Quantity, 0,
                terms.AutoToman, terms.Rate)
            : PricingPolicy.DiscountTotal(terms.UnitUsdt, terms.Quantity, 0, Currency.Usd);
        Money after = terms.Currency == Currency.Toman
            ? PricingPolicy.TomanTotal(terms.UnitUsdt, terms.UnitToman, terms.Quantity, percent,
                terms.AutoToman, terms.Rate)
            : PricingPolicy.DiscountTotal(terms.UnitUsdt, terms.Quantity, percent, Currency.Usd);
        Money saved = before.Subtract(after);
        using var insert = SqliteWallet.Command(tx, """
            INSERT INTO discount_uses(discount_id,order_id,discount_revision_id,units_covered,
                saved_amount,saved_currency,status)
            VALUES ($coupon,$order,$revision,$units,$saved,$currency,'held')
            """, ("$coupon", coupon), ("$order", order), ("$revision", revision),
            ("$units", terms.Quantity), ("$saved", ExactDecimal.Format(saved.Amount)),
            ("$currency", saved.Currency.ToCode()));
        if (await insert.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Discount hold was not persisted.");
    }

    internal static async Task<bool> TransitionAsync(SqliteTransaction tx, BrandContext context, Guid order,
        bool redeem, CancellationToken ct)
    {
        using var lookup = SqliteWallet.Command(tx, """
            SELECT c.discount_id,c.units_covered,c.status,o.payment_status,o.order_kind,
                o.fulfillment_status,o.resolution_kind,o.release_authorized_at IS NOT NULL,
                o.is_preorder,o.payment_method,o.quantity,
                (SELECT count(*) FROM fulfillments f JOIN inventory_items i ON i.assigned_fulfillment_id=f.id
                    WHERE f.order_id=o.id AND f.kind='local' AND f.status IN ('acquired','ready','released')
                        AND i.state='allocated'),
                (SELECT count(*) FROM fulfillments WHERE order_id=o.id),
                EXISTS(SELECT 1 FROM supplier_requests WHERE order_id=o.id)
            FROM discount_uses c JOIN orders o ON o.id=c.order_id
                JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
            WHERE c.order_id=$order AND b.id=$brand AND b.bot_id=$bot AND b.active=1
            """, ("$order", order), ("$brand", context.BrandId), ("$bot", context.BotId));
        await using var row = await lookup.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return false;
        Guid coupon = row.GetGuid(0);
        long units = row.GetInt64(1);
        string status = row.GetString(2), payment = row.GetString(3), kind = row.GetString(4),
            fulfillment = row.GetString(5);
        string? resolution = row.IsDBNull(6) ? null : row.GetString(6);
        bool released = row.GetBoolean(7), preorder = row.GetBoolean(8);
        string method = row.GetString(9);
        long quantity = row.GetInt64(10), allocated = row.GetInt64(11), total = row.GetInt64(12);
        bool remote = row.GetBoolean(13);
        await row.CloseAsync();
        string target = redeem ? "redeemed" : "released";
        if (status == target) return false;
        bool immediate = kind == "product_purchase" && !preorder &&
            method is ("wallet" or "no_payment_required") && allocated == quantity &&
            total == quantity && !remote;
        bool permitted = redeem
            ? payment is ("paid" or "not_required") && (released || immediate ||
                kind == "prepaid_units" && fulfillment == "completed")
            : payment is ("expired" or "cancelled") ||
                resolution is ("local_preorder_cancellation" or "immediate_crypto_shortage_credit" or "supplier_wallet_credit");
        if (status != "held" || !permitted)
            throw new UnauthorizedAccessException("Discount claim transition is unavailable.");
        using (var update = SqliteWallet.Command(tx, """
            UPDATE discount_uses SET status=$status,transitioned_at=$now
            WHERE order_id=$order AND discount_id=$coupon AND status='held'
            """, ("$status", target), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
            ("$order", order), ("$coupon", coupon)))
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Discount claim was superseded.");
        if (redeem)
        {
            using var count = SqliteWallet.Command(tx, """
                UPDATE discounts SET redeemed_count=redeemed_count+$units WHERE id=$coupon AND brand_id=$brand
                """, ("$units", units), ("$coupon", coupon), ("$brand", context.BrandId));
            if (await count.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Discount redemption was superseded.");
        }
        return true;
    }
}
