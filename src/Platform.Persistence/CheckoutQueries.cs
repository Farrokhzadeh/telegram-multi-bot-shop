using Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

internal sealed record CheckoutReplay(bool NoOrder, bool NoAudit, Guid? Member, Guid? Brand, byte[]? Hash);
internal static class CheckoutQueries
{
    internal static async Task InsertAsync(EfSession data, BrandContext context, long actor, Entities.Order order, byte[] hash, CancellationToken ct)
    {
        var member = await data.RequireMembershipAsync(context, actor, false, false, ct, true);
        if (member!.Id != order.UserId) data.RejectScope("Checkout membership is unavailable.");
        data.Data.Add(new Entities.AuditEvent { Id = order.Id, ActorKind = "customer", ActorUserId = member.Id, BrandId = context.BrandId, Action = "create", EntityKind = "order", EntityId = order.Id, Reason = "customer_request", CommandSha256 = hash, CorrelationId = Guid.NewGuid() });
        await data.FlushAsync(ct, false);
        data.Data.Add(order);
    }
    internal static async Task<CheckoutReplay> ReplayAsync(EfSession data, Guid order, CancellationToken ct)
    {
        bool exists = await data.Data.Set<Entities.Order>().AnyAsync(value => value.Id == order, ct);
        var audit = await data.Data.Set<Entities.AuditEvent>().Where(value => value.Id == order).Select(value => new { value.ActorUserId, value.BrandId, value.CommandSha256 }).SingleOrDefaultAsync(ct);
        return new(!exists, audit is null, audit?.ActorUserId, audit?.BrandId, audit?.CommandSha256);
    }
}
internal sealed partial class EfSession
{
    internal Task<int> LockExpiryJobsAsync(Guid order, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Job>().Where(value => value.OrderId == order && value.JobKind == "expire_order_payment" && (value.Status == "pending" || value.Status == "retry")).OrderBy(value => value.Id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockPendingOrdersAsync(Guid member, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Order>().Where(value => value.UserId == member && value.PaymentStatus == "awaiting_payment").OrderBy(value => value.Id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockPendingPaymentsAsync(Guid member, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Payment>().Where(value => Data.Set<Entities.Order>().Any(order => order.Id == value.OrderId && order.UserId == member && order.PaymentStatus == "awaiting_payment")).OrderBy(value => value.Id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockInventoryAsync(Guid product, int offset, int count, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.InventoryItem>().Where(value => value.ProductId == product && value.State == "available").OrderBy(value => value.Id).Skip(offset).Take(count).Select(value => value.Id), EfRowLock.Update, ct);
}
