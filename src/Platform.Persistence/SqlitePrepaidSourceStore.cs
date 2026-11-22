using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqlitePrepaidSourceStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<Guid> IssueAsync(BrandContext context, Guid source, CancellationToken ct = default) =>
        sessions.RunAsync(true, (_, tx, token) => IssueAsync(tx, context, source, token), ct);

    internal async Task<Guid> IssueAsync(SqliteTransaction tx, BrandContext context, Guid source,
        CancellationToken ct)
    {
        if (source == Guid.Empty) throw new ArgumentException("Prepaid source order is unavailable.");
        using var query = SqliteWallet.Command(tx, """
            SELECT o.user_id,o.product_id,o.quantity,o.order_kind,o.payment_status,o.payment_method,
                o.unit_price_usdt,o.discount_pct_snapshot,o.currency,o.unit_price_toman,
                o.auto_price_toman_snapshot,o.fx_rate_snapshot,o.fx_source_snapshot,o.fx_fetched_at_snapshot,
                o.price_calculation_version,o.pricing_rounding_step_snapshot
            FROM orders o JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
            WHERE o.id=$order AND b.id=$brand AND b.bot_id=$bot AND b.active=1
            """, ("$order", source), ("$brand", context.BrandId), ("$bot", context.BotId));
        Guid member, product;
        int quantity;
        string method, currency;
        decimal unit, percent;
        Money due;
        await using (var row = await query.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct) || row.GetString(3) != "prepaid_units" ||
                row.GetString(4) != "paid" || row.GetString(14) != PricingPolicy.Version ||
                ExactDecimal.Parse(row.GetString(15)) != PricingPolicy.TomanStep)
                throw new PricingRejectedException();
            member = row.GetGuid(0);
            product = row.GetGuid(1);
            quantity = row.GetInt32(2);
            method = row.GetString(5);
            unit = ExactDecimal.Parse(row.GetString(6));
            percent = row.IsDBNull(7) ? 0 : ExactDecimal.Parse(row.GetString(7));
            currency = row.GetString(8);
            decimal? toman = row.IsDBNull(9) ? null : ExactDecimal.Parse(row.GetString(9));
            bool automatic = row.GetBoolean(10);
            FxRate? rate = row.IsDBNull(11) ? null : new FxRate(ExactDecimal.Parse(row.GetString(11)),
                row.GetString(12), DateTime.ParseExact(row.GetString(13),
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal));
            due = currency == "TOMAN"
                ? PricingPolicy.TomanTotal(unit, toman, quantity, percent, automatic, rate)
                : PricingPolicy.DiscountTotal(unit, quantity, percent, Currency.Usd);
        }
        using (var prior = SqliteWallet.Command(tx, """
            SELECT id FROM discounts WHERE source_order_id=$order AND kind='prepaid_entitlement'
            """, ("$order", source)))
        {
            object? existing = await prior.ExecuteScalarAsync(ct);
            if (existing is string id) return Guid.Parse(id);
        }
        if (quantity <= 0 || due.Amount <= 0 || percent is < 0 or >= 100)
            throw new PricingRejectedException();
        using (var evidence = SqliteWallet.Command(tx, """
            SELECT CASE WHEN $method='wallet'
                THEN EXISTS(SELECT 1 FROM wallet_entries e WHERE e.order_id=$order AND e.user_id=$member
                    AND e.movement_kind='purchase_debit' AND e.is_posting=1 AND
                    e.signed_amount=akanti_decimal_add('0',$debit))
                ELSE EXISTS(SELECT 1 FROM payments p WHERE p.order_id=$order AND p.payment_method=$method
                    AND p.status='settled' AND p.confirmed_at IS NOT NULL
                    AND p.amount_due=$due AND akanti_decimal_compare(p.amount_received,p.amount_due)>=0)
                END
            """, ("$method", method), ("$order", source), ("$member", member),
            ("$debit", ExactDecimal.Format(-due.Amount)), ("$due", ExactDecimal.Format(due.Amount))))
            if ((long)(await evidence.ExecuteScalarAsync(ct))! != 1)
                throw new PricingRejectedException();
        Guid coupon = Guid.CreateVersion7(), audit = Guid.CreateVersion7(), revision = Guid.CreateVersion7();
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string key = secrets.CurrentBrandKeyId(context);
        byte[] bytes = Encoding.UTF8.GetBytes("coupon-lookup-v1:" + context.BrandId.ToString("D") + ":" + code);
        byte[] digest;
        try { digest = secrets.UseBrandKey(context, key, secret => HMACSHA256.HashData(secret, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        byte[] plaintext = Encoding.UTF8.GetBytes(code);
        byte[] encrypted;
        try { encrypted = new BrandCipher(secrets).Encrypt(context, "discounts", coupon, "code_ciphertext", plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        using var insert = SqliteWallet.Command(tx, """
            INSERT INTO audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,
                new_revision,correlation_id)
            VALUES ($audit,'system',$brand,'coupon_issue','discount',$coupon,'paid_source_entitlement',0,$order);
            INSERT INTO discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,
                issuance_batch_id,kind,percent_off,product_id,eligible_user_id,source_order_id,max_total_uses)
            VALUES ($coupon,$brand,$digest,$encrypted,$key,$prefix,$coupon,'prepaid_entitlement',
                '100',$product,$member,$order,$units);
            INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                max_total_uses,usage_adjustment_units,redeemed_count_at_revision,
                held_units_at_revision,active,product_id,eligible_user_id)
            VALUES ($revision,$coupon,0,$audit,'100',$units,0,0,0,1,$product,$member);
            UPDATE orders SET fulfillment_status='completed',revision=revision+1,updated_at=$now
            WHERE id=$order AND order_kind='prepaid_units' AND payment_status='paid';
            """, ("$audit", audit), ("$brand", context.BrandId), ("$coupon", coupon),
            ("$order", source), ("$digest", digest), ("$encrypted", encrypted),
            ("$key", key), ("$prefix", code[..3]), ("$product", product),
            ("$member", member), ("$units", quantity), ("$revision", revision),
            ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
        if (await insert.ExecuteNonQueryAsync(ct) != 4)
            throw new InvalidOperationException("Prepaid entitlement was not persisted.");
        await SqliteDiscountClaimStore.TransitionAsync(tx, context, source, true, ct);
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Coupon(context.BrandId, coupon),
            source, DateTime.UtcNow, ct);
        return coupon;
    }
}
