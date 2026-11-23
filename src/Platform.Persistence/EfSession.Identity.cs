using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class EfApplicationException(string message, bool argument = false) : Exception(message) { internal bool Argument { get; } = argument; }

internal sealed partial class EfSession
{
    internal void RestoreSharedAdmission(bool value) => sharedAdmitted = writer?.Shared == true && value;
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectScope(string message) => throw Denied(message);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectConflict(string message) { Integrity.Reject(); throw new EfApplicationException(message); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectArgument(string message) { Integrity.Reject(); throw new EfApplicationException(message, true); }
    private async Task<int> LockRowsAsync<T>(IQueryable<T> query, EfRowLock mode, CancellationToken ct)
    {
        Integrity.ThrowIfRejected();
        if (Integrity.RowLock != EfRowLock.None) throw Denied("Row locks cannot overlap.");
        try
        {
            if (Data is PostgresAkantiDbContext) Integrity.RowLock = mode;
            int count = (await query.Take(1001).ToArrayAsync(ct)).Length;
            if (count > 1000) throw Denied("The row lock batch exceeds its bound.");
            return count;
        }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
    }
    internal Task<int> LockAdminAsync(Guid id, CancellationToken ct, bool shared = false) => LockRowsAsync(Data.Set<Admin>().Where(value => value.Id == id).Select(value => value.Id), shared ? EfRowLock.Share : EfRowLock.Update, ct);
    internal Task<int> LockMemberAsync(Guid brand, long actor, CancellationToken ct, bool shared = false) => LockRowsAsync(Data.Set<User>().Where(value => value.BrandId == brand && value.TelegramId == actor).Select(value => value.Id), shared ? EfRowLock.Share : EfRowLock.Update, ct);
    internal Task<int> LockProductAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Product>().Where(value => value.Id == id).Select(value => value.Id), EfRowLock.Update, ct);
    internal Task<int> LockBrandAsync(Guid id, CancellationToken ct, bool shared = false) => LockRowsAsync(Data.Set<Entities.Brand>().Where(value => value.Id == id).Select(value => value.Id), shared ? EfRowLock.Share : EfRowLock.NoKeyUpdate, ct);
    internal Task<int> LockChannelsAsync(Guid brand, CancellationToken ct) => LockRowsAsync(Data.Set<BrandChannel>().Where(value => value.BrandId == brand && value.Active && value.Required).OrderBy(value => value.ChatId).Select(value => value.ChatId), EfRowLock.Share, ct);
    internal Task<DateTime> BrandClockAsync(Guid brand, CancellationToken ct) => ClockAsync(Data.Set<Entities.Brand>().Where(value => value.Id == brand), ct);
    internal async Task<DateTime> ClockAsync<T>(IQueryable<T> anchor, CancellationToken ct)
    {
        DateTime clock = await anchor.Select(value => EfValues.UtcNow()).FirstAsync(ct);
        return clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
    }
    internal async Task<User?> RequireMembershipAsync(BrandContext context, long actor, bool missing, bool blocked, CancellationToken ct, bool allowUnscoped = false)
    {
        if (actor <= 0 || (writer is not null && (writer.Shared || writer.Find(context) is null))) throw Denied("Customer writer scope is unavailable.");
        await LockMemberAsync(context.BrandId, actor, ct);
        var member = await Data.Set<User>().AsNoTracking().SingleOrDefaultAsync(value => value.BrandId == context.BrandId && value.TelegramId == actor, ct);
        if ((!missing && member is null) || (!blocked && member is not null && (member.BlockedAt is not null || member.BannedAt is not null)) ||
            !await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct))
            throw Denied("Customer membership or brand is unavailable.");
        if (!nativeWriterBoundary && writer is null && !allowUnscoped) throw Denied("Customer writes require a registered writer.");
        admitted = true;
        return member;
    }
    internal async Task RequireRegisteredRevisionAsync(BrandContext context, long revision, CancellationToken ct)
    {
        if (writer is not null && (writer.Find(context)?.Revision != revision || (writer.Shared && !sharedAdmitted)))
            throw Denied("Writer scope is unavailable.");
        await LockBrandAsync(context.BrandId, ct);
        if (!await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active && value.PolicyVersion == revision, ct))
            throw Denied("Writer revision is unavailable.");
        markWriter?.Invoke(context, revision);
        admitted = true;
    }
    internal async Task<int> ConfirmBindingAsync(Guid admin, long version, byte[] initial, byte[] confirmed, CancellationToken ct)
    {
        if (!write || !nativeWriterBoundary) throw Denied("A native verified proof transaction is required.");
        Integrity.ThrowIfRejected();
        int changed = await AdmitAsync(() => Data.Set<Admin>().Where(value => value.Id == admin && value.DisabledAt == null &&
            value.BindingExpiresAt > EfValues.UtcNow() && value.AuthVersion == version && value.BindingChallengeHash == initial)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.BindingChallengeHash, confirmed), ct));
        admitted = true;
        return changed;
    }
    internal Task<int> DeactivateChannelsAsync(Guid brand, DateTime now, CancellationToken ct)
    {
        Integrity.RequireWrite();
        return AdmitAsync(() => Data.Set<BrandChannel>().Where(value => value.BrandId == brand)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Active, false).SetProperty(value => value.UpdatedAt, now), ct));
    }
    internal Task<int> RevokeSessionsAsync(Guid admin, CancellationToken ct)
    {
        Integrity.RequireWrite();
        return AdmitAsync(() => Data.Set<AdminSession>().Where(value => value.AdminId == admin && value.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.RevokedAt, value => EfValues.UtcNow()), ct));
    }
}
