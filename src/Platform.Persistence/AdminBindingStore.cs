using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public static class AdminBindingStore
{
    public static async Task<AdminToken?> BeginAsync(DbSession session, AuthenticatedAdmin actor, long candidateTelegramId,
        TimeSpan lifetime, Guid commandId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (candidateTelegramId <= 0 || lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromMinutes(5) ||
            lifetime.Ticks % TimeSpan.TicksPerSecond != 0) throw new ArgumentException("Invalid binding challenge request.");
        byte[] commitment = Metadata(candidateTelegramId, checked((long)lifetime.TotalSeconds));
        var command = new AuditCommand(commandId, AuditAction.Update, AuditEntity.Admin, actor.AdminId, null,
            AuditReason.TelegramPossession, correlationId, commitment, actor.AuthVersion, actor.AuthVersion);
        if (!await AdminAuditStore.AppendAsync(session, actor, command, cancellationToken)) return null;
        var nonce = AdminToken.Create();
        byte[] hash = AdminAuth.BindingHash(nonce, actor.AdminId, actor.AuthVersion, candidateTelegramId, false);
        try
        {
            await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.admins SET binding_challenge_hash=$2,binding_candidate_telegram_id=$3,
                    binding_expires_at=clock_timestamp()+$4*INTERVAL '1 second' WHERE id=$1
                """, DbValue.Uuid(actor.AdminId), DbValue.Binary(hash), DbValue.Bigint(candidateTelegramId),
                DbValue.Integer(checked((int)lifetime.TotalSeconds))), cancellationToken);
            return nonce;
        }
        catch { nonce.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    // Only the verified private-ingress adapter supplies proof. This does not authenticate Telegram transport itself.
    public static Task<bool> ObservePrivateProofAsync(DbSession session, Guid adminId, AdminToken nonce,
        PrivateTelegramProof proof, CancellationToken cancellationToken = default) => ObserveProofCoreAsync(session, adminId, nonce, proof, null, null, cancellationToken);
    public static async Task<bool> ObservePrivateProofForBrandAsync(DbSession session, BrandContext context, long expectedRevision, Guid adminId, AdminToken nonce,
        PrivateTelegramProof proof, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            return await ObserveProofCoreAsync(session, adminId, nonce, proof, context, expectedRevision, cancellationToken);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static async Task<bool> ObserveProofCoreAsync(DbSession session, Guid adminId, AdminToken nonce,
        PrivateTelegramProof proof, BrandContext? context, long? writerRevision, CancellationToken cancellationToken)
    {
        var pending = await PendingAsync(session, adminId, cancellationToken);
        if (pending is null || pending.TelegramId != proof.UserId) return false;
        byte[] nonceHash = nonce.Hash();
        byte[] initial = AdminAuth.BindingHash(nonce, adminId, pending.Version, proof.UserId, false);
        byte[] confirmed = AdminAuth.BindingHash(nonce, adminId, pending.Version, proof.UserId, true);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(nonceHash, proof.NonceHash) ||
                !CryptographicOperations.FixedTimeEquals(initial, pending.Hash)) return false;
            if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, context!, revision, cancellationToken);
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.admins SET binding_challenge_hash=$2 WHERE id=$1 AND binding_expires_at>clock_timestamp()
                    AND disabled_at IS NULL AND auth_version=$3 AND binding_challenge_hash=$4
                """, DbValue.Uuid(adminId), DbValue.Binary(confirmed), DbValue.Bigint(pending.Version), DbValue.Binary(initial)), cancellationToken);
            if (changed == 0) return false;
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.audit_events(id,actor_kind,action,entity_kind,entity_id,reason,correlation_id)
                VALUES ($1,'system','update','admin',$2,'telegram_private_proof',$3)
                """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(adminId), DbValue.Uuid(Guid.NewGuid())), cancellationToken);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonceHash);
            CryptographicOperations.ZeroMemory(initial);
            CryptographicOperations.ZeroMemory(confirmed);
        }
    }

    public static async Task<bool> CompleteAsync(DbSession session, AuthenticatedAdmin actor, long candidateTelegramId,
        long expectedAuthVersion, AdminToken nonce, Guid commandId, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (expectedAuthVersion < 0 || expectedAuthVersion == long.MaxValue || candidateTelegramId <= 0)
            throw new ArgumentException("Invalid binding completion request.");
        byte[] expected = AdminAuth.BindingHash(nonce, actor.AdminId, expectedAuthVersion, candidateTelegramId, true);
        try
        {
            var command = new AuditCommand(commandId, AuditAction.BindTelegram, AuditEntity.Admin, actor.AdminId, null,
                AuditReason.TelegramPossession, correlationId, expected, expectedAuthVersion, expectedAuthVersion + 1);
            if (!await AdminAuditStore.AppendAsync(session, actor, command, cancellationToken)) return false;
            var pending = await PendingAsync(session, actor.AdminId, cancellationToken);
            if (pending is null || pending.Version != expectedAuthVersion || pending.TelegramId != candidateTelegramId ||
                !CryptographicOperations.FixedTimeEquals(expected, pending.Hash)) AdminSessionStore.Reject(session);
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.admins SET telegram_user_id=$2,telegram_verified_by_admin_id=$1,telegram_verified_at=clock_timestamp(),
                    telegram_verification_audit_id=$3,public_contact_handle=NULL,auth_version=auth_version+1,
                    binding_challenge_hash=NULL,binding_candidate_telegram_id=NULL,binding_expires_at=NULL
                WHERE id=$1 AND auth_version=$4 AND disabled_at IS NULL AND binding_expires_at>clock_timestamp()
                """, DbValue.Uuid(actor.AdminId), DbValue.Bigint(candidateTelegramId), DbValue.Uuid(commandId), DbValue.Bigint(expectedAuthVersion)), cancellationToken);
            if (changed != 1) AdminSessionStore.Reject(session);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    public static Task<bool> IsVerifiedStaffAsync(DbSession session, long telegramUserId, CancellationToken cancellationToken = default) =>
        session.QuerySingleAsync(new DbStatement("""
            WITH staff AS (SELECT id FROM shop.admins WHERE telegram_user_id=$1 AND disabled_at IS NULL
                AND telegram_verified_at IS NOT NULL AND telegram_verification_audit_id IS NOT NULL FOR SHARE)
            SELECT EXISTS(SELECT 1 FROM staff)
            """, DbValue.Bigint(telegramUserId)), row => row.GetBoolean(0), cancellationToken);

    private static Task<Pending?> PendingAsync(DbSession session, Guid adminId, CancellationToken cancellationToken) =>
        session.QuerySingleAsync<Pending?>(new DbStatement("""
            WITH locked AS (SELECT id,auth_version,binding_candidate_telegram_id,binding_challenge_hash FROM shop.admins
                WHERE id=$1 AND disabled_at IS NULL AND binding_expires_at>clock_timestamp() FOR UPDATE)
            SELECT a.id,a.auth_version,a.binding_candidate_telegram_id,a.binding_challenge_hash
            FROM (SELECT 1) singleton LEFT JOIN locked a ON true
            """, DbValue.Uuid(adminId)), row => row.IsDBNull(0) ? null : new Pending(row.GetInt64(1), row.GetInt64(2), row.GetBinary(3)), cancellationToken);

    internal static byte[] Metadata(long first, long second)
    {
        byte[] bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, first);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8), second);
        return SHA256.HashData(bytes);
    }
    private sealed record Pending(long Version, long TelegramId, byte[] Hash);
}
