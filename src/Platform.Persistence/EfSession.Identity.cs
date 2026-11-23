using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;
using Platform.Security;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal void RestoreSharedAdmission(bool value) => sharedAdmitted = writer?.Shared == true && value;
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal void RejectScope(string message) => throw Denied(message);
    internal Task<int> LockAdminAsync(Guid id, CancellationToken ct) => AdmitAsync(() => Data.Set<Admin>().Where(value => value.Id == id)
        .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.AuthVersion, value => value.AuthVersion), ct));
    internal Task<int> LockMemberAsync(Guid brand, long actor, CancellationToken ct) => AdmitAsync(() => Data.Set<User>().Where(value => value.BrandId == brand && value.TelegramId == actor)
        .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.FlowRevision, value => value.FlowRevision), ct));
    internal Task<int> LockProductAsync(Guid id, CancellationToken ct) => AdmitAsync(() => Data.Set<Product>().Where(value => value.Id == id)
        .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Revision, value => value.Revision), ct));
    internal Task<int> LockBrandAsync(Guid id, CancellationToken ct) => AdmitAsync(() => Data.Set<Entities.Brand>().Where(value => value.Id == id)
        .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PolicyVersion, value => value.PolicyVersion), ct));
    internal Task<int> LockChannelsAsync(Guid brand, CancellationToken ct) => AdmitAsync(() => Data.Set<BrandChannel>().Where(value => value.BrandId == brand && value.Active && value.Required)
        .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.UpdatedAt, value => value.UpdatedAt), ct));
    internal async Task<DateTime> BrandClockAsync(Guid brand, CancellationToken ct)
    {
        DateTime clock = await Data.Set<Entities.Brand>().Where(value => value.Id == brand).Select(value => EfValues.UtcNow()).SingleAsync(ct);
        return clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
    }
    internal async Task<User?> RequireMembershipAsync(BrandContext context, long actor, bool missing, bool blocked, CancellationToken ct)
    {
        if (actor <= 0 || (writer is not null && (writer.Shared || writer.Find(context) is null))) throw Denied("Customer writer scope is unavailable.");
        await LockMemberAsync(context.BrandId, actor, ct);
        var member = await Data.Set<User>().AsNoTracking().SingleOrDefaultAsync(value => value.BrandId == context.BrandId && value.TelegramId == actor, ct);
        if ((!missing && member is null) || (!blocked && member is not null && (member.BlockedAt is not null || member.BannedAt is not null)) ||
            !await Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, ct))
            throw Denied("Customer membership or brand is unavailable.");
        if (!nativeWriterBoundary && writer is null) throw Denied("Customer writes require a registered writer.");
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
