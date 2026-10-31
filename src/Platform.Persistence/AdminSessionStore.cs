using System.Security.Cryptography;
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
        if (login is null || login.Length is < 1 or > 160 || lifetime < TimeSpan.FromSeconds(1) ||
            lifetime > TimeSpan.FromHours(8) || lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Invalid administrator session request.");
        var account = await session.QuerySingleAsync(new DbStatement("""
            WITH actor AS (SELECT id,authentication_mode,password_verifier,disabled_at,auth_version FROM shop.admins WHERE login=$1 FOR UPDATE)
            SELECT a.id,a.authentication_mode,a.password_verifier,a.disabled_at,a.auth_version
            FROM (SELECT 1) singleton LEFT JOIN actor a ON true
            """, DbValue.Text(login)), row => row.IsDBNull(0) ? null : new Account(row.GetGuid(0), row.GetString(1),
                row.IsDBNull(2) ? null : row.GetString(2), !row.IsDBNull(3), row.GetInt64(4)), cancellationToken);
        bool valid = AdminAuth.VerifyPassword(password.Span, account?.Verifier);
        if (!valid || account is null || account.Disabled || account.Mode != "password") Reject(session);
        var token = AdminToken.Create();
        byte[] hash = token.Hash();
        try
        {
            Guid id = Guid.NewGuid();
            DateTime expires = await session.QuerySingleAsync(new DbStatement("""
                INSERT INTO shop.admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at)
                VALUES ($1,$2,$3,$4,clock_timestamp()+$5*INTERVAL '1 second') RETURNING expires_at
                """, DbValue.Uuid(id), DbValue.Uuid(account!.Id), DbValue.Binary(hash), DbValue.Bigint(account.Version),
                DbValue.Integer(checked((int)lifetime.TotalSeconds))), row => row.GetUtc(0), cancellationToken);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.admins SET last_login_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(account.Id)), cancellationToken);
            return new IssuedAdminSession(id, expires, token);
        }
        catch { token.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    public static async Task<AuthenticatedAdmin> AuthenticateAsync(DbSession session, AdminToken token, CancellationToken cancellationToken = default)
    {
        byte[] hash = token.Hash();
        try
        {
            var actor = await session.QuerySingleAsync(new DbStatement("""
                SELECT a.id,s.id,a.auth_version FROM (SELECT 1) singleton
                LEFT JOIN (shop.admin_sessions s JOIN shop.admins a ON a.id=s.admin_id)
                    ON s.token_hash=$1 AND a.disabled_at IS NULL AND s.revoked_at IS NULL
                    AND s.expires_at>clock_timestamp() AND s.auth_version_at_issue=a.auth_version
                """, DbValue.Binary(hash)), row => row.IsDBNull(0) ? null : new AuthenticatedAdmin(row.GetGuid(0), row.GetGuid(1), row.GetInt64(2), hash), cancellationToken);
            if (actor is null) Reject(session);
            return actor!;
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    // Privileged command entry: authenticate first, then acquire all involved admin rows in UUID order.
    // Call before business locks/writes. Context resolution above intentionally holds no row locks.
    public static async Task RequireActiveAsync(DbSession session, AuthenticatedAdmin actor, Guid? otherAdmin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await session.QuerySingleAsync(new DbStatement("""
            WITH locked AS MATERIALIZED (SELECT id FROM shop.admins WHERE id=$1 OR id=$2 ORDER BY id FOR UPDATE)
            SELECT count(*) FROM locked
            """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(otherAdmin ?? actor.AdminId)), row => row.GetInt64(0), cancellationToken);
        bool active = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.admins a JOIN shop.admin_sessions s ON s.admin_id=a.id
                WHERE a.id=$1 AND s.id=$2 AND s.token_hash=$3 AND a.disabled_at IS NULL AND s.revoked_at IS NULL
                AND a.auth_version=$4 AND s.auth_version_at_issue=$4 AND s.expires_at>clock_timestamp())
            """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(actor.SessionId), DbValue.Binary(actor.TokenHash), DbValue.Bigint(actor.AuthVersion)),
            row => row.GetBoolean(0), cancellationToken);
        if (!active) Reject(session);
    }

    public static async Task LogoutAsync(DbSession session, AuthenticatedAdmin actor, CancellationToken cancellationToken = default)
    {
        await RequireActiveAsync(session, actor, cancellationToken: cancellationToken);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE id=$1 AND revoked_at IS NULL", DbValue.Uuid(actor.SessionId)), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Reject(DbSession session)
    {
        session.RejectApplication();
        throw new AdminAuthenticationException();
    }
    private sealed record Account(Guid Id, string Mode, string? Verifier, bool Disabled, long Version);
}
