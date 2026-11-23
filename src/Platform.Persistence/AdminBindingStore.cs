using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
            await session.RunEfStepAsync(true, async (data, ct) =>
            {
                await AdminSessionStore.RequireActiveAsync(data, actor, actor.AdminId, ct, true);
                var account = await data.Data.Set<Entities.Admin>().AsNoTracking().SingleAsync(value => value.Id == actor.AdminId, ct);
                DateTime clock = await data.Data.Set<Entities.Admin>().Where(value => value.Id == actor.AdminId)
                    .Select(value => Configurations.EfValues.UtcNow()).SingleAsync(ct);
                DateTime now = clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
                account.BindingChallengeHash = hash.ToArray();
                account.BindingCandidateTelegramId = candidateTelegramId;
                account.BindingExpiresAt = now.Add(lifetime);
                var entry = data.Data.Attach(account);
                entry.Property(value => value.BindingChallengeHash).IsModified = true;
                entry.Property(value => value.BindingCandidateTelegramId).IsModified = true;
                entry.Property(value => value.BindingExpiresAt).IsModified = true;
                return true;
            }, cancellationToken);
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
            return await session.RunEfStepAsync(true, async (data, ct) =>
            {
                if (await data.ConfirmBindingAsync(adminId, pending.Version, initial, confirmed, ct) == 0) return false;
                data.Data.Add(new Entities.AuditEvent
                {
                    Id = Guid.NewGuid(),
                    ActorKind = "system",
                    Action = "update",
                    EntityKind = "admin",
                    EntityId = adminId,
                    Reason = "telegram_private_proof",
                    CorrelationId = Guid.NewGuid()
                });
                return true;
            }, cancellationToken);
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
            await session.RunEfStepAsync(true, (data, ct) => CompleteEfAsync(data, actor, candidateTelegramId, expectedAuthVersion, expected, commandId, ct), cancellationToken);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    internal static async Task<bool> CompleteEfAsync(EfSession data, AuthenticatedAdmin actor, long candidateTelegramId,
        long expectedAuthVersion, byte[] expected, Guid commandId, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, actor.AdminId, ct, true);
        var account = await data.Data.Set<Entities.Admin>().SingleAsync(value => value.Id == actor.AdminId, ct);
        if (account.AuthVersion != expectedAuthVersion || account.DisabledAt is not null || account.BindingExpiresAt is null ||
            account.BindingCandidateTelegramId != candidateTelegramId ||
            !CryptographicOperations.FixedTimeEquals(expected, account.BindingChallengeHash ?? Array.Empty<byte>()))
            data.RejectAuthentication();
        DateTime clock = await data.Data.Set<Entities.Admin>().Where(value => value.Id == actor.AdminId)
            .Select(value => Configurations.EfValues.UtcNow()).SingleAsync(ct);
        DateTime now = clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
        if (account.BindingExpiresAt <= now) data.RejectAuthentication();
        account.TelegramUserId = candidateTelegramId;
        account.TelegramVerifiedByAdminId = actor.AdminId;
        account.TelegramVerifiedAt = now;
        account.TelegramVerificationAuditId = commandId;
        account.PublicContactHandle = null;
        account.AuthVersion += 1;
        account.BindingChallengeHash = null;
        account.BindingCandidateTelegramId = null;
        account.BindingExpiresAt = null;
        var entry = data.Data.Entry(account);
        entry.Property(value => value.TelegramUserId).IsModified = true;
        entry.Property(value => value.TelegramVerifiedByAdminId).IsModified = true;
        entry.Property(value => value.TelegramVerifiedAt).IsModified = true;
        entry.Property(value => value.TelegramVerificationAuditId).IsModified = true;
        entry.Property(value => value.PublicContactHandle).IsModified = true;
        entry.Property(value => value.AuthVersion).IsModified = true;
        entry.Property(value => value.BindingChallengeHash).IsModified = true;
        entry.Property(value => value.BindingCandidateTelegramId).IsModified = true;
        entry.Property(value => value.BindingExpiresAt).IsModified = true;
        return true;
    }

    public static Task<bool> IsVerifiedStaffAsync(DbSession session, long telegramUserId, CancellationToken cancellationToken = default) =>
        session.RunEfStepAsync(false, async (data, ct) =>
        {
            var staff = data.Data.Set<Entities.Admin>().Where(value => value.TelegramUserId == telegramUserId && value.DisabledAt == null &&
                value.TelegramVerifiedAt != null && value.TelegramVerificationAuditId != null);
            Guid[] ids = await staff.OrderBy(value => value.Id).Select(value => value.Id).Take(2).ToArrayAsync(ct);
            foreach (Guid id in ids) await data.LockAdminAsync(id, ct, true);
            return await staff.AnyAsync(ct);
        }, cancellationToken);

    private static Task<Pending?> PendingAsync(DbSession session, Guid adminId, CancellationToken cancellationToken) =>
        session.RunEfStepAsync(false, async (data, ct) =>
        {
            await data.LockAdminAsync(adminId, ct);
            return await data.Data.Set<Entities.Admin>().AsNoTracking().Where(value => value.Id == adminId && value.DisabledAt == null && value.BindingExpiresAt > Configurations.EfValues.UtcNow())
                .Select(value => new Pending(value.AuthVersion, value.BindingCandidateTelegramId!.Value, value.BindingChallengeHash!)).SingleOrDefaultAsync(ct);
        }, cancellationToken);

    internal static byte[] Metadata(long first, long second)
    {
        byte[] bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, first);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8), second);
        return SHA256.HashData(bytes);
    }
    private sealed record Pending(long Version, long TelegramId, byte[] Hash);
}
