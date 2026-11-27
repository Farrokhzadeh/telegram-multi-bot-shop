using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed partial class PricingStore
{
    public async Task<Guid> IssuePaidSourceAsync(DbSession session, BrandContext context, Guid sourceOrder, CancellationToken ct = default)
    {
        try
        {
            await session.RunEfStepAsync(false, async (data, token) =>
            {
                var identity = await (from order in data.Data.Set<Entities.Order>() join member in data.Data.Set<Entities.User>() on order.UserId equals member.Id where order.Id == sourceOrder && member.BrandId == context.BrandId select new { Member = member.Id, order.ProductId }).SingleAsync(token);
                await data.LockUserAsync(identity.Member, context.BrandId, token);
                if (identity.ProductId is Guid product) await data.LockProductAsync(product, token);
                return true;
            }, ct);
            await LockBrandAsync(session, context, ct);
            var sale = await session.RunEfStepAsync(false, (data, token) => PrepaidEntitlements.ReadAsync(data, context, sourceOrder, token), ct);
            if (sale is null || sale.Kind != "prepaid_units" || sale.Status != "paid") Reject(session);
            Guid? prior = await session.RunEfStepAsync(false, (data, token) => PrepaidEntitlements.PriorAsync(data, sourceOrder, token), ct);
            if (prior is Guid existing) return existing;
            var due = PrepaidEntitlements.Due(sale);
            if (due.Amount <= 0 || !await session.RunEfStepAsync(false, (data, token) => PrepaidEntitlements.EvidenceAsync(data, sale, sourceOrder, due.Amount, token), ct)) Reject(session);
            Guid id = await session.RunEfStepAsync(true, (data, token) => PrepaidEntitlements.CreateAsync(data, context, sourceOrder, sale, token), ct);
            Guid? held = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.DiscountUse>().Where(value => value.OrderId == sourceOrder && value.Status == "held").Select(value => (Guid?)value.DiscountId).SingleOrDefaultAsync(token), ct);
            if (held is Guid coupon) await TransitionClaimAsync(session, context, coupon, sourceOrder, true, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Coupon(context.BrandId, id), sourceOrder, DateTime.UtcNow, ct);
            return id;
        }
        catch { Poison(session); throw; }
    }
}
