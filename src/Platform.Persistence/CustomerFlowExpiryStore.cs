using System.Buffers.Binary;
using System.Security.Cryptography;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class CustomerFlowExpiryStore(DbSessionFactory sessions)
{
    public async Task<bool> ExpireAsync(BrandContext context, Guid memberId, long expectedRevision, CancellationToken ct = default)
    {
        if (memberId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("Invalid flow expiry identity.");
        await using var session = await sessions.BeginAsync(ct);
        var original = await session.QuerySingleAsync(new DbStatement("""
            SELECT u.telegram_id,u.flow_revision,u.flow_expires_at,u.flow_product_id,u.flow_order_id FROM (SELECT 1) seed
            LEFT JOIN (SELECT u.* FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id
                WHERE u.id=$1 AND u.brand_id=$2 AND b.bot_id=$3 AND b.active AND u.blocked_at IS NULL AND u.banned_at IS NULL
                    AND u.flow_state<>'idle' AND u.flow_expires_at<CURRENT_TIMESTAMP AND u.flow_revision=$4 FOR UPDATE OF u) u ON true
            """, DbValue.Uuid(memberId), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(expectedRevision)),
            r => r.IsDBNull(0) ? null : new Original(r.GetInt64(0), r.GetInt64(1), r.GetUtc(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4)), ct);
        if (original is null) return false;
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.users SET flow_state='idle',flow_revision=flow_revision+1,flow_product_id=NULL,flow_order_id=NULL,flow_anchor_message_id=NULL,flow_expires_at=NULL
            WHERE id=$1 AND flow_revision=$2 AND flow_expires_at=$3
            """, DbValue.Uuid(memberId), DbValue.Bigint(expectedRevision), DbValue.Utc(original.At)), ct);
        if (changed != 1) { session.RejectApplication(); throw new InvalidOperationException("Flow expiry changed concurrently."); }
        await BotActivityWriter.RecordAsync(session, context, original.Actor, memberId, BotActivityAction.FlowExpired, BotActivitySource.System, BotActivityOutcome.Cancelled,
            new(original.Product, original.Order, FlowRevision: original.Revision), Identity(memberId, expectedRevision), original.At, ct);
        await session.CommitAsync(ct);
        return true;
    }
    internal static Guid Identity(Guid member, long revision)
    {
        byte[] identity = new byte[24]; member.TryWriteBytes(identity.AsSpan(0, 16)); BinaryPrimitives.WriteInt64BigEndian(identity.AsSpan(16), revision);
        return new Guid(SHA256.HashData(identity).AsSpan(0, 16));
    }
    private sealed record Original(long Actor, long Revision, DateTime At, Guid? Product, Guid? Order);
}
