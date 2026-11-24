using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal Task<int> LockPaymentAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Payment>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
    internal async Task RequirePaymentCommandAsync(BrandContext context, Guid payment, CancellationToken ct)
    {
        var member = await (from value in Data.Set<Entities.Payment>()
                            join order in Data.Set<Entities.Order>() on value.OrderId equals order.Id into orders
                            from order in orders.DefaultIfEmpty()
                            join user in Data.Set<Entities.User>() on (value.TopupUserId ?? (Guid?)order.UserId) equals user.Id
                            join brand in Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
                            where value.Id == payment && brand.Id == context.BrandId && brand.BotId == context.BotId
                            select (Guid?)user.Id).SingleOrDefaultAsync(ct);
        if (member is null) RejectScope("Payment scope is unavailable.");
        await RequireWalletCommandAsync(member.Value, ct);
        await LockPaymentAsync(payment, ct);
    }
}
internal static class PaymentInstructionQueries
{
    internal static async Task<Guid> EnqueueAsync(EfSession data, BrandContext context, Guid order, string cause, CancellationToken ct)
    {
        await data.RequireOrderCommandAsync(context, order, ct);
        await data.LockOrderAsync(order, ct);
        string dedupe = "instruction-" + cause + ":" + order.ToString("D");
        var prior = await data.Data.Set<Entities.TelegramDelivery>().Where(value => value.DedupeKey == dedupe).Select(value => new { value.Id, value.BrandId, value.OrderId }).SingleOrDefaultAsync(ct);
        if (prior is not null)
        {
            if (prior.BrandId != context.BrandId || prior.OrderId != order) data.RejectScope("Payment instruction scope is unavailable.");
            return prior.Id;
        }
        Guid member = await data.Data.Set<Entities.Order>().Where(value => value.Id == order).Select(value => value.UserId).SingleAsync(ct);
        long? message = await data.Data.Set<Entities.TelegramDelivery>().Where(value => value.OrderId == order && value.Purpose == "order_status" && value.Status == "succeeded" && value.TelegramMessageId != null).OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Select(value => value.TelegramMessageId).FirstOrDefaultAsync(ct);
        Guid id = Guid.CreateVersion7();
        data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = member, Purpose = "order_status", Action = message is null ? "send" : "edit", TargetMessageId = message, DedupeKey = dedupe, OrderId = order });
        return id;
    }
}

internal static class PaymentStateQueries
{
    internal static async Task<bool> ExpireAsync(EfSession data, BrandContext context, Guid order, DateTime now, CancellationToken ct)
    {
        await data.RequireOrderCommandAsync(context, order, ct); await data.LockOrderAsync(order, ct);
        var row = await data.Data.Set<Entities.Order>().SingleOrDefaultAsync(value => value.Id == order && value.PaymentStatus == "awaiting_payment", ct);
        if (row is null) data.RejectConflict("Expiry state was superseded.");
        row.PaymentStatus = "expired"; row.FulfillmentStatus = "cancelled"; row.ResolutionKind = "unpaid_expiry"; row.ResolutionAt = now; row.Revision = checked(row.Revision + 1); row.UpdatedAt = now;
        return true;
    }
}
