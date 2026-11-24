using Platform.Security;
using Platform.Core;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

namespace Platform.Persistence;

public sealed record AuditMetadata(Guid Id, string ActorKind, Guid? ActorId, Guid? BrandId, string Action, string Entity, Guid? EntityId, string Reason, long? Previous, long? Current, DateTime At);
public sealed record WorkBacklog(string Kind, string Status, long Count, DateTime? OldestAvailableAt, long ExpiredLeases);
public sealed record CampaignMetadata(Guid Id, Guid BatchId, Guid BrandId, string Language, string Status, DateTime CreatedAt);
public sealed record CouponMetadata(Guid Id, Guid BrandId, string Kind, string Prefix, string Percent, long? Maximum, string EffectiveUsed, string Held, bool Active, DateTime? EndsAt, long Revision, Guid? SourceOrderId);
public sealed record CouponRevisionMetadata(Guid Id, long Revision, string Percent, long? Maximum, long? MaximumPerMember, long Adjustment, long Redeemed, long Held, DateTime? StartsAt, DateTime? EndsAt, bool Active, Guid? ProductId, Guid? MemberId, Guid? ActorId, string Reason, DateTime At);
public sealed record CouponRevisionPage(CouponRevisionMetadata[] Items, long? Next);
public sealed class OperationsStore(DbSessionFactory sessions)
{
    public async Task<AdminPage<AuditMetadata>> AuditAsync(AuthenticatedAdmin actor, Guid? brand, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var raw = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.AuditEvent>().Where(row => (brand == null || row.BrandId == brand) && (after == null || row.Id.CompareTo(after.Value) > 0)).OrderBy(row => row.Id).Take(limit + 1).Select(row => new { row.Id, row.ActorKind, row.ActorAdminId, row.BrandId, row.Action, row.EntityKind, row.EntityId, row.Reason, row.PreviousRevision, row.NewRevision, row.CreatedAt }).ToListAsync(token), ct);
        var rows = raw.Select(row => new AuditMetadata(row.Id, row.ActorKind, row.ActorAdminId, row.BrandId, row.Action, row.EntityKind, row.EntityId, Reason(row.Reason), row.PreviousRevision, row.NewRevision, row.CreatedAt)).ToList();
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<WorkBacklog[]> BacklogAsync(AuthenticatedAdmin actor, Guid? brand, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Job>().Where(row => brand == null || row.BrandId == brand).GroupBy(row => new { row.JobKind, row.Status }).OrderBy(group => group.Key.JobKind).ThenBy(group => group.Key.Status).Select(group => new WorkBacklog(group.Key.JobKind, group.Key.Status, group.LongCount(), group.Min(row => (DateTime?)row.AvailableAt), group.LongCount(row => row.Status == "leased" && row.LeaseUntil <= EfValues.UtcNow()))).Take(1000).ToListAsync(token), ct);
        return rows.ToArray();
    }
    public async Task<AdminPage<CampaignMetadata>> CampaignsAsync(AuthenticatedAdmin actor, Guid? brand, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Campaign>().Where(row => (brand == null || row.BrandId == brand) && (after == null || row.Id.CompareTo(after.Value) > 0)).OrderBy(row => row.Id).Select(row => new CampaignMetadata(row.Id, row.BatchId, row.BrandId, row.Language, row.Status, row.CreatedAt)).Take(limit + 1).ToListAsync(token), ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<AdminPage<CouponMetadata>> CouponsAsync(AuthenticatedAdmin actor, Guid brand, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var raw = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Discount>().Where(row => row.BrandId == brand && (after == null || row.Id.CompareTo(after.Value) > 0)).OrderBy(row => row.Id).Take(limit + 1).Select(row => new { row.Id, row.BrandId, row.Kind, row.DisplayPrefix, row.PercentOff, row.MaxTotalUses, row.RedeemedCount, row.UsageAdjustmentUnits, Held = data.Data.Set<Entities.DiscountUse>().Where(use => use.DiscountId == row.Id && use.Status == "held").Sum(use => (decimal?)use.UnitsCovered) ?? 0m, row.Active, row.EndsAt, row.Revision, row.SourceOrderId }).ToListAsync(token), ct);
        var rows = raw.Select(row => new CouponMetadata(row.Id, row.BrandId, row.Kind, row.DisplayPrefix, ExactDecimal.Format(row.PercentOff), row.MaxTotalUses, ExactDecimal.Format(Math.Max(0m, (decimal)row.RedeemedCount + row.UsageAdjustmentUnits)), ExactDecimal.Format(row.Held), row.Active, row.EndsAt, row.Revision, row.SourceOrderId)).ToList();
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<CouponRevisionPage> RevisionsAsync(AuthenticatedAdmin actor, Guid brand, Guid coupon, long? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var raw = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.DiscountRevision>() join d in data.Data.Set<Entities.Discount>() on r.DiscountId equals d.Id join a in data.Data.Set<Entities.AuditEvent>() on r.AuditEventId equals a.Id where d.BrandId == brand && d.Id == coupon && (after == null || r.RevisionNo > after) orderby r.RevisionNo select new { r.Id, r.RevisionNo, r.PercentOff, r.MaxTotalUses, r.MaxUsesPerUser, r.UsageAdjustmentUnits, r.RedeemedCountAtRevision, r.HeldUnitsAtRevision, r.StartsAt, r.EndsAt, r.Active, r.ProductId, r.EligibleUserId, a.ActorAdminId, a.Reason, a.CreatedAt }).Take(limit + 1).ToListAsync(token), ct);
        var rows = raw.Select(row => new CouponRevisionMetadata(row.Id, row.RevisionNo, ExactDecimal.Format(row.PercentOff), row.MaxTotalUses, row.MaxUsesPerUser, row.UsageAdjustmentUnits, row.RedeemedCountAtRevision, row.HeldUnitsAtRevision, row.StartsAt, row.EndsAt, row.Active, row.ProductId, row.EligibleUserId, row.ActorAdminId, Reason(row.Reason), row.CreatedAt)).ToList();
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Revision : null);
    }
    private static string Reason(string value) => value is "operator_review" or "configuration_change" or "credential_rotation" or "account_revocation" or "telegram_possession" or "reconciliation" or "customer_request" or "paid_source_entitlement" ? value : "operator_review";
}
