using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

internal sealed record CheckoutReplay(bool NoOrder, bool NoAudit, Guid? Member, Guid? Brand, byte[]? Hash);
internal static class CheckoutQueries
{
    internal static async Task AcquireStockAsync(EfSession data, Guid order, CheckoutStore.Stock item,
        Guid fulfillment, int number, DateTime now, CancellationToken ct)
    {
        var (content, row) = await ReserveStockAsync(data, order, item.Id, fulfillment, number, now, ct);
        content.Status = "acquired"; content.ContentKind = "text"; content.DeliveredContent = row.CredentialContent;
        await data.FlushAsync(ct, false);
    }

    internal static async Task AcquireStockAsync(EfSession data, Guid order, Guid inventory,
        Guid fulfillment, int number, DateTime now, CancellationToken ct)
    {
        var (content, row) = await ReserveStockAsync(data, order, inventory, fulfillment, number, now, ct);
        content.Status = "acquired"; content.ContentKind = "text"; content.DeliveredContent = row.CredentialContent;
        await data.FlushAsync(ct, false);
    }

    private static async Task<(Entities.Fulfillment, Entities.InventoryItem)> ReserveStockAsync(EfSession data, Guid order,
        Guid inventory, Guid fulfillment, int number, DateTime now, CancellationToken ct)
    {
        var content = new Entities.Fulfillment { Id = fulfillment, OrderId = order, UnitNo = number, Kind = "local" };
        data.Data.Add(content);
        await data.FlushAsync(ct, false);
        var row = await data.Data.Set<Entities.InventoryItem>().SingleAsync(value => value.Id == inventory && value.State == "available", ct);
        row.State = "allocated"; row.AssignedFulfillmentId = fulfillment; row.AllocatedAt = now;
        await data.FlushAsync(ct, false);
        return (content, row);
    }

    internal static Task<List<CheckoutStore.Stock>> ReadStockAsync(EfSession data, IQueryable<Entities.InventoryItem> rows, CancellationToken ct) =>
        rows.Select(value => new CheckoutStore.Stock(value.Id)).ToListAsync(ct);

    internal static async Task<byte[]?> FreezeBankDestinationAsync(EfSession data, BrandContext context,
        bool requireEnabled, CancellationToken ct)
    {
        var card = await data.Data.Set<Entities.Brand>()
            .Where(value => value.Id == context.BrandId && (!requireEnabled || value.AllowBank) &&
                value.BankNumber != null && value.BankHolder != null)
            .Select(value => new { Number = value.BankNumber!, Holder = value.BankHolder! }).SingleOrDefaultAsync(ct);
        if (card is null) return null;
        try { return PackBankDestination(card.Number, card.Holder); }
        finally { CryptographicOperations.ZeroMemory(card.Number); CryptographicOperations.ZeroMemory(card.Holder); }
    }

    private static byte[] PackBankDestination(byte[] number, byte[] holder)
    {
        using var bytes = new MemoryStream();
        try
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(1); writer.Write(number.Length); writer.Write(number);
                writer.Write(holder.Length); writer.Write(holder);
            }
            return bytes.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
    }

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
