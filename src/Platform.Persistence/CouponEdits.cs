using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

internal static class CouponEdits
{
    internal static async Task<long> WriteAsync(EfSession data, BrandContext context, AuthenticatedAdmin actor, Guid coupon, long revision, CouponOwnerEdit edit, Guid audit, CancellationToken ct)
    {
        await data.RequireAdminAsync(actor, null, ct);
        bool sqlite = data.Data is SqliteAkantiDbContext;
        if (sqlite && !await data.Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct)) data.RejectScope("Coupon brand is unavailable.");
        if (edit.MemberId is Guid member && !await data.Data.Set<Entities.User>().AnyAsync(value => value.Id == member && value.BrandId == context.BrandId, ct)) data.RejectScope("Coupon member is outside this brand.");
        if (sqlite && edit.ProductId is Guid product && !await data.Data.Set<Entities.Product>().AnyAsync(value => value.Id == product, ct)) data.RejectScope("Coupon product is unavailable.");
        await data.LockDiscountAsync(coupon, context.BrandId, ct);
        var row = await data.Data.Set<Entities.Discount>().SingleOrDefaultAsync(value => value.Id == coupon && value.BrandId == context.BrandId, ct);
        if (row is null || row.Revision != revision) data.RejectConflict("Coupon revision conflict.");
        long adjustment = checked(edit.EffectiveUsed - row.RedeemedCount);
        if (row.PercentOff == edit.Percent && row.MaxTotalUses == edit.MaximumUses && row.MaxUsesPerUser == edit.MaximumPerMember && row.UsageAdjustmentUnits == adjustment && row.StartsAt == edit.StartsAt && row.EndsAt == edit.EndsAt && row.Active == edit.Active && row.ProductId == edit.ProductId && row.EligibleUserId == edit.MemberId) data.RejectConflict("Coupon edit has no changes.");
        row.PercentOff = edit.Percent; row.MaxTotalUses = edit.MaximumUses; row.MaxUsesPerUser = edit.MaximumPerMember; row.UsageAdjustmentUnits = adjustment; row.StartsAt = edit.StartsAt; row.EndsAt = edit.EndsAt; row.Active = edit.Active; row.ProductId = edit.ProductId; row.EligibleUserId = edit.MemberId; row.Revision = checked(revision + 1);
        long held = checked((long)(await data.Data.Set<Entities.DiscountUse>().Where(value => value.DiscountId == coupon && value.Status == "held").SumAsync(value => (decimal?)value.UnitsCovered, ct) ?? 0m));
        await data.FlushAsync(ct, false);
        data.Data.Add(new Entities.DiscountRevision { Id = Guid.CreateVersion7(), DiscountId = coupon, RevisionNo = row.Revision, AuditEventId = audit, PercentOff = row.PercentOff, MaxTotalUses = row.MaxTotalUses, MaxUsesPerUser = row.MaxUsesPerUser, UsageAdjustmentUnits = row.UsageAdjustmentUnits, RedeemedCountAtRevision = row.RedeemedCount, HeldUnitsAtRevision = held, StartsAt = row.StartsAt, EndsAt = row.EndsAt, Active = row.Active, ProductId = row.ProductId, EligibleUserId = row.EligibleUserId });
        return row.Revision;
    }
}
