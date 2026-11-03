using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class PaymentDeadlineQualification
{
    public static async Task SchemaAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 0, ct: ct);
        await using (var read = await f.Sessions.BeginAsync(ct))
        {
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_index i JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=i.indkey[0] WHERE i.indrelid='shop.orders'::regclass AND i.indisunique AND i.indnkeyatts=1 AND a.attname='user_id' AND pg_get_expr(i.indpred,i.indrelid) LIKE '%awaiting_payment%'"), r => r.GetInt64(0), ct) == 1, "single membership pending index");
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND ((table_name IN ('orders','payments') AND column_name IN ('brand_id','telegram_id')) OR (table_name IN ('wallets','wallet_entries') AND column_name='currency') OR data_type IN ('json','jsonb','ARRAY','real','double precision'))"), r => r.GetInt64(0), ct) == 0, "lean typed schema");
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND ((table_name='orders' AND column_name='payment_due_at' AND data_type='timestamp with time zone') OR (table_name='payments' AND column_name IN ('amount_due','amount_received','quote_currency','first_payment_observed_at')) OR (table_name='discount_uses' AND column_name='status') OR (table_name='telegram_deliveries' AND column_name IN ('action','target_message_id')))"), r => r.GetInt64(0), ct) == 8, "canonical deadline columns");
        }
        var first = await SeedAsync(f, 0, false, ct: ct);
        await SeedAsync(f, 1, false, ct: ct); await SeedAsync(f, 3, false, ct: ct);
        bool duplicate = false;
        try { await SeedAsync(f, 0, false, ct: ct); } catch (DbOperationException e) when (e.Failure.SqlState == "23505") { duplicate = true; }
        Require(duplicate, "same membership rejects second pending");
        bool immutable = false;
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_due_at=payment_due_at+INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(first.Order)), ct); } catch (DbOperationException) { immutable = true; } }
        Require(immutable && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 3, "immutable original deadline and distinct memberships");
    }
    public static async Task ExpiryAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(ct))
        { await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), await f.AdminAsync(ct), new(coupon, "PUBLIC-DEADLINE", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 1), Guid.NewGuid(), ct); await issue.CommitAsync(ct); }
        var old = await SeedAsync(f, 0, true, coupon, ct: ct);
        var lease = await ClaimAsync(f, ct);
        Require(await new PaymentExpiryService(f.Sessions, f.Secrets).RunAsync(f.Context(0), lease, ct) == PaymentExpiryOutcome.Expired, "expired original");
        Require(await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='expired' AND resolution_kind='unpaid_expiry'", ct) == 1, "terminal expiry");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events e JOIN shop.orders o ON o.id=e.order_id WHERE e.action_code='checkout_expired' AND e.source_kind='system' AND e.outcome_code='cancelled' AND e.occurred_at=o.resolution_at", ct) == 1, "actual unpaid expiry appends original-time typed state activity");
        Require(await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='released' AND units_covered=1", ct) == 1 && await f.CountAsync("SELECT redeemed_count FROM shop.discounts", ct) == 0, "whole held claim returned");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.fulfillments", ct) == 0, "no financial or stock effects");
        Require(await f.CountAsync("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_status' AND dedupe_key LIKE 'instruction-expired:%'", ct) == 1, "durable expired notice");
        bool fenced = false;
        try { await new PaymentExpiryService(new(f.Database.Data), f.Secrets).RunAsync(f.Context(0), lease, ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.LeaseLost) { fenced = true; }
        Require(fenced, "completed expiry cannot replay writes");
        var payment = new PaymentSettlementFixture(f);
        await payment.CaptureAsync(old.Payment, old.Origin, old.Due, ct: ct);
        var result = await payment.Store.SettleAsync(f.Context(0), old.Payment, PaymentSettlementFixture.Lookup, ct);
        Require(result.Route == ExternalSettlementRoute.LateCredit, "late original invoice recovery");
        Require((await payment.Store.SettleAsync(f.Context(0), old.Payment, (_, _) => throw new InvalidOperationException(), ct)).Outcome == PaymentSettlementOutcome.Replayed, "late credit once");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='late_crypto_credit' AND signed_amount=5", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='expired' AND resolution_kind='unpaid_expiry'", ct) == 1, "no revival or lost money");
        await using var read = await f.Sessions.BeginAsync(ct);
        Require(await read.QuerySingleAsync(new DbStatement("SELECT o.payment_due_at=$1 AND p.provider_payment_ref=$2 AND p.amount_due=$3 FROM shop.orders o JOIN shop.payments p ON p.order_id=o.id WHERE o.id=$4", DbValue.Utc(old.Deadline), DbValue.Text(PaymentSettlementFixture.Provider(old.Payment)), DbValue.Numeric(old.Due), DbValue.Uuid(old.Order)), r => r.GetBoolean(0), ct), "original quote deadline and provider identity");
    }
    public static async Task ProtectionAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var crypto = await SeedAsync(f, 0, true, proof: true, ct: ct);
        var review = await SeedAsync(f, 1, true, proof: true, state: "under_review", ct: ct);
        await SeedAsync(f, 2, false, ct: ct);
        for (int i = 0; i < 2; i++)
        {
            var lease = await ClaimAsync(f, ct); var context = f.Contexts.Single(c => c.BrandId == lease.Intent.BrandId);
            Require(await new PaymentExpiryService(f.Sessions, f.Secrets).RunAsync(context, lease, ct) == PaymentExpiryOutcome.Protected, "existing evidence remains protected");
        }
        await using (var due = await f.Sessions.BeginAsync(ct))
        { await due.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp() WHERE job_kind='expire_order_payment' AND status='pending'"), ct); await due.CommitAsync(ct); }
        Require(await new PaymentExpiryService(f.Sessions, f.Secrets).RunAsync(f.Context(2), await ClaimAsync(f, ct), ct) == PaymentExpiryOutcome.NotDue, "early expiry defers");
        Require(await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status IN ('confirmation_pending','under_review','awaiting_payment') AND resolution_kind IS NULL", ct) == 3 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0, "protected obligations unchanged");
    }
    public static async Task<(Guid Order, Guid Payment, string Origin, decimal Due, DateTime Deadline)> SeedAsync(CheckoutFixture f, int user, bool past, Guid? coupon = null, bool proof = false, string? state = null, CancellationToken ct = default, bool preorder = false, TimeSpan? age = null)
    {
        Guid order = Guid.NewGuid(), payment = Guid.NewGuid(); string origin = "order:" + order.ToString("D"); decimal due = coupon is null ? 10 : 5;
        await using var seed = await f.Sessions.BeginAsync(ct);
        DateTime created = await seed.QuerySingleAsync(new DbStatement("SELECT clock_timestamp()"), r => r.GetUtc(0), ct); if (past) created = created.Subtract(age ?? TimeSpan.FromMinutes(31));
        DateTime deadline = created.AddMinutes(30);
        await seed.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                pricing_rounding_step_snapshot,price_calculation_version,discount_pct_snapshot,discount_units_snapshot,brand_public_name_snapshot,product_title_snapshot,
                payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,created_at,payment_due_at)
            VALUES ($1,$2,$3,1,'product_purchase',$4,'USDT','crypto',10,false,5000,$5,$6,$7,'public brand','public title','public terms','local',true,$10,$8,$9)
            """, DbValue.Uuid(order), DbValue.Uuid(f.Users[user]), DbValue.Uuid(f.Product), DbValue.Text(state ?? (proof ? "confirmation_pending" : "awaiting_payment")), DbValue.Text(PricingPolicy.Version),
            coupon is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric) : DbValue.Numeric(50), coupon is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Integer) : DbValue.Integer(1), DbValue.Utc(created), DbValue.Utc(deadline), DbValue.Boolean(preorder)), ct);
        await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.payments(id,order_id,payment_method,quote_currency,amount_due,idempotency_ref,network_asset,created_at) VALUES ($1,$2,'crypto','USD',$3,$4,'USDTTRC20',$5)", DbValue.Uuid(payment), DbValue.Uuid(order), DbValue.Numeric(due), DbValue.Text(origin), DbValue.Utc(created)), ct);
        await new PaymentEvidenceStore(f.Sessions, f.Secrets).BindInvoiceAsync(seed, f.Context(user), CheckoutFixture.Actor(user), payment, PaymentSettlementFixture.Provider(payment), 10.4m, ct);
        if (coupon is Guid id) await new PricingStore(f.Sessions, f.Secrets).HoldAsync(seed, f.Context(user), CheckoutFixture.Actor(user), id, order, ct);
        if (proof) await seed.ExecuteAsync(new DbStatement("UPDATE shop.payments SET first_payment_observed_at=$1,status='confirming' WHERE id=$2", DbValue.Utc(deadline.AddSeconds(-1)), DbValue.Uuid(payment)), ct);
        await OutboxStore.EnqueueAsync(seed, JobIntent.Order(JobKind.ExpireOrderPayment, f.Context(user).BrandId, order), order, deadline, ct);
        await seed.CommitAsync(ct); return (order, payment, origin, due, deadline);
    }
    private static async Task<JobLease> ClaimAsync(CheckoutFixture f, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); var lease = await OutboxStore.ClaimAsync(session, JobKind.ExpireOrderPayment, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct); await session.CommitAsync(ct); return lease ?? throw new InvalidOperationException("Expiry fixture work missing."); }
    private static void Require(bool condition, string stage) { if (!condition) throw new InvalidOperationException("Deadline qualification failed: " + stage + "."); }
}
