using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
        byte[] commitment = audit.Fingerprint(actor.AdminId);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using (var prior = SqliteWallet.Command(tx, """
                SELECT command_sha256 FROM audit_events WHERE id=$command
                """, ("$command", command)))
            {
                object? previous = await prior.ExecuteScalarAsync(token);
                if (previous is byte[] earlier)
                {
                    if (!CryptographicOperations.FixedTimeEquals(earlier, commitment))
                        throw new UnauthorizedAccessException("Administrator coupon command identity conflict.");
                    return checked(revision + 1);
                }
            }
            if (edit.MemberId is Guid member)
            {
                using var scope = SqliteWallet.Command(tx, """
                    SELECT 1 FROM users WHERE id=$member AND brand_id=$brand
                    """, ("$member", member), ("$brand", context.BrandId));
                if (await scope.ExecuteScalarAsync(token) is null)
                    throw new UnauthorizedAccessException("Coupon member is outside this brand.");
            }
            if (edit.ProductId is Guid product)
            {
                using var lookup = SqliteWallet.Command(tx, "SELECT 1 FROM products WHERE id=$product",
                    ("$product", product));
                if (await lookup.ExecuteScalarAsync(token) is null)
                    throw new UnauthorizedAccessException("Coupon product is unavailable.");
            }
            long redeemed;
            using (var current = SqliteWallet.Command(tx, """
                SELECT d.revision,d.redeemed_count FROM discounts d JOIN brands b ON b.id=d.brand_id
                WHERE d.id=$coupon AND b.id=$brand AND b.bot_id=$bot AND b.active=1
                """, ("$coupon", coupon), ("$brand", context.BrandId), ("$bot", context.BotId)))
            await using (var row = await current.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || row.GetInt64(0) != revision)
                    throw new InvalidOperationException("Coupon revision conflict.");
                redeemed = row.GetInt64(1);
            }
            long adjustment = checked(edit.EffectiveUsed - redeemed);
            string? starts = edit.StartsAt is DateTime startAt ? SqliteSchema.UtcText(startAt) : null;
            string? ends = edit.EndsAt is DateTime endAt ? SqliteSchema.UtcText(endAt) : null;
            string percent = ExactDecimal.Format(edit.Percent);
            using (var eventWrite = SqliteWallet.Command(tx, """
                INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,
                    reason,previous_revision,new_revision,correlation_id,command_sha256)
                VALUES ($id,'admin',$admin,$brand,'coupon_edit','discount',$coupon,
                    $reason,$previous,$next,$id,$commitment)
                """, ("$id", command), ("$admin", actor.AdminId),
                ("$brand", context.BrandId), ("$coupon", coupon),
                ("$reason", AdminAuditStore.ReasonCode(reason)), ("$previous", revision),
                ("$next", checked(revision + 1)), ("$commitment", commitment)))
                if (await eventWrite.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Coupon audit write was not persisted.");
            using (var change = SqliteWallet.Command(tx, """
                UPDATE discounts SET percent_off=$percent,max_total_uses=$maximum,
                    max_uses_per_user=$per_user,usage_adjustment_units=$adjustment,starts_at=$starts,
                    ends_at=$ends,active=$active,product_id=$product,eligible_user_id=$member,
                    revision=revision+1
                WHERE id=$coupon AND brand_id=$brand AND revision=$revision
                    AND (percent_off IS NOT $percent OR max_total_uses IS NOT $maximum
                        OR max_uses_per_user IS NOT $per_user OR usage_adjustment_units IS NOT $adjustment
                        OR starts_at IS NOT $starts OR ends_at IS NOT $ends OR active IS NOT $active
                        OR product_id IS NOT $product OR eligible_user_id IS NOT $member)
                """, ("$percent", percent), ("$maximum", edit.MaximumUses),
                ("$per_user", edit.MaximumPerMember), ("$adjustment", adjustment),
                ("$starts", starts), ("$ends", ends), ("$active", edit.Active),
                ("$product", edit.ProductId), ("$member", edit.MemberId),
                ("$coupon", coupon), ("$brand", context.BrandId), ("$revision", revision)))
                if (await change.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Coupon edit has no changes.");
            using var snapshot = SqliteWallet.Command(tx, """
                INSERT INTO discount_revisions(id,discount_id,revision_no,audit_event_id,percent_off,
                    max_total_uses,max_uses_per_user,usage_adjustment_units,redeemed_count_at_revision,
                    held_units_at_revision,starts_at,ends_at,active,product_id,eligible_user_id)
                SELECT $id,id,revision,$audit,percent_off,max_total_uses,max_uses_per_user,
                    usage_adjustment_units,redeemed_count,
                    (SELECT coalesce(sum(units_covered),0) FROM discount_uses
                        WHERE discount_id=$coupon AND status='held'),
                    starts_at,ends_at,active,product_id,eligible_user_id
                FROM discounts WHERE id=$coupon AND brand_id=$brand
                """, ("$id", Guid.CreateVersion7()), ("$audit", command),
                ("$coupon", coupon), ("$brand", context.BrandId));
            if (await snapshot.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Coupon edit revision was not persisted.");
            return checked(revision + 1);
        }, ct);
    }
}
