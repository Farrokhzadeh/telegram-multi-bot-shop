using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal Task<int> LockMessageAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.TelegramDelivery>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockJobAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Job>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
    internal async Task<JobLease?> ClaimPaymentMessageAsync(Guid? brand, Guid worker, CancellationToken ct)
    {
        if (!write || !nativeWriterBoundary) RejectScope("A native claim transaction is required.");
        if (Integrity.RowLock != EfRowLock.None) RejectScope("Row locks cannot overlap.");
        Guid? selected;
        try
        {
            Integrity.RowLock = EfRowLock.UpdateSkipLocked;
            selected = await Data.Set<Entities.Job>().Where(value => (brand == null || value.BrandId == brand) && value.JobKind == "send_telegram" && (value.Status == "pending" || value.Status == "retry") && value.AvailableAt <= EfValues.UtcNow() && Data.Set<Entities.TelegramDelivery>().Any(delivery => delivery.Id == value.TelegramDeliveryId && delivery.Purpose == "order_status" && (delivery.Status == "pending" || delivery.Status == "retry") && delivery.NextAttemptAt <= EfValues.UtcNow() && Data.Set<Entities.Order>().Any(order => order.Id == delivery.OrderId && order.PaymentDueAt != null))).OrderBy(value => value.AvailableAt).ThenBy(value => value.Id).Select(value => (Guid?)value.Id).FirstOrDefaultAsync(ct);
        }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
        if (selected is null) return null;
        var row = await Data.Set<Entities.Job>().SingleAsync(value => value.Id == selected, ct);
        if (writer is not null) await RequireNativeBrandAsync(row.BrandId!.Value, ct);
        admitted = true;
        DateTime now = await ClockAsync(Data.Set<Entities.Job>().Where(value => value.Id == row.Id), ct);
        row.Status = "leased"; row.LockedBy = worker.ToString("D"); row.LeaseUntil = now.AddMinutes(1); row.Attempts = checked(row.Attempts + 1);
        return new(row.Id, JobIntent.Telegram(row.BrandId!.Value, row.TelegramDeliveryId!.Value), worker, row.Attempts, row.LeaseUntil.Value);
    }
}
internal static class PaymentMessageQueries
{
    internal static Task<bool> ChangeAsync(DbSession session, BrandContext context, Guid delivery, JobLease lease, Action<Entities.TelegramDelivery, Entities.Job, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) =>
    {
        var row = await data.Data.Set<Entities.TelegramDelivery>().SingleAsync(value => value.Id == delivery && value.BrandId == context.BrandId && value.OrderId != null, token);
        await data.RequireOrderCommandAsync(context, row.OrderId!.Value, token);
        await data.LockJobAsync(lease.Id, token);
        var job = await data.Data.Set<Entities.Job>().SingleAsync(value => value.Id == lease.Id && value.TelegramDeliveryId == delivery, token);
        DateTime now = await data.BrandClockAsync(context.BrandId, token); change(row, job, now); return true;
    }, ct);
    internal static Task<Guid> CreateAsync(DbSession session, BrandContext context, Guid member, Guid order, string key, long? target, Guid? audit, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) =>
    {
        await data.RequireOrderCommandAsync(context, order, token);
        var prior = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(value => value.DedupeKey == key, token);
        if (prior is not null)
        {
            if (prior.BrandId != context.BrandId || prior.UserId != member || prior.OrderId != order) data.RejectScope("Payment message identity conflict.");
            return prior.Id;
        }
        Guid id = Guid.CreateVersion7(); data.Data.Add(new Entities.TelegramDelivery { Id = id, BrandId = context.BrandId, RecipientKind = "customer_dm", UserId = member, Purpose = "order_status", Action = target is null ? "send" : "edit", TargetMessageId = target, DedupeKey = key, OrderId = order, AuditEventId = audit }); return id;
    }, ct);
}
