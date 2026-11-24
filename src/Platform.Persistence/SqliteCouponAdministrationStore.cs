using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteCouponAdministrationStore(SqliteSessionFactory sessions)
{
    public Task<long> EditAsync(BrandContext context, AuthenticatedAdmin actor, Guid coupon, long revision,
        CouponOwnerEdit edit, AuditReason reason, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (coupon == Guid.Empty || command == Guid.Empty || revision < 0 || revision == long.MaxValue ||
            edit.Percent is < 0 or > 100 || decimal.Round(edit.Percent, 2) != edit.Percent ||
            edit.MaximumUses < 0 || edit.MaximumPerMember < 0 || edit.EffectiveUsed < 0 ||
            edit.ProductId == Guid.Empty || edit.MemberId == Guid.Empty ||
            edit.StartsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified ||
            edit.EndsAt?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified ||
            edit.EndsAt <= edit.StartsAt)
            throw new ArgumentException("Invalid coupon edit.");
        byte[] hash;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("coupon-owner-edit-v1");
                writer.Write(edit.Percent);
                writer.Write(edit.MaximumUses.HasValue);
                if (edit.MaximumUses is long max) writer.Write(max);
                writer.Write(edit.MaximumPerMember.HasValue);
                if (edit.MaximumPerMember is long per) writer.Write(per);
                writer.Write(edit.EffectiveUsed);
                writer.Write(edit.StartsAt.HasValue);
                if (edit.StartsAt is DateTime start) writer.Write(start.Ticks);
                writer.Write(edit.EndsAt.HasValue);
                if (edit.EndsAt is DateTime end) writer.Write(end.Ticks);
                writer.Write(edit.Active);
                writer.Write((edit.ProductId ?? Guid.Empty).ToByteArray());
                writer.Write((edit.MemberId ?? Guid.Empty).ToByteArray());
            }
            hash = SHA256.HashData(stream.ToArray());
        }
        var audit = new AuditCommand(command, AuditAction.CouponEdit, AuditEntity.Discount, coupon,
            context.BrandId, reason, command, hash, revision, checked(revision + 1));
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            if (!await AdminAuditStore.AppendEfAsync(data, actor, audit, token)) return checked(revision + 1);
            return await CouponEdits.WriteAsync(data, context, actor, coupon, revision, edit, command, token);
        }, ct);
    }
}
