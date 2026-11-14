using Platform.Security;

namespace Platform.Persistence;

public sealed class LocalPreorderStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    private const string Head = "SELECT o.id,o.user_id,u.brand_id FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.product_id=$1 AND o.is_preorder AND o.payment_status IN ('paid','not_required') AND o.fulfillment_status IN ('waiting_stock','requires_admin') ORDER BY o.payment_confirmed_at,o.id LIMIT 1";
    public async Task<Guid?> DrainAsync(JobLease lease, IReadOnlyList<BrandContext> contexts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        if (lease.Intent.Kind != JobKind.DrainLocalPreorders || lease.Intent.BrandId is not null || lease.Intent.OriginId is not Guid product) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            Identity? candidate = await ReadHeadAsync(session, product, ct);
            if (candidate is not null) await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(candidate.Member)), r => r.GetGuid(0), ct);
            await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => r.GetGuid(0), ct);
            await session.AdmitSharedWriterAsync(ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            Identity? current = await ReadHeadAsync(session, product, ct);
            Guid? allocated = null;
            bool again = current is not null && current != candidate;
            if (current is not null && current == candidate)
            {
                BrandContext context = contexts.SingleOrDefault(c => c.BrandId == current.Brand) ?? throw new UnauthorizedAccessException("FIFO head has no trusted brand registration.");
                bool active = await session.QuerySingleAsync(new DbStatement("SELECT active AND bot_id=$2 FROM shop.brands WHERE id=$1 FOR NO KEY UPDATE", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct);
                if (!active && session.IsWriterScoped)
                {
                    await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(30), int.MaxValue, JobError.Busy, ct);
                    await session.CommitAsync(ct); return null;
                }
                var state = await session.QuerySingleAsync(new DbStatement("""
                    SELECT o.quantity,o.hold_at IS NOT NULL OR o.review_code IS NOT NULL OR o.resolution_kind IS NOT NULL OR u.blocked_at IS NOT NULL OR u.banned_at IS NOT NULL,
                        EXISTS(SELECT 1 FROM shop.fulfillments WHERE order_id=o.id),o.fulfillment_kind_snapshot
                    FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 FOR UPDATE OF o
                    """, DbValue.Uuid(current.Order)), r => (Quantity: r.GetInt32(0), Blocked: r.GetBoolean(1), Units: r.GetBoolean(2), Kind: r.GetString(3)), ct);
                if (active && !state.Blocked && !state.Units && state.Kind == "local")
                {
                    var stock = await CheckoutStore.ReadStockAsync(session, product, state.Quantity, ct);
                    if (stock.Count == state.Quantity)
                    {
                        await new CheckoutStore(sessions, secrets).AcquireAsync(session, context, current.Order, stock, ct);
                        await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='acquiring',revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(current.Order)), ct);
                        await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, current.Order), current.Order, DateTime.UtcNow, ct);
                        allocated = current.Order; again = true;
                    }
                }
            }
            if (again) await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), lease.Id, DateTime.UtcNow, ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            await session.CommitAsync(ct);
            return allocated;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static Task<Identity?> ReadHeadAsync(DbSession session, Guid product, CancellationToken ct) => session.QuerySingleAsync<Identity?>(new DbStatement("SELECT h.id,h.user_id,h.brand_id FROM (SELECT 1) seed LEFT JOIN LATERAL (" + Head + ") h ON true", DbValue.Uuid(product)), r => r.IsDBNull(0) ? null : new Identity(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2)), ct);
    private sealed record Identity(Guid Order, Guid Member, Guid Brand);
}
