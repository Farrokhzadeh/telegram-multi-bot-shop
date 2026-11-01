using Platform.Security;

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
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,previous_revision,new_revision,created_at FROM shop.audit_events
            WHERE ($1 IS NULL OR brand_id=$1) AND ($2 IS NULL OR id>$2) ORDER BY id LIMIT $3
            """, AdminCommand.Id(brand), AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new AuditMetadata(r.GetGuid(0), r.GetString(1), Id(r, 2), Id(r, 3), r.GetString(4), r.GetString(5), Id(r, 6), Reason(r.GetString(7)), Number(r, 8), Number(r, 9), r.GetUtc(10)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<WorkBacklog[]> BacklogAsync(AuthenticatedAdmin actor, Guid? brand, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT job_kind,status,count(*),min(available_at),count(*) FILTER(WHERE status='leased' AND lease_until<=clock_timestamp())
            FROM shop.jobs WHERE ($1 IS NULL OR brand_id=$1) GROUP BY job_kind,status ORDER BY job_kind,status
            """, AdminCommand.Id(brand)), r => new WorkBacklog(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetUtc(3), r.GetInt64(4)), 1000, ct);
        return rows.ToArray();
    }
    public async Task<AdminPage<CampaignMetadata>> CampaignsAsync(AuthenticatedAdmin actor, Guid? brand, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("SELECT id,batch_id,brand_id,language,status,created_at FROM shop.campaigns WHERE ($1 IS NULL OR brand_id=$1) AND ($2 IS NULL OR id>$2) ORDER BY id LIMIT $3",
            AdminCommand.Id(brand), AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new CampaignMetadata(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetUtc(5)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<AdminPage<CouponMetadata>> CouponsAsync(AuthenticatedAdmin actor, Guid brand, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT id,brand_id,kind,display_prefix,percent_off::text,max_total_uses,GREATEST(0,redeemed_count::numeric+usage_adjustment_units)::text,
                (SELECT COALESCE(sum(units_covered),0)::text FROM shop.discount_uses WHERE discount_id=d.id AND status='held'),active,ends_at,revision,source_order_id
            FROM shop.discounts d WHERE brand_id=$1 AND ($2 IS NULL OR id>$2) ORDER BY id LIMIT $3
            """, DbValue.Uuid(brand), AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new CouponMetadata(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), Number(r, 5), r.GetString(6), r.GetString(7), r.GetBoolean(8), Time(r, 9), r.GetInt64(10), Id(r, 11)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<CouponRevisionPage> RevisionsAsync(AuthenticatedAdmin actor, Guid brand, Guid coupon, long? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT r.id,r.revision_no,r.percent_off::text,r.max_total_uses,r.max_uses_per_user,r.usage_adjustment_units,r.redeemed_count_at_revision,r.held_units_at_revision,
                r.starts_at,r.ends_at,r.active,r.product_id,r.eligible_user_id,a.actor_admin_id,a.reason,a.created_at
            FROM shop.discount_revisions r JOIN shop.discounts d ON d.id=r.discount_id JOIN shop.audit_events a ON a.id=r.audit_event_id
            WHERE d.brand_id=$1 AND d.id=$2 AND ($3 IS NULL OR r.revision_no>$3) ORDER BY r.revision_no LIMIT $4
            """, DbValue.Uuid(brand), DbValue.Uuid(coupon), after is long revision ? DbValue.Bigint(revision) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Integer(limit + 1)),
            r => new CouponRevisionMetadata(r.GetGuid(0), r.GetInt64(1), r.GetString(2), Number(r, 3), Number(r, 4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), Time(r, 8), Time(r, 9), r.GetBoolean(10), Id(r, 11), Id(r, 12), Id(r, 13), Reason(r.GetString(14)), r.GetUtc(15)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Revision : null);
    }
    private static string Reason(string value) => value is "operator_review" or "configuration_change" or "credential_rotation" or "account_revocation" or "telegram_possession" or "reconciliation" or "customer_request" or "paid_source_entitlement" ? value : "operator_review";
    private static Guid? Id(DbRow row, int index) => row.IsDBNull(index) ? null : row.GetGuid(index);
    private static long? Number(DbRow row, int index) => row.IsDBNull(index) ? null : row.GetInt64(index);
    private static DateTime? Time(DbRow row, int index) => row.IsDBNull(index) ? null : row.GetUtc(index);
}
