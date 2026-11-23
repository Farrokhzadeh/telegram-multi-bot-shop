using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed class IssuedAdminSession : IDisposable
{
    internal IssuedAdminSession(Guid id, DateTime expires, AdminToken token) { Id = id; ExpiresAt = expires; Token = token; }
    public Guid Id { get; }
    public DateTime ExpiresAt { get; }
    public AdminToken Token { get; }
    public void Dispose() => Token.Dispose();
    public override string ToString() => "Issued administrator session (redacted)";
}

/// <summary>Bearer-session foundation. No customer/group claims, automatic cookies, public bootstrap or online login route.</summary>
public static class AdminSessionStore
{
    public static async Task<IssuedAdminSession> LoginAsync(DbSession session, string login, ReadOnlyMemory<char> password,
        TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ValidateLogin(login, lifetime);
        IssuedAdminSession? issued = null;
        try { return await session.RunEfStepAsync(true, async (data, ct) => issued = await LoginAsync(data, login, password, lifetime, ct), cancellationToken); }
        catch { issued?.Dispose(); throw; }
    }
    internal static async Task<IssuedAdminSession> LoginAsync(EfSession session, string login, ReadOnlyMemory<char> password, TimeSpan lifetime, CancellationToken ct)
    {
        ValidateLogin(login, lifetime);
        var account = await session.RequirePasswordAsync(login, password, ct);
        var token = AdminToken.Create();
        byte[] hash = token.Hash();
        try
        {
            DateTime clock = await session.Data.Set<Entities.Admin>().Where(value => value.Id == account.Id).Select(value => Configurations.EfValues.UtcNow()).SingleAsync(ct);
            DateTime now = clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
            var row = new Entities.AdminSession { Id = Guid.NewGuid(), AdminId = account.Id, TokenHash = hash.ToArray(), AuthVersionAtIssue = account.AuthVersion, ExpiresAt = now.Add(lifetime) };
            session.Data.Add(row);
            account.LastLoginAt = now;
            session.Data.Attach(account).Property(value => value.LastLoginAt).IsModified = true;
            return new IssuedAdminSession(row.Id, row.ExpiresAt, token);
        }
        catch { token.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    public static async Task<AuthenticatedAdmin> AuthenticateAsync(DbSession session, AdminToken token, CancellationToken cancellationToken = default)
    {
        byte[] hash = token.Hash();
        try { return await session.RunEfStepAsync(false, (data, ct) => AuthenticateAsync(data, hash, ct), cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    internal static async Task<AuthenticatedAdmin> AuthenticateAsync(EfSession session, byte[] hash, CancellationToken ct)
    {
        var actor = await (from ticket in session.Data.Set<Entities.AdminSession>()
                           join admin in session.Data.Set<Entities.Admin>() on ticket.AdminId equals admin.Id
                           where ticket.TokenHash == hash && admin.DisabledAt == null && ticket.RevokedAt == null &&
                               ticket.ExpiresAt > Configurations.EfValues.UtcNow() && ticket.AuthVersionAtIssue == admin.AuthVersion
                           select new { Admin = admin.Id, Session = ticket.Id, Version = admin.AuthVersion }).SingleOrDefaultAsync(ct);
        if (actor is null) session.RejectAuthentication();
        return new AuthenticatedAdmin(actor.Admin, actor.Session, actor.Version, hash);
    }
    public static Task RequireActiveAsync(DbSession session, AuthenticatedAdmin actor, Guid? otherAdmin = null, CancellationToken cancellationToken = default) =>
        session.RunEfStepAsync(false, async (data, ct) => { await RequireActiveAsync(data, actor, otherAdmin, ct, true); return true; }, cancellationToken);
    internal static async Task RequireActiveAsync(EfSession session, AuthenticatedAdmin actor, Guid? otherAdmin, CancellationToken ct, bool lockRows = false)
    {
        try { await session.RequireAdminAsync(actor, otherAdmin, ct, lockRows); }
        catch (UnauthorizedAccessException) { session.RejectAuthentication(); }
    }
    public static Task LogoutAsync(DbSession session, AuthenticatedAdmin actor, CancellationToken cancellationToken = default) =>
        session.RunEfStepAsync(true, async (data, ct) =>
        {
            await RequireActiveAsync(data, actor, null, ct);
            var ticket = await data.Data.Set<Entities.AdminSession>().SingleAsync(value => value.Id == actor.SessionId, ct);
            if (ticket.RevokedAt is null)
            {
                DateTime clock = await data.Data.Set<Entities.Admin>().Where(value => value.Id == actor.AdminId).Select(value => Configurations.EfValues.UtcNow()).SingleAsync(ct);
                ticket.RevokedAt = clock.Kind == DateTimeKind.Local ? clock.ToUniversalTime() : DateTime.SpecifyKind(clock, DateTimeKind.Utc);
            }
            return true;
        }, cancellationToken);
    private static void ValidateLogin(string login, TimeSpan lifetime)
    {
        if (login is null || login.Length is < 1 or > 160 || lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromHours(8) || lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Invalid administrator session request.");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Reject(DbSession session)
    {
        session.RejectApplication();
        throw new AdminAuthenticationException();
    }
}
