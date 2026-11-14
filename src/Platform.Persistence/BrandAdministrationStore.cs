using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public sealed record BrandConfiguration(Guid Id, long BotId, string BotUsername, bool HasBotToken, long Revision, DateTime UpdatedAt);
public sealed class BrandAdministrationStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<BrandConfiguration> ReadAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await ReadAsync(session, context, ct);
    }
    public async Task<long> RotateTokenAsync(AuthenticatedAdmin actor, BrandContext context, BotToken token, long revision, Guid command,
        Func<BotToken, CancellationToken, Task<VerifiedBotProfile>> verify, CancellationToken ct = default)
    {
        if (token.BotId != context.BotId) throw new UnauthorizedAccessException("Bot identity mismatch.");
        byte[] hash = SHA256.HashData(token.ForTrustedTransport().Span);
        var audit = Audit(context, command, revision, AuditReason.CredentialRotation, hash);
        await using (var before = await sessions.BeginAsync(ct))
        {
            if (!await AdminAuditStore.AppendAsync(before, actor, audit, ct)) return revision + 1;
            await before.AdmitWriterAsync(context, ct);
            RequireRevision(await ReadAsync(before, context, ct), revision);
        }
        VerifiedBotProfile verified = await verify(token, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (verified.Id != context.BotId || verified.Username.Length is < 5 or > 32 || !verified.Username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new UnauthorizedAccessException("Bot verification failed.");
        ProtectedBotToken encrypted = new BotTokenVault(secrets).Protect(context, token);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, audit, ct)) return revision + 1;
            await session.AdmitWriterAsync(context, ct);
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.brands SET bot_token_ciphertext=$1,bot_token_key_id=$2,bot_username=$3,policy_version=policy_version+1,updated_at=clock_timestamp()
                WHERE id=$4 AND bot_id=$5 AND policy_version=$6
                """, DbValue.Binary(encrypted.Ciphertext), DbValue.Text(encrypted.KeyId), DbValue.Text(verified.Username), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(revision)), ct);
            if (changed != 1) throw new InvalidOperationException("Brand configuration revision conflict.");
            await session.CommitAsync(ct);
            return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<long> SetChannelsAsync(AuthenticatedAdmin actor, BrandContext context, IReadOnlyList<RequiredChannel> channels, long revision, Guid command,
        Func<BrandContext, IReadOnlyList<RequiredChannel>, CancellationToken, Task<bool>> verify, CancellationToken ct = default)
    {
        if (channels.Count > 1000 || channels.Any(c => c is null)) throw new ArgumentException("Too many channels.");
        RequiredChannel[] frozen = channels.OrderBy(c => c.ChatId).ToArray();
        if (frozen.Select(c => c.ChatId).Distinct().Count() != frozen.Length || frozen.Any(c => c.ChatId >= 0 ||
            (c.Title is not null && (string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 160 || c.Title.Any(char.IsControl))) ||
            (c.InviteLink is not null && (c.InviteLink.Length > 1024 || c.InviteLink.Any(char.IsControl) || !Uri.TryCreate(c.InviteLink, UriKind.Absolute, out var uri) || uri.Scheme != "https"))))
            throw new ArgumentException("Invalid required channels.");
        byte[] hash;
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                foreach (var c in frozen) { writer.Write(c.ChatId); writer.Write(c.Title ?? ""); writer.Write(c.InviteLink ?? ""); }
            hash = SHA256.HashData(bytes.ToArray());
        }
        var audit = Audit(context, command, revision, AuditReason.ConfigurationChange, hash);
        await using (var before = await sessions.BeginAsync(ct))
        {
            if (!await AdminAuditStore.AppendAsync(before, actor, audit, ct)) return revision + 1;
            await before.AdmitWriterAsync(context, ct);
            RequireRevision(await ReadAsync(before, context, ct), revision);
        }
        if (!await verify(context, Array.AsReadOnly(frozen), ct).WaitAsync(TimeSpan.FromSeconds(15), ct)) throw new InvalidOperationException("Bot channel eligibility is unavailable.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, audit, ct)) return revision + 1;
            await session.AdmitWriterAsync(context, ct);
            int changed = await session.ExecuteAsync(new DbStatement("UPDATE shop.brands SET policy_version=policy_version+1,updated_at=clock_timestamp() WHERE id=$1 AND bot_id=$2 AND policy_version=$3",
                DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(revision)), ct);
            if (changed != 1) throw new InvalidOperationException("Brand configuration revision conflict.");
            await session.ExecuteAsync(new DbStatement("UPDATE shop.brand_channels SET active=false,updated_at=clock_timestamp() WHERE brand_id=$1", DbValue.Uuid(context.BrandId)), ct);
            foreach (var channel in frozen)
                await session.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.brand_channels(brand_id,chat_id,title,invite_link,required,active) VALUES ($1,$2,$3,$4,true,true)
                    ON CONFLICT(brand_id,chat_id) DO UPDATE SET title=EXCLUDED.title,invite_link=EXCLUDED.invite_link,required=true,active=true,updated_at=clock_timestamp()
                    """, DbValue.Uuid(context.BrandId), DbValue.Bigint(channel.ChatId), Text(channel.Title), Text(channel.InviteLink)), ct);
            await session.CommitAsync(ct);
            return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static AuditCommand Audit(BrandContext context, Guid command, long revision, AuditReason reason, byte[] hash)
    {
        if (revision < 0 || revision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(revision));
        return new(command, AuditAction.Update, AuditEntity.Brand, context.BrandId, context.BrandId, reason, command, hash, revision, revision + 1);
    }
    private static void RequireRevision(BrandConfiguration data, long revision)
    { if (data.Revision != revision) throw new InvalidOperationException("Brand configuration revision conflict."); }
    private static Task<BrandConfiguration> ReadAsync(DbSession session, BrandContext context, CancellationToken ct) => session.QuerySingleAsync(new DbStatement(
        "SELECT id,bot_id,bot_username,policy_version,updated_at FROM shop.brands WHERE id=$1 AND bot_id=$2", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)),
        r => new BrandConfiguration(r.GetGuid(0), r.GetInt64(1), r.GetString(2), true, r.GetInt64(3), r.GetUtc(4)), ct);
    private static DbValue Text(string? value) => value is null ? DbValue.Null(NpgsqlTypes.NpgsqlDbType.Text) : DbValue.Text(value);
}
