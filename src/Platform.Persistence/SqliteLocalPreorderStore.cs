using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteLocalPreorderStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions.RequirePlaintext();

    public Task<JobLease?> ClaimAsync(Guid worker, CancellationToken ct = default) => sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx, null, JobKind.DrainLocalPreorders, worker, TimeSpan.FromSeconds(30), token), ct);
    public Task<Guid?> DrainAsync(JobLease lease, IReadOnlyList<BrandContext> contexts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        if (lease.Intent.Kind != JobKind.DrainLocalPreorders || lease.Intent.BrandId is not null || lease.Intent.OriginId is not Guid product || product == Guid.Empty) throw new UnauthorizedAccessException("The shared preorder job is unavailable.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            Guid? acquired = null;
            var head = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => PreorderQueries.HeadAsync(data, product, cancel), token);
            if (head is not null)
            {
                BrandContext context = contexts.SingleOrDefault(value => value.BrandId == head.Brand) ?? throw new UnauthorizedAccessException("The preorder brand has no trusted registration.");
                bool active = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, cancel), token);
                if (!active) { await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(30), int.MaxValue, JobError.Busy, token); return null; }
                if (!head.Blocked && !head.Units && head.Kind == "local")
                {
                    var stock = await SqliteOrderPaymentSettlementStore.ReadStockAsync(tx, product, head.Quantity, token);
                    if (stock.Count == head.Quantity)
                    {
                        DateTime now = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.BrandClockAsync(context.BrandId, cancel), token);
                        await SqliteOrderPaymentSettlementStore.AcquireAsync(tx, context, head.Order, stock, SqliteSchema.UtcText(now), token);
                        await SqlitePaymentOperations.OrderAsync(tx, head.Order, row =>
                        {
                            if (row.FulfillmentStatus is not ("waiting_stock" or "requires_admin")) throw new InvalidOperationException("Preorder fulfillment state changed.");
                            row.FulfillmentStatus = "acquiring"; row.ReviewCode = null;
                        }, now, token);
                        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, head.Order), head.Order, DateTime.UtcNow, token);
                        acquired = head.Order;
                    }
                }
            }
            if (acquired is not null) await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Product(JobKind.DrainLocalPreorders, product), lease.Id, DateTime.UtcNow, token);
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token); return acquired;
        }, ct);
    }
}
