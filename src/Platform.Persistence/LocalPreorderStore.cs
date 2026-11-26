using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed class LocalPreorderStore(DbSessionFactory sessions)
{

    public async Task<Guid?> DrainAsync(JobLease lease, IReadOnlyList<BrandContext> contexts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        if (lease.Intent.Kind != JobKind.DrainLocalPreorders || lease.Intent.BrandId is not null || lease.Intent.OriginId is not Guid product) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            Identity? candidate = await ReadHeadAsync(session, product, ct);
            if (candidate is not null) await session.RunEfStepAsync(false, (data, token) => data.LockUserAsync(candidate.Member, candidate.Brand, token), ct);
            await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(product, token), ct);
            await session.AdmitSharedWriterAsync(ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            Identity? current = await ReadHeadAsync(session, product, ct);
            Guid? allocated = null;
            bool again = current is not null && current != candidate;
            if (current is not null && current == candidate)
            {
                BrandContext context = contexts.SingleOrDefault(c => c.BrandId == current.Brand) ?? throw new UnauthorizedAccessException("FIFO head has no trusted brand registration.");
                bool active = await session.RunEfStepAsync(false, async (data, token) => { await data.LockBrandAsync(context.BrandId, token); return await data.Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, token); }, ct);
                if (!active && session.IsWriterScoped)
                {
                    await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(30), int.MaxValue, JobError.Busy, ct);
                    await session.CommitAsync(ct); return null;
                }
                var state = await session.RunEfStepAsync(false, async (data, token) =>
                {
                    await data.LockOrderAsync(current.Order, token);
                    return await (from order in data.Data.Set<Entities.Order>() join user in data.Data.Set<Entities.User>() on order.UserId equals user.Id where order.Id == current.Order select new { order.Quantity, Blocked = order.HoldAt != null || order.ReviewCode != null || order.ResolutionKind != null || user.BlockedAt != null || user.BannedAt != null, Units = data.Data.Set<Entities.Fulfillment>().Any(value => value.OrderId == order.Id), Kind = order.FulfillmentKindSnapshot }).SingleAsync(token);
                }, ct);
                if (active && !state.Blocked && !state.Units && state.Kind == "local")
                {
                    var stock = await CheckoutStore.ReadStockAsync(session, product, state.Quantity, ct);
                    if (stock.Count == state.Quantity)
                    {
                        await new CheckoutStore(sessions).AcquireAsync(session, context, current.Order, stock, ct);
                        await PaymentSettlementStore.FulfillmentAsync(session, current.Order, "acquiring", null, ct);
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
    private static Task<Identity?> ReadHeadAsync(DbSession session, Guid product, CancellationToken ct) => session.RunEfStepAsync(false, async (data, token) =>
    {
        var head = await PreorderQueries.HeadAsync(data, product, token);
        return head is null ? null : new Identity(head.Order, head.Member, head.Brand);
    }, ct);
    private sealed record Identity(Guid Order, Guid Member, Guid Brand);
}
