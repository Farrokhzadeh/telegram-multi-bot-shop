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
            await session.RunEfStepAsync(false, async (data, token) =>
            {
                if (edit.MemberId is Guid member && await data.LockUserAsync(member, context.BrandId, token) != 1) data.RejectScope("Coupon member is outside this brand.");
                if (edit.ProductId is Guid product) await data.LockProductAsync(product, token);
                return true;
            }, ct);
            await LockBrandAsync(session, context, ct);
            await session.RunEfStepAsync(true, (data, token) => CouponEdits.WriteAsync(data, context, actor, coupon, revision, edit, command, token), ct);
            await session.CommitAsync(ct);
            return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
