using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record BrandWriterState(bool Active, long Revision);
public sealed class BrandWriterStore(DbSessionFactory sessions)
{
    public static async Task RequireAsync(DbSession session, BrandContext context, long expectedRevision, CancellationToken ct = default)
    {
        session.ValidateWriter(context, expectedRevision);
        ArgumentNullException.ThrowIfNull(context);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await session.RunEfStepAsync(false, async (data, token) => { await data.RequireRegisteredRevisionAsync(context, expectedRevision, token); return true; }, ct);
    }

    public static async Task RequireCustomerAsync(DbSession session, BrandContext context, long actor, long expectedRevision, Guid? product = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            await session.RunEfStepAsync(false, async (data, token) =>
            {
                await data.LockMemberAsync(context.BrandId, actor, token);
                if (!await (from member in data.Data.Set<Entities.User>()
                            join owner in data.Data.Set<Entities.Brand>() on member.BrandId equals owner.Id
                            where owner.Id == context.BrandId && owner.BotId == context.BotId && owner.Active && member.TelegramId == actor
                            select member.Id).AnyAsync(token))
                    data.RejectScope("Customer membership is unavailable.");
                if (product is Guid id) await data.LockProductAsync(id, token);
                return true;
            }, ct);
            await RequireAsync(session, context, expectedRevision, ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<BrandWriterState> ReadAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var state = await session.RunEfStepAsync(false, async (data, token) => await data.Data.Set<Entities.Brand>()
            .Where(value => value.Id == context.BrandId && value.BotId == context.BotId).Select(value => new BrandWriterState(value.Active, value.PolicyVersion)).SingleOrDefaultAsync(token), ct);
        if (state is null) Deny(session);
        return state!;
    }

    public async Task<BrandWriterState> SetActiveAsync(AuthenticatedAdmin actor, BrandContext context, long expectedRevision,
        bool active, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        byte[] commitment = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"brand-writer-control-v1:{context.BotId}:{context.DeploymentId:N}:{context.ConfigurationRevision}")));
        var audit = new AuditCommand(command, active ? AuditAction.Enable : AuditAction.Disable, AuditEntity.Brand, context.BrandId,
            context.BrandId, AuditReason.ConfigurationChange, command, commitment, expectedRevision, expectedRevision + 1);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, audit, ct)) return new(active, expectedRevision + 1);
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireAdminAsync(actor, null, token);
                await data.LockBrandAsync(context.BrandId, token);
                var brand = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.PolicyVersion == expectedRevision && value.Active != active, token);
                if (brand is null) data.RejectScope("Brand writer is unavailable or changed.");
                brand!.Active = active;
                brand.PolicyVersion++;
                brand.UpdatedAt = await data.BrandClockAsync(context.BrandId, token);
                return true;
            }, ct);
            await session.CommitAsync(ct);
            return new(active, expectedRevision + 1);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    private static void Deny(DbSession session)
    { session.RejectApplication(); throw new UnauthorizedAccessException("Brand writer is unavailable or changed."); }
}
