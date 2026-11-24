using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteAdminOrderStore(SqliteSessionFactory sessions)
{
    public Task<AdminPage<AdminOrder>> SearchAsync(AuthenticatedAdmin actor, BrandContext context,
        Guid? member, Guid? order, Guid? after, int limit, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            AdminCommand.Page(limit);
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            var list = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from o in data.Data.Set<Entities.Order>() join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where b.Id == context.BrandId && b.BotId == context.BotId && (member == null || u.Id == member) && (order == null || o.Id == order) && (after == null || o.Id.CompareTo(after.Value) > 0) orderby o.Id select new AdminOrder(o.Id, u.BrandId, o.UserId, o.ProductId, o.Quantity, o.PaymentStatus, o.FulfillmentStatus, o.IsPreorder, o.HoldAt != null, o.ReviewCode, o.Revision, o.ProductTitleSnapshot, o.CreatedAt)).Take(limit + 1).ToListAsync(cancel), token);
            return new AdminPage<AdminOrder>(list.Take(limit).ToArray(),
                list.Count > limit ? list[limit - 1].Id : null);
        }, ct);
}
