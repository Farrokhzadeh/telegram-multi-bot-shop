using Microsoft.EntityFrameworkCore;
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
        await session.RunEfStepAsync(true, (data, ct) => RevokeEfAsync(data, actor, targetAdminId, expectedVersion, disableAccount, revokeTelegram, ct), cancellationToken);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE admin_id=$1 AND revoked_at IS NULL",
            DbValue.Uuid(targetAdminId)), cancellationToken);
        return true;
    }
    internal static async Task<bool> RevokeEfAsync(EfSession data, AuthenticatedAdmin actor, Guid targetAdminId,
        long expectedVersion, bool disableAccount, bool revokeTelegram, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, targetAdminId, ct, true);
        var account = await data.Data.Set<Entities.Admin>().SingleOrDefaultAsync(value => value.Id == targetAdminId, ct);
        if (account is null || account.AuthVersion != expectedVersion) data.RejectAuthentication();
        DateTime clock = await data.Data.Set<Entities.Admin>().Where(value => value.Id == targetAdminId)
            .Select(value => Configurations.EfValues.UtcNow()).SingleAsync(ct);
        DateTime now = clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
        account.AuthVersion += 1;
        account.BindingChallengeHash = null;
        account.BindingCandidateTelegramId = null;
        account.BindingExpiresAt = null;
        var entry = data.Data.Entry(account);
        entry.Property(value => value.AuthVersion).IsModified = true;
        entry.Property(value => value.BindingChallengeHash).IsModified = true;
        entry.Property(value => value.BindingCandidateTelegramId).IsModified = true;
        entry.Property(value => value.BindingExpiresAt).IsModified = true;
        if (disableAccount)
        {
            account.DisabledAt = now;
            entry.Property(value => value.DisabledAt).IsModified = true;
        }
        if (revokeTelegram)
        {
            account.TelegramUserId = null;
            account.TelegramVerifiedByAdminId = null;
            account.TelegramVerifiedAt = null;
            account.TelegramVerificationAuditId = null;
            account.PublicContactHandle = null;
            entry.Property(value => value.TelegramUserId).IsModified = true;
            entry.Property(value => value.TelegramVerifiedByAdminId).IsModified = true;
            entry.Property(value => value.TelegramVerifiedAt).IsModified = true;
            entry.Property(value => value.TelegramVerificationAuditId).IsModified = true;
            entry.Property(value => value.PublicContactHandle).IsModified = true;
        }
        return true;
    }
}
