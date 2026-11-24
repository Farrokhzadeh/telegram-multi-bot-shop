using System.Buffers.Binary;
using System.Security.Cryptography;
using Platform.Core;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public sealed class CustomerFlowExpiryStore(DbSessionFactory sessions)
{
    public async Task<bool> ExpireAsync(BrandContext context, Guid memberId, long expectedRevision, CancellationToken ct = default)
    {
        if (memberId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("Invalid flow expiry identity.");
        await using var session = await sessions.BeginAsync(ct);
        var original = await session.RunEfStepAsync(false, async (data, token) => { await data.LockUserAsync(memberId, context.BrandId, token); return await (from u in data.Data.Set<Entities.User>() join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where u.Id == memberId && u.BrandId == context.BrandId && b.BotId == context.BotId && b.Active && u.BlockedAt == null && u.BannedAt == null && u.FlowState != "idle" && u.FlowExpiresAt < EfValues.TransactionTime() && u.FlowRevision == expectedRevision select new Original(u.TelegramId, u.FlowRevision, u.FlowExpiresAt!.Value, u.FlowProductId, u.FlowOrderId)).SingleOrDefaultAsync(token); }, ct);
        if (original is null) return false;
        await session.AdmitCustomerWriterAsync(context, original.Actor, original.Product, ct);
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireMembershipAsync(context, original.Actor, false, false, token); var row = await data.Data.Set<Entities.User>().SingleOrDefaultAsync(row => row.Id == memberId && row.FlowRevision == expectedRevision && row.FlowExpiresAt == original.At, token); if (row is null) data.RejectConflict("Flow expiry changed concurrently."); row.FlowState = "idle"; row.FlowRevision++; row.FlowProductId = null; row.FlowOrderId = null; row.FlowAnchorMessageId = null; row.FlowExpiresAt = null; return true; }, ct);
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
