using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public sealed record BrandWriterState(bool Active, long Revision);
public sealed class BrandWriterStore(DbSessionFactory sessions)
{
    public static async Task RequireAsync(DbSession session, BrandContext context, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        bool allowed = await session.QuerySingleAsync(new DbStatement("""
            WITH locked AS MATERIALIZED (SELECT active,policy_version FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE)
            SELECT COALESCE((SELECT active AND policy_version=$3 FROM locked),false)
            """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(expectedRevision)), r => r.GetBoolean(0), ct);
        if (!allowed) Deny(session);
    }

    public async Task<BrandWriterState> ReadAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var state = await session.QuerySingleAsync<BrandWriterState?>(new DbStatement("""
            SELECT b.id,b.active,b.policy_version FROM (SELECT 1) seed LEFT JOIN shop.brands b ON b.id=$1 AND b.bot_id=$2
            """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.IsDBNull(0) ? null : new(r.GetBoolean(1), r.GetInt64(2)), ct);
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
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.brands SET active=$1,policy_version=policy_version+1,updated_at=clock_timestamp()
                WHERE id=$2 AND bot_id=$3 AND policy_version=$4 AND active<>$1
                """, DbValue.Boolean(active), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(expectedRevision)), ct);
            if (changed != 1) Deny(session);
            await session.CommitAsync(ct);
            return new(active, expectedRevision + 1);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    private static void Deny(DbSession session)
    { session.RejectApplication(); throw new UnauthorizedAccessException("Brand writer is unavailable or changed."); }
}
