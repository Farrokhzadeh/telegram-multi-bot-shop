using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal Task<int> LockUserAsync(Guid id, Guid brand, CancellationToken ct) => LockRowsAsync(Data.Set<User>().Where(value => value.Id == id && value.BrandId == brand).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockBrandUpdateAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Brand>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockListingAsync(Guid brand, Guid product, CancellationToken ct, bool shared = false) => LockRowsAsync(Data.Set<BrandProduct>().Where(value => value.BrandId == brand && value.ProductId == product).Select(value => value.ProductId), shared ? EfRowLock.Share : EfRowLock.Update, ct);
    internal Task<int> LockSourceAsync(Guid source, CancellationToken ct) => LockRowsAsync(Data.Set<ProductSupplierSource>().Where(value => value.Id == source).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockDiscountAsync(Guid discount, Guid brand, CancellationToken ct) => LockRowsAsync(Data.Set<Discount>().Where(value => value.Id == discount && value.BrandId == brand).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockOrderAsync(Guid order, CancellationToken ct) => LockRowsAsync(Data.Set<Order>().Where(value => value.Id == order).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockSubscriptionAsync(Guid user, Guid product, CancellationToken ct) => LockRowsAsync(Data.Set<ProductStockSubscription>().Where(value => value.UserId == user && value.ProductId == product && value.ClosedAt == null).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockSubscriptionDeliveriesAsync(Guid subscription, CancellationToken ct) => LockRowsAsync(Data.Set<TelegramDelivery>().Where(value => value.ProductStockSubscriptionId == subscription).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockDeliveryJobsAsync(Guid delivery, CancellationToken ct) => LockRowsAsync(Data.Set<Job>().Where(value => value.TelegramDeliveryId == delivery).OrderBy(value => value.Id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockDiscountsAsync(IQueryable<Guid> query, CancellationToken ct) => LockRowsAsync(query, EfRowLock.Update, ct);
    internal Task RequireBrandCommandAsync(Guid brand, CancellationToken ct) => RequireNativeBrandAsync(brand, ct);
    private async Task RequireNativeBrandAsync(Guid brand, CancellationToken ct)
    {
        if (!write || !nativeWriterBoundary) throw Denied("A native command transaction is required.");
        if (writer is not null)
        {
            var registration = writer.Entries.SingleOrDefault(value => value.Context.BrandId == brand);
            if (registration is null) throw Denied("Writer registration is unavailable.");
            await RequireRegisteredRevisionAsync(registration.Context, registration.Revision, ct);
        }
        admitted = true;
    }
    internal async Task RequireOrderCommandAsync(BrandContext context, Guid order, CancellationToken ct)
    {
        Guid? member = await (from value in Data.Set<Order>()
                              join user in Data.Set<User>() on value.UserId equals user.Id
                              join brand in Data.Set<Entities.Brand>() on user.BrandId equals brand.Id
                              where value.Id == order && brand.Id == context.BrandId && brand.BotId == context.BotId && (nativeWriterBoundary || brand.Active)
                              select (Guid?)user.Id).SingleOrDefaultAsync(ct);
        if (member is null) throw Denied("Order scope is unavailable.");
        if (nativeWriterBoundary) await RequireNativeBrandAsync(context.BrandId, ct);
        else await RequireWalletCommandAsync(member.Value, ct);
    }
    internal async Task RequireSourceCommandAsync(Guid source, CancellationToken ct)
    {
        if (!write || !nativeWriterBoundary) throw Denied("A native source transaction is required.");
        if (writer is { Shared: true })
        {
            if (!sharedAdmitted) throw Denied("Shared source admission is unavailable.");
        }
        else if (writer is not null)
        {
            var registration = writer.Entries[0];
            if (!await (from value in Data.Set<ProductSupplierSource>()
                        join listing in Data.Set<BrandProduct>() on value.ProductId equals listing.ProductId
                        where value.Id == source && listing.BrandId == registration.Context.BrandId
                        select value.Id).AnyAsync(ct))
                throw Denied("Source scope is unavailable.");
            await RequireNativeBrandAsync(registration.Context.BrandId, ct);
        }
        admitted = true;
    }
    internal async Task RequireFxLeaseAsync(JobLease lease, Guid brand, CancellationToken ct)
    {
        string worker = lease.Worker.ToString("D");
        if (!await Data.Set<Job>().AnyAsync(value => value.Id == lease.Id && value.BrandId == brand && value.JobKind == lease.Intent.Code &&
            value.Status == "leased" && value.LockedBy == worker && value.Attempts == lease.Attempt && value.LeaseUntil > EfValues.UtcNow(), ct))
            throw new DbOperationException(new(DbFailureKind.LeaseLost, DbRecovery.StopLeaseOwner));
        await RequireNativeBrandAsync(brand, ct);
    }
}
