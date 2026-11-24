using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal Task<int> LockRequestAsync(Guid request, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.SupplierRequest>().Where(value => value.Id == request).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockSupplierJobsAsync(Guid request, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Job>().Where(value => value.SupplierRequestId == request && (value.Status == "pending" || value.Status == "retry")).OrderBy(value => value.Id).Select(value => value.Id), EfRowLock.Update, ct);
}
internal static class SupplierQueries
{
    internal static Task<bool> ChangeAsync(DbSession session, BrandContext context, Guid request, Action<Entities.SupplierRequest, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) =>
    {
        await data.LockRequestAsync(request, token);
        var row = await data.Data.Set<Entities.SupplierRequest>().SingleAsync(value => value.Id == request, token);
        await data.RequireOrderCommandAsync(context, row.OrderId, token);
        DateTime now = await data.BrandClockAsync(context.BrandId, token); change(row, now); return true;
    }, ct);
    internal static Task<bool> OrderAsync(DbSession session, BrandContext context, Guid order, Action<Entities.Order, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) =>
    {
        await data.RequireOrderCommandAsync(context, order, token);
        var row = await data.Data.Set<Entities.Order>().SingleAsync(value => value.Id == order, token);
        DateTime now = await data.BrandClockAsync(context.BrandId, token); change(row, now); return true;
    }, ct);
    internal static Task<bool> AttemptAsync(DbSession session, BrandContext context, Guid attempt, Action<Entities.SupplierRequestAttempt, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) =>
    {
        var row = await data.Data.Set<Entities.SupplierRequestAttempt>().SingleAsync(value => value.Id == attempt, token);
        Guid order = await data.Data.Set<Entities.SupplierRequest>().Where(value => value.Id == row.SupplierRequestId).Select(value => value.OrderId).SingleAsync(token);
        await data.RequireOrderCommandAsync(context, order, token);
        DateTime now = await data.BrandClockAsync(context.BrandId, token); change(row, now); return true;
    }, ct);

}

internal sealed partial class EfSession
{
    internal Task<int> LockFulfillmentAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Fulfillment>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
}

internal sealed partial class EfSession
{
    internal Task<int> LockOrderPartsAsync(Guid order, int offset, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(offset).Take(1000).Select(row => row.Id), EfRowLock.Update, ct);
    internal Task<int> LockDeliveryJobsAsync(System.Linq.Expressions.Expression<Func<Entities.Job, bool>> predicate, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Job>().Where(predicate).OrderBy(row => row.Id).Select(row => row.Id), EfRowLock.Update, ct);
}
