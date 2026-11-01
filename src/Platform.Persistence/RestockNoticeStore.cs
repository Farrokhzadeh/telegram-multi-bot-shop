using Platform.Security;

namespace Platform.Persistence;

public sealed partial class CampaignStore
{
    private static async Task<bool> AvailableAsync(DbSession session, Guid brand, Guid product, IReadOnlyDictionary<string, string> accounts, CancellationToken ct)
    {
        var state = await session.QuerySingleAsync(new DbStatement("""
            SELECT p.active AND b.active AND bp.visible,p.fulfillment_kind,bp.min_quantity,p.local_first,
                CASE WHEN EXISTS(SELECT 1 FROM shop.orders o WHERE o.product_id=p.id AND o.is_preorder AND o.payment_status IN ('paid','not_required') AND o.fulfillment_status IN ('waiting_stock','requires_admin'))
                    THEN 0 ELSE (SELECT count(*) FROM shop.inventory_items WHERE product_id=p.id AND state='available') END,
                s.provider_code,s.observed_account_ref,s.available_quantity,
                s.enabled AND s.availability_code='available' AND s.eligibility_code='ready_item' AND s.observed_source_revision=s.revision AND s.observed_at>=clock_timestamp()-INTERVAL '30 seconds'
            FROM shop.products p JOIN shop.brand_products bp ON bp.product_id=p.id JOIN shop.brands b ON b.id=bp.brand_id LEFT JOIN shop.product_supplier_sources s ON s.id=p.active_supplier_source_id
            WHERE p.id=$1 AND b.id=$2
            """, DbValue.Uuid(product), DbValue.Uuid(brand)), r => (Published: r.GetBoolean(0), Kind: r.GetString(1), Minimum: r.GetInt32(2), LocalFirst: r.GetBoolean(3), Local: r.GetInt64(4), Provider: Text(r, 5), Account: Text(r, 6), Remote: r.IsDBNull(7) ? 0 : r.GetInt32(7), Fresh: !r.IsDBNull(8) && r.GetBoolean(8)), ct);
        if (!state.Published) return false;
        if (state.Kind == "manual") return true;
        long local = state.Kind == "supplier" && !state.LocalFirst ? 0 : state.Local;
        if (local >= state.Minimum) return true;
        return state.Kind == "supplier" && state.Fresh && state.Provider is string provider && accounts.TryGetValue(provider, out string? account) && account == state.Account && local + state.Remote >= state.Minimum;
    }
    public async Task<int> EvaluateRestockAsync(JobLease lease, IReadOnlyDictionary<string, string> accounts, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.EvaluateRestock || lease.Intent.OriginId is not Guid product) throw new UnauthorizedAccessException();
        var brands = new List<Guid>();
        await using (var read = await sessions.BeginAsync(ct)) brands.AddRange(await read.QueryManyAsync(new DbStatement("SELECT brand_id FROM shop.brand_products WHERE product_id=$1 ORDER BY brand_id", DbValue.Uuid(product)), r => r.GetGuid(0), 1000, ct));
        int total = 0;
        bool more = false;
        foreach (Guid brand in brands)
        {
            await using var session = await sessions.BeginAsync(ct);
            try
            {
                await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
                if (!await AvailableAsync(session, brand, product, accounts, ct)) { await session.CommitAsync(ct); continue; }
                var members = await session.QueryManyAsync(new DbStatement("""
                    SELECT s.id,s.user_id FROM shop.product_stock_subscriptions s JOIN shop.users u ON u.id=s.user_id
                    WHERE s.product_id=$1 AND u.brand_id=$2 AND s.closed_at IS NULL AND s.cancelled_at IS NULL AND s.triggered_at IS NULL ORDER BY s.id LIMIT 1000
                    """, DbValue.Uuid(product), DbValue.Uuid(brand)), r => (Consent: r.GetGuid(0), Member: r.GetGuid(1)), 1000, ct);
                foreach (var item in members)
                {
                    await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(item.Member)), r => r.GetGuid(0), ct);
                    bool active = await session.QuerySingleAsync(new DbStatement("SELECT closed_at IS NULL AND cancelled_at IS NULL AND triggered_at IS NULL FROM shop.product_stock_subscriptions WHERE id=$1 FOR UPDATE", DbValue.Uuid(item.Consent)), r => r.GetBoolean(0), ct);
                    if (!active) continue;
                    Guid delivery = Guid.CreateVersion7();
                    await session.ExecuteAsync(new DbStatement("""
                        INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,recipient_language,eligibility_checked_at,dedupe_key,product_stock_subscription_id)
                        SELECT $1,$2,'customer_dm',id,'restock_notice','send',CASE WHEN split_part(lower(language),'-',1) IN ('fa','en') THEN split_part(lower(language),'-',1) ELSE 'en' END,
                            clock_timestamp(),$3,$4 FROM shop.users WHERE id=$5
                        """, DbValue.Uuid(delivery), DbValue.Uuid(brand), DbValue.Text("restock:" + item.Consent.ToString("D")), DbValue.Uuid(item.Consent), DbValue.Uuid(item.Member)), ct);
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.product_stock_subscriptions SET triggered_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(item.Consent)), ct);
                    await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(brand, delivery), delivery, DateTime.UtcNow, ct);
                    total++;
                }
                more |= members.Count == 1000;
                await session.CommitAsync(ct);
            }
            catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
        }
        await using var finish = await sessions.BeginAsync(ct);
        if (more) await OutboxStore.RetryAsync(finish, lease, TimeSpan.FromSeconds(1), 1000000, JobError.Transient, ct);
        else await OutboxStore.CompleteAsync(finish, lease, ct);
        await finish.CommitAsync(ct); return total;
    }
    public async Task<int> CleanupClosedSubscriptionsAsync(JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.SubscriptionCleanup) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(5), ct);
            var rows = await session.QueryManyAsync(new DbStatement("""
                SELECT s.id FROM shop.product_stock_subscriptions s WHERE s.closed_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'
                    AND NOT EXISTS(SELECT 1 FROM shop.telegram_deliveries d WHERE d.product_stock_subscription_id=s.id AND (d.purpose<>'restock_notice' OR d.status NOT IN ('succeeded','failed','cancelled')))
                    AND NOT EXISTS(SELECT 1 FROM shop.jobs j JOIN shop.telegram_deliveries d ON d.id=j.telegram_delivery_id WHERE d.product_stock_subscription_id=s.id AND j.status NOT IN ('succeeded','failed','cancelled'))
                ORDER BY s.id LIMIT 100 FOR UPDATE OF s SKIP LOCKED
                """), r => r.GetGuid(0), 100, ct);
            foreach (Guid id in rows)
            {
                await session.ExecuteAsync(new DbStatement("DELETE FROM shop.jobs WHERE telegram_delivery_id IN (SELECT id FROM shop.telegram_deliveries WHERE product_stock_subscription_id=$1 AND purpose='restock_notice' AND status IN ('succeeded','failed','cancelled'))", DbValue.Uuid(id)), ct);
                await session.ExecuteAsync(new DbStatement("DELETE FROM shop.telegram_deliveries WHERE product_stock_subscription_id=$1 AND purpose='restock_notice' AND status IN ('succeeded','failed','cancelled')", DbValue.Uuid(id)), ct);
                await session.ExecuteAsync(new DbStatement("DELETE FROM shop.product_stock_subscriptions WHERE id=$1", DbValue.Uuid(id)), ct);
            }
            await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return rows.Count;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
