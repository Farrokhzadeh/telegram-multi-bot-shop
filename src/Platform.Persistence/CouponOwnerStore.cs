using System.Security.Cryptography;
using Platform.Security;

namespace Platform.Persistence;

public sealed record CouponOwnerEdit(decimal Percent, long? MaximumUses, long? MaximumPerMember, long EffectiveUsed, DateTime? StartsAt, DateTime? EndsAt,
    bool Active, Guid? ProductId, Guid? MemberId);
public sealed partial class PricingStore
{
    public async Task<long> EditAsync(BrandContext context, AuthenticatedAdmin actor, Guid coupon, long revision, CouponOwnerEdit edit,
        AuditReason reason, Guid command, CancellationToken ct = default)
    {
        if (coupon == Guid.Empty || revision < 0 || revision == long.MaxValue || edit.Percent is < 0 or > 100 || decimal.Round(edit.Percent, 2) != edit.Percent ||
            edit.MaximumUses < 0 || edit.MaximumPerMember < 0 || edit.EffectiveUsed < 0 || edit.ProductId == Guid.Empty || edit.MemberId == Guid.Empty ||
            edit.StartsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || edit.EndsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || edit.EndsAt <= edit.StartsAt)
            throw new ArgumentException("Invalid coupon edit.");
        byte[] hash;
        using (var stream = new MemoryStream())
        {
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                w.Write("coupon-owner-edit-v1"); w.Write(edit.Percent); w.Write(edit.MaximumUses.HasValue); if (edit.MaximumUses is long max) w.Write(max);
                w.Write(edit.MaximumPerMember.HasValue); if (edit.MaximumPerMember is long per) w.Write(per);
                w.Write(edit.EffectiveUsed); w.Write(edit.StartsAt.HasValue); if (edit.StartsAt is DateTime start) w.Write(start.Ticks);
                w.Write(edit.EndsAt.HasValue); if (edit.EndsAt is DateTime end) w.Write(end.Ticks);
                w.Write(edit.Active); w.Write((edit.ProductId ?? Guid.Empty).ToByteArray()); w.Write((edit.MemberId ?? Guid.Empty).ToByteArray());
            }
            hash = SHA256.HashData(stream.ToArray());
        }
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.CouponEdit, AuditEntity.Discount, coupon, context.BrandId, reason, command, hash, revision, revision + 1), ct)) return revision + 1;
            if (edit.MemberId is Guid member)
            {
                long exists = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 FOR UPDATE) u", DbValue.Uuid(member), DbValue.Uuid(context.BrandId)), r => r.GetInt64(0), ct);
                if (exists != 1) throw new UnauthorizedAccessException("Coupon member is outside this brand.");
            }
            if (edit.ProductId is Guid product) await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.products WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(product)), r => r.GetInt64(0), ct);
            await LockBrandAsync(session, context, ct);
            var current = await session.QuerySingleAsync(new DbStatement("SELECT revision,redeemed_count FROM shop.discounts WHERE id=$1 AND brand_id=$2 FOR UPDATE", DbValue.Uuid(coupon), DbValue.Uuid(context.BrandId)), r => (Revision: r.GetInt64(0), Redeemed: r.GetInt64(1)), ct);
            if (current.Revision != revision) throw new InvalidOperationException("Coupon revision conflict.");
            long adjustment = checked(edit.EffectiveUsed - current.Redeemed);
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.discounts SET percent_off=$1,max_total_uses=$2,max_uses_per_user=$3,usage_adjustment_units=$4,starts_at=$5,ends_at=$6,
                    active=$7,product_id=$8,eligible_user_id=$9,revision=revision+1 WHERE id=$10 AND
                    ROW(percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,starts_at,ends_at,active,product_id,eligible_user_id)
                    IS DISTINCT FROM ROW($1::numeric,$2::bigint,$3::bigint,$4::bigint,$5::timestamptz,$6::timestamptz,$7::boolean,$8::uuid,$9::uuid)
                """, DbValue.Numeric(edit.Percent), Number(edit.MaximumUses), Number(edit.MaximumPerMember), DbValue.Bigint(adjustment), Time(edit.StartsAt), Time(edit.EndsAt),
                DbValue.Boolean(edit.Active), Uuid(edit.ProductId), Uuid(edit.MemberId), DbValue.Uuid(coupon)), ct);
            if (changed != 1) throw new InvalidOperationException("Coupon edit has no changes.");
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,
                    redeemed_count_at_revision,held_units_at_revision,starts_at,ends_at,active,product_id,eligible_user_id)
                SELECT gen_random_uuid(),id,revision,$1,percent_off,max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count,
                    (SELECT COALESCE(sum(units_covered),0) FROM shop.discount_uses WHERE discount_id=$2 AND status='held'),starts_at,ends_at,active,product_id,eligible_user_id
                FROM shop.discounts WHERE id=$2
                """, DbValue.Uuid(command), DbValue.Uuid(coupon)), ct);
            await session.CommitAsync(ct);
            return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
