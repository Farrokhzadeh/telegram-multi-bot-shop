using Platform.Security;

namespace Platform.Persistence;

public static class AdminRevocationStore
{
    public static async Task<bool> RevokeAsync(DbSession session, AuthenticatedAdmin actor, Guid targetAdminId,
        long expectedVersion, Guid commandId, Guid correlationId, bool disableAccount = false, bool revokeTelegram = false,
        CancellationToken cancellationToken = default)
    {
        if (targetAdminId == Guid.Empty || expectedVersion < 0 || expectedVersion == long.MaxValue)
            throw new ArgumentException("Invalid administrator revocation request.");
        var action = disableAccount ? AuditAction.Disable : revokeTelegram ? AuditAction.RevokeTelegram : AuditAction.Update;
        var command = new AuditCommand(commandId, action, AuditEntity.Admin, targetAdminId, null, AuditReason.AccountRevocation,
            correlationId, AdminBindingStore.Metadata(disableAccount ? 1 : 0, revokeTelegram ? 1 : 0), expectedVersion, expectedVersion + 1);
        if (!await AdminAuditStore.AppendAsync(session, actor, command, cancellationToken)) return false;
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.admins SET auth_version=auth_version+1,
                disabled_at=CASE WHEN $3 THEN clock_timestamp() ELSE disabled_at END,
                telegram_user_id=CASE WHEN $4 THEN NULL ELSE telegram_user_id END,
                telegram_verified_by_admin_id=CASE WHEN $4 THEN NULL ELSE telegram_verified_by_admin_id END,
                telegram_verified_at=CASE WHEN $4 THEN NULL ELSE telegram_verified_at END,
                telegram_verification_audit_id=CASE WHEN $4 THEN NULL ELSE telegram_verification_audit_id END,
                public_contact_handle=CASE WHEN $4 THEN NULL ELSE public_contact_handle END,
                binding_challenge_hash=NULL,binding_candidate_telegram_id=NULL,binding_expires_at=NULL
            WHERE id=$1 AND auth_version=$2
            """, DbValue.Uuid(targetAdminId), DbValue.Bigint(expectedVersion), DbValue.Boolean(disableAccount), DbValue.Boolean(revokeTelegram)), cancellationToken);
        if (changed != 1) AdminSessionStore.Reject(session);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE admin_id=$1 AND revoked_at IS NULL",
            DbValue.Uuid(targetAdminId)), cancellationToken);
        return true;
    }
}
