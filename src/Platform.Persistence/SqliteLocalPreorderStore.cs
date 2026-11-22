using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteLocalPreorderStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<JobLease?> ClaimAsync(Guid worker, CancellationToken ct = default) =>
        sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx, null,
            JobKind.DrainLocalPreorders, worker, TimeSpan.FromSeconds(30), token), ct);

    public Task<Guid?> DrainAsync(JobLease lease, IReadOnlyList<BrandContext> contexts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        if (lease.Intent.Kind != JobKind.DrainLocalPreorders || lease.Intent.BrandId is not null ||
            lease.Intent.OriginId is not Guid product || product == Guid.Empty)
            throw new UnauthorizedAccessException("The shared preorder job is unavailable.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            Guid? acquired = null;
            using (var head = SqliteWallet.Command(tx, """
                SELECT o.id,o.user_id,u.brand_id,o.quantity,o.fulfillment_kind_snapshot,
                    o.hold_at IS NOT NULL OR o.review_code IS NOT NULL OR o.resolution_kind IS NOT NULL
                        OR u.blocked_at IS NOT NULL OR u.banned_at IS NOT NULL,
                    EXISTS(SELECT 1 FROM fulfillments f WHERE f.order_id=o.id)
                FROM orders o JOIN users u ON u.id=o.user_id
                WHERE o.product_id=$product AND o.is_preorder=1 AND
                    o.payment_status IN ('paid','not_required') AND
                    o.fulfillment_status IN ('waiting_stock','requires_admin')
                ORDER BY o.payment_confirmed_at,o.id LIMIT 1
                """, ("$product", product)))
            await using (var row = await head.ExecuteReaderAsync(token))
            {
                if (await row.ReadAsync(token))
                {
                    Guid order = row.GetGuid(0), brand = row.GetGuid(2);
                    int quantity = row.GetInt32(3);
                    bool blocked = row.GetBoolean(5), units = row.GetBoolean(6);
                    string kind = row.GetString(4);
                    BrandContext context = contexts.SingleOrDefault(value => value.BrandId == brand)
                        ?? throw new UnauthorizedAccessException("The preorder brand has no trusted registration.");
                    bool active;
                    using (var registration = SqliteWallet.Command(tx, """
                        SELECT active FROM brands WHERE id=$brand AND bot_id=$bot
                        """, ("$brand", context.BrandId), ("$bot", context.BotId)))
                        active = await registration.ExecuteScalarAsync(token) is long enabled && enabled != 0;
                    if (!active)
                    {
                        await row.CloseAsync();
                        await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(30),
                            int.MaxValue, JobError.Busy, token);
                        return null;
                    }
                    if (!blocked && !units && kind == "local")
                    {
                        await row.CloseAsync();
                        var stock = await SqliteOrderPaymentSettlementStore.ReadStockAsync(tx, product, quantity, token);
                        if (stock.Count == quantity)
                        {
                            await new SqliteOrderPaymentSettlementStore(sessions, secrets).AcquireAsync(
                                tx, context, order, stock, SqliteSchema.UtcText(DateTime.UtcNow), token);
                            using var change = SqliteWallet.Command(tx, """
                                UPDATE orders SET fulfillment_status='acquiring',review_code=NULL,
                                    revision=revision+1,updated_at=$now
                                WHERE id=$order AND fulfillment_status IN ('waiting_stock','requires_admin')
                                """, ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$order", order));
                            if (await change.ExecuteNonQueryAsync(token) != 1)
                                throw new InvalidOperationException("Preorder fulfillment state changed.");
                            await OutboxStore.EnqueueSqliteAsync(tx,
                                JobIntent.Order(JobKind.PrepareManifest, context.BrandId, order), order,
                                DateTime.UtcNow, token);
                            acquired = order;
                        }
                    }
                }
            }
            if (acquired is not null)
                await OutboxStore.EnqueueSqliteAsync(tx,
                    JobIntent.Product(JobKind.DrainLocalPreorders, product), lease.Id, DateTime.UtcNow, token);
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return acquired;
        }, ct);
    }
}
