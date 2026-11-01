using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed partial class PricingStore
{
    public async Task<Guid> IssuePaidSourceAsync(DbSession session, BrandContext context, Guid sourceOrder, CancellationToken ct = default)
    {
        try
        {
            var identity = await session.QuerySingleAsync(new DbStatement("""
                SELECT o.user_id,o.product_id FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND u.brand_id=$2
                """, DbValue.Uuid(sourceOrder), DbValue.Uuid(context.BrandId)), row => (Member: row.GetGuid(0), Product: row.GetGuid(1)), ct);
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 FOR UPDATE) u", DbValue.Uuid(identity.Member)), row => row.GetInt64(0), ct);
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.products WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(identity.Product)), row => row.GetInt64(0), ct);
            await LockBrandAsync(session, context, ct);
            var order = await session.QuerySingleAsync(new DbStatement("""
                SELECT quantity,order_kind,payment_status,payment_method,unit_price_usdt::text,discount_pct_snapshot::text,currency,unit_price_toman::text,
                    auto_price_toman_snapshot,fx_rate_snapshot::text,fx_source_snapshot,fx_fetched_at_snapshot FROM shop.orders WHERE id=$1 FOR UPDATE
                """, DbValue.Uuid(sourceOrder)), row => new SourceSale(row.GetInt32(0), row.GetString(1), row.GetString(2), row.GetString(3), ExactDecimal.Parse(row.GetString(4)),
                    row.IsDBNull(5) ? 0 : ExactDecimal.Parse(row.GetString(5)), row.GetString(6), row.IsDBNull(7) ? null : ExactDecimal.Parse(row.GetString(7)), row.GetBoolean(8),
                    row.IsDBNull(9) ? null : new FxRate(ExactDecimal.Parse(row.GetString(9)), row.GetString(10), row.GetUtc(11))), ct);
            if (order.Kind != "prepaid_units" || order.Status != "paid") Reject(session);
            Guid? prior = await session.QuerySingleAsync(new DbStatement("SELECT id FROM (SELECT 1) seed LEFT JOIN shop.discounts ON source_order_id=$1 AND kind='prepaid_entitlement'", DbValue.Uuid(sourceOrder)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
            if (prior is Guid existing) return existing;
            Money usd = PricingPolicy.DiscountTotal(order.Unit, order.Quantity, order.Percent, Currency.Usd);
            Money due = order.Currency == "TOMAN" ? PricingPolicy.TomanTotal(order.Unit, order.Toman, order.Quantity, order.Percent, order.Automatic, order.Rate) : usd;
            if (due.Amount <= 0) Reject(session);
            bool evidence = await session.QuerySingleAsync(new DbStatement("""
                SELECT CASE WHEN $3='wallet' THEN EXISTS(SELECT 1 FROM shop.wallet_entries WHERE order_id=$1 AND user_id=$2 AND movement_kind='purchase_debit' AND is_posting AND signed_amount=-$4)
                ELSE EXISTS(SELECT 1 FROM shop.payments WHERE order_id=$1 AND payment_method=$3 AND status='settled' AND confirmed_at IS NOT NULL AND amount_due=$4 AND amount_received>=amount_due) END
                """, DbValue.Uuid(sourceOrder), DbValue.Uuid(identity.Member), DbValue.Text(order.Method), DbValue.Numeric(due.Amount)), row => row.GetBoolean(0), ct);
            if (!evidence) Reject(session);
            Guid id = Guid.CreateVersion7(), audit = Guid.CreateVersion7();
            string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            string key = secrets.CurrentBrandKeyId(context);
            byte[] digest = Digest(context, key, code), plain = Encoding.UTF8.GetBytes(code), encrypted;
            try { encrypted = new BrandCipher(secrets).Encrypt(context, "discounts", id, "code_ciphertext", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,new_revision,correlation_id)
                VALUES ($1,'system',$2,'coupon_issue','discount',$3,'paid_source_entitlement',0,$4)
                """, DbValue.Uuid(audit), DbValue.Uuid(context.BrandId), DbValue.Uuid(id), DbValue.Uuid(sourceOrder)), ct);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discounts(id,brand_id,code_digest,code_ciphertext,code_key_id,display_prefix,issuance_batch_id,kind,percent_off,product_id,
                    eligible_user_id,source_order_id,max_total_uses)
                VALUES ($1,$2,$3,$4,$5,$6,$1,'prepaid_entitlement',100,$7,$8,$9,$10)
                """, DbValue.Uuid(id), DbValue.Uuid(context.BrandId), DbValue.Binary(digest), DbValue.Binary(encrypted), DbValue.Text(key), DbValue.Text(code[..3]),
                DbValue.Uuid(identity.Product), DbValue.Uuid(identity.Member), DbValue.Uuid(sourceOrder), DbValue.Bigint(order.Quantity)), ct);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,usage_adjustment_units,redeemed_count_at_revision,
                    held_units_at_revision,active,product_id,eligible_user_id)
                VALUES (gen_random_uuid(),$1,0,$2,100,$3,0,0,0,true,$4,$5)
                """, DbValue.Uuid(id), DbValue.Uuid(audit), DbValue.Bigint(order.Quantity), DbValue.Uuid(identity.Product), DbValue.Uuid(identity.Member)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='completed',revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(sourceOrder)), ct);
            Guid? held = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(sourceOrder)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
            if (held is Guid coupon) await TransitionClaimAsync(session, context, coupon, sourceOrder, true, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Coupon(context.BrandId, id), sourceOrder, DateTime.UtcNow, ct);
            return id;
        }
        catch { Poison(session); throw; }
    }
    private sealed record SourceSale(int Quantity, string Kind, string Status, string Method, decimal Unit, decimal Percent, string Currency, decimal? Toman, bool Automatic, FxRate? Rate);
}
