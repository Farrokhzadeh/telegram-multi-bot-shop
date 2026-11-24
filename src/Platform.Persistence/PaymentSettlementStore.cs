using System.Diagnostics;
using System.Security.Cryptography;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum PaymentSettlementOutcome { Pending, Review, Settled, Replayed }
public sealed record PaymentSettlement(PaymentSettlementOutcome Outcome, ExternalSettlementRoute Route, Guid? Credit = null);

public sealed class PaymentSettlementStore(DbSessionFactory sessions, SecretSnapshot secrets, CatalogStore catalog)
{
    public Task<PaymentSettlement> SettleAsync(BrandContext context, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct = default) => SettleCoreAsync(context, payment, lookup, null, ct);
    public Task<PaymentSettlement> SettleForBrandAsync(BrandContext context, long expectedRevision, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return SettleCoreAsync(context, payment, lookup, expectedRevision, ct);
    }
    private async Task<PaymentSettlement> SettleCoreAsync(BrandContext context, Guid payment,
        Func<InvoiceExpectation, CancellationToken, Task<ProviderPaymentSnapshot>> lookup, long? writerRevision, CancellationToken ct)
    {
        State before;
        await using (var read = await sessions.BeginAsync(ct))
        {
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(read, context, revision, ct);
            else await read.AdmitWriterAsync(context, ct);
            before = await LoadAsync(read, context, payment, ct);
        }
        if (before.Status == "settled") return new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, before.Credit);
        if (!Ready(before)) return new(before.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
        ProviderPaymentSnapshot remote = await lookup(Expectation(before), ct);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await LockAsync(session, context, before, ct, writerRevision);
            State data = await LoadAsync(session, context, payment, ct);
            if (data.Status == "settled") return new(PaymentSettlementOutcome.Replayed, ExternalSettlementRoute.AlreadyAccepted, data.Credit);
            if (!Ready(data)) return new(data.Status == "review" ? PaymentSettlementOutcome.Review : PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
            if (Stopwatch.GetElapsedTime(remote.ObservedAt) > TimeSpan.FromSeconds(30) || Expectation(data) != Expectation(before) ||
                data.Hash is null || before.Hash is null || !CryptographicOperations.FixedTimeEquals(data.Hash, before.Hash)) throw new PaymentNoticeRejectedException();
            bool match = remote.Invoice == Expectation(data) && remote.Received == data.Expected;
            if (!match || remote.Status != "finished")
            {
                if (remote.Invoice == Expectation(data) && remote.Received <= data.Expected && remote.Status is "waiting" or "confirming" or "confirmed" or "sending")
                    return new(PaymentSettlementOutcome.Pending, ExternalSettlementRoute.Review);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET status='review' WHERE id=$1", DbValue.Uuid(payment)), ct);
                await session.CommitAsync(ct);
                return new(PaymentSettlementOutcome.Review, ExternalSettlementRoute.Review);
            }
            await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET status='settled' WHERE id=$1", DbValue.Uuid(payment)), ct);
            ExternalSettlementRoute route;
            Guid? credit = null;
            if (data.Order is not Guid order)
            {
                route = ExternalSettlementRoute.Topup;
                credit = await CreditAsync(session, data, "wallet_topup", ct);
            }
            else
            {
                if (data.OrderStatus is "awaiting_payment" or "under_review" or "confirmation_pending" && data.First > data.Deadline)
                {
                    await session.ExecuteAsync(new DbStatement("""
                        UPDATE shop.orders SET payment_status='expired',fulfillment_status='cancelled',resolution_kind='unpaid_expiry',resolution_at=clock_timestamp(),
                            revision=revision+1,updated_at=clock_timestamp() WHERE id=$1
                        """, DbValue.Uuid(order)), ct);
                    data = data with { OrderStatus = "expired" };
                }
                if (data.OrderStatus is "expired" or "cancelled")
                {
                    route = ExternalSettlementRoute.LateCredit;
                    credit = await CreditAsync(session, data, "late_crypto_credit", ct);
                    await ReleaseAsync(session, context, order, ct);
                }
                else if (data.OrderStatus is "awaiting_payment" or "under_review" or "confirmation_pending")
                {
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET payment_status='paid',payment_confirmed_at=$1,revision=revision+1,updated_at=clock_timestamp() WHERE id=$2",
                        DbValue.Utc(data.Confirmed!.Value), DbValue.Uuid(order)), ct);
                    route = await DispatchAsync(session, context, data, ct);
                    if (route == ExternalSettlementRoute.LocalShortageCredit)
                    {
                        credit = await CreditAsync(session, data, "immediate_crypto_shortage_credit", ct);
                        await session.ExecuteAsync(new DbStatement("""
                            UPDATE shop.orders SET fulfillment_status='compensated',resolution_kind='immediate_crypto_shortage_credit',resolution_at=clock_timestamp(),
                                resolution_wallet_entry_id=$1,revision=revision+1,updated_at=clock_timestamp() WHERE id=$2
                            """, DbValue.Uuid(credit.Value), DbValue.Uuid(order)), ct);
                        await ReleaseAsync(session, context, order, ct);
                    }
                }
                else throw new PaymentNoticeRejectedException();
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE order_id=$1 AND job_kind='expire_order_payment' AND status IN ('pending','retry')", DbValue.Uuid(order)), ct);
                await NoticeAsync(session, context, data, ct);
            }
            if (credit is Guid entry) await session.ExecuteAsync(new DbStatement("UPDATE shop.payments SET compensating_wallet_entry_id=$1 WHERE id=$2", DbValue.Uuid(entry), DbValue.Uuid(payment)), ct);
            await session.CommitAsync(ct);
            return new(PaymentSettlementOutcome.Settled, route, credit);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    internal async Task<ExternalSettlementRoute> DispatchAsync(DbSession session, BrandContext context, State data, CancellationToken ct)
    {
        Guid order = data.Order!.Value, product = data.Product!.Value;
        if (data.Kind == "prepaid_units")
        {
            await new PricingStore(sessions, secrets).IssuePaidSourceAsync(session, context, order, ct);
            return ExternalSettlementRoute.Prepaid;
        }
        if (data.Preorder)
        {
            await FulfillmentAsync(session, order, "waiting_stock", null, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), order, DateTime.UtcNow, ct);
            return ExternalSettlementRoute.LocalPreorder;
        }
        if (data.Fulfillment == "manual")
        {
            await FulfillmentAsync(session, order, "requires_admin", null, ct);
            return ExternalSettlementRoute.LocalGoods;
        }
        var current = await session.QuerySingleAsync(new DbStatement("SELECT fulfillment_kind FROM shop.products WHERE id=$1", DbValue.Uuid(product)), r => r.GetString(0), ct);
        if (current != data.Fulfillment)
        {
            await FulfillmentAsync(session, order, "requires_admin", "source_ineligible", ct);
            if (data.Fulfillment == "supplier") await catalog.BindAcceptedAsync(session, context, product, order, data.Quantity, ct);
            return ExternalSettlementRoute.Review;
        }
        bool queued = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.orders WHERE product_id=$1 AND id<>$2 AND is_preorder AND payment_status IN ('paid','not_required')
                AND fulfillment_status IN ('waiting_stock','requires_admin'))
            """, DbValue.Uuid(product), DbValue.Uuid(order)), r => r.GetBoolean(0), ct);
        int wanted = queued || (data.Fulfillment == "supplier" && !data.LocalFirst) ? 0 : data.Quantity;
        var stock = await CheckoutStore.ReadStockAsync(session, product, wanted, ct);
        if (data.Fulfillment == "local" && stock.Count != data.Quantity)
        {
            if (data.Method == "crypto") return ExternalSettlementRoute.LocalShortageCredit;
            await FulfillmentAsync(session, order, "waiting_stock", null, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), order, DateTime.UtcNow, ct);
            return ExternalSettlementRoute.LocalGoods;
        }
        string? review = null;
        if (data.Fulfillment == "supplier" && stock.Count < data.Quantity)
            review = await catalog.BindAcceptedAsync(session, context, product, order, data.Quantity - stock.Count, ct);
        await new CheckoutStore(sessions, secrets).AcquireAsync(session, context, order, stock, ct);
        await FulfillmentAsync(session, order, review is null ? "acquiring" : "requires_admin", review, ct);
        if (stock.Count == data.Quantity) await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, order), order, DateTime.UtcNow, ct);
        return data.Fulfillment == "supplier" ? ExternalSettlementRoute.Supplier : ExternalSettlementRoute.LocalGoods;
    }

    private static async Task<Guid> CreditAsync(DbSession session, State data, string movement, CancellationToken ct)
    {
        Money amount = ExternalPaymentPolicy.FrozenCredit(data.Due);
        return await WalletOperations.PostAsync(session, data.Member, amount, null,
            new Entities.WalletEntry { Id = Guid.CreateVersion7(), MovementKind = movement, IdempotencyKey = "payment-credit:" + data.Id.ToString("D"), OrderId = data.Order, PaymentId = data.Id, Reason = "verified_original_invoice" }, ct);
    }
    private async Task ReleaseAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Guid? coupon = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
        if (coupon is Guid id) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, id, order, false, ct);
    }
    internal static async Task NoticeAsync(DbSession session, BrandContext context, State data, CancellationToken ct)
    {
        Guid? anchor = await session.QuerySingleAsync(new DbStatement("""
            SELECT id FROM (SELECT 1) seed LEFT JOIN LATERAL (SELECT id FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_status'
                AND status='succeeded' ORDER BY created_at,id LIMIT 1) a ON true
            """, Uuid(data.Order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
        Guid delivery = Guid.CreateVersion7();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id)
            VALUES ($1,$2,'customer_dm',$3,'order_status',CASE WHEN $4 IS NULL THEN 'send' ELSE 'edit' END,
                (SELECT telegram_message_id FROM shop.telegram_deliveries WHERE id=$4),$5,$6)
            """, DbValue.Uuid(delivery), DbValue.Uuid(context.BrandId), DbValue.Uuid(data.Member), Uuid(anchor), DbValue.Text("payment-status:" + data.Id.ToString("D")), Uuid(data.Order)), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), data.Id, DateTime.UtcNow, ct);
    }
    internal static Task FulfillmentAsync(DbSession session, Guid order, string status, string? review, CancellationToken ct) => session.ExecuteAsync(new DbStatement(
        "UPDATE shop.orders SET fulfillment_status=$1,review_code=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3", DbValue.Text(status),
        review is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text) : DbValue.Text(review), DbValue.Uuid(order)), ct);
    internal static async Task LockAsync(DbSession session, BrandContext context, State data, CancellationToken ct, long? writerRevision = null)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 FOR UPDATE) u", DbValue.Uuid(data.Member), DbValue.Uuid(context.BrandId)), r => r.GetInt64(0), ct);
        if (data.Product is Guid product) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.products WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(product)), r => r.GetInt64(0), ct);
        if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context, revision, ct);
        else
        {
            await session.AdmitWriterAsync(context, ct);
            await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE) b", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetInt64(0), ct);
        }
        if (data.Order is Guid order) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE) o", DbValue.Uuid(order)), r => r.GetInt64(0), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.payments WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(data.Id)), r => r.GetInt64(0), ct);
    }
    internal static async Task<State> LoadAsync(DbSession session, BrandContext context, Guid payment, CancellationToken ct)
    {
        State? data = await session.QuerySingleAsync<State?>(new DbStatement("""
            SELECT p.id,COALESCE(p.topup_user_id,o.user_id),p.order_id,o.product_id,p.payment_method,p.status,p.amount_due::text,p.provider_payment_ref,
                p.idempotency_ref,p.network_asset,p.expected_network_amount::text,p.network_amount::text,p.amount_received::text,p.provider_status_code,
                p.ipn_payload_sha256,p.confirmed_at,p.first_payment_observed_at,o.payment_due_at,o.payment_status,o.order_kind,o.quantity,
                o.fulfillment_kind_snapshot,o.local_first_snapshot,o.is_preorder,p.compensating_wallet_entry_id,p.provider_code,p.evidence_asset_ref
            FROM (SELECT 1) seed LEFT JOIN (shop.payments p LEFT JOIN shop.orders o ON o.id=p.order_id JOIN shop.users u ON u.id=COALESCE(p.topup_user_id,o.user_id)
                JOIN shop.brands b ON b.id=u.brand_id) ON p.id=$1 AND b.id=$2 AND b.bot_id=$3
            """, DbValue.Uuid(payment), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.IsDBNull(0) ? null : new State(r.GetGuid(0), r.GetGuid(1), Id(r, 2), Id(r, 3), r.GetString(4), r.GetString(5), ExactDecimal.Parse(r.GetString(6)),
                Text(r, 7), r.GetString(8), Text(r, 9), Number(r, 10), Number(r, 11), Number(r, 12), Text(r, 13), r.IsDBNull(14) ? null : r.GetBinary(14),
                Time(r, 15), Time(r, 16), Time(r, 17), Text(r, 18), Text(r, 19), r.IsDBNull(20) ? 0 : r.GetInt32(20), Text(r, 21), !r.IsDBNull(22) && r.GetBoolean(22), !r.IsDBNull(23) && r.GetBoolean(23), Id(r, 24), Text(r, 25), !r.IsDBNull(26)), ct);
        if (data is null) throw new UnauthorizedAccessException("Payment is unavailable.");
        return data;
    }
    private static bool Ready(State data) => data.Method == "crypto" && data.ProviderCode == "nowpayments" && data.Status == "confirming" && data.Provider is not null && data.Expected > 0 &&
        data.Tokens == data.Expected && data.Received == data.Due && data.ProviderStatus == "finished" && data.Hash is not null && data.Evidence && data.Confirmed is not null && data.First is not null;
    private static InvoiceExpectation Expectation(State data) => new(data.Provider!, data.Origin, data.Due, data.Asset == "USDTBSC" ? "usdtbsc" : "usdttrc20", data.Expected!.Value);
    private static Guid? Id(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static string? Text(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static decimal? Number(DbRow r, int i) => r.IsDBNull(i) ? null : ExactDecimal.Parse(r.GetString(i));
    private static DateTime? Time(DbRow r, int i) => r.IsDBNull(i) ? null : r.GetUtc(i);
    private static DbValue Uuid(Guid? id) => id is Guid value ? DbValue.Uuid(value) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid);
    internal sealed record State(Guid Id, Guid Member, Guid? Order, Guid? Product, string Method, string Status, decimal Due, string? Provider, string Origin, string? Asset,
        decimal? Expected, decimal? Tokens, decimal? Received, string? ProviderStatus, byte[]? Hash, DateTime? Confirmed, DateTime? First, DateTime? Deadline,
        string? OrderStatus, string? Kind, int Quantity, string? Fulfillment, bool LocalFirst, bool Preorder, Guid? Credit, string? ProviderCode, bool Evidence);
}
