using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
            await session.RunEfStepAsync(true, async (data, cancel) =>
            {
                await data.RequireAdminAsync(actor, null, cancel);
                await data.LockBrandAsync(context.BrandId, cancel);
                var brand = await data.Data.Set<Entities.Brand>().SingleAsync(value => value.Id == context.BrandId && value.BotId == context.BotId, cancel);
                if (brand.PolicyVersion != revision) throw new InvalidOperationException("Brand configuration revision conflict.");
                brand.BotTokenCiphertext = encrypted.Ciphertext.ToArray();
                brand.BotTokenKeyId = encrypted.KeyId;
                brand.BotUsername = verified.Username;
                brand.PolicyVersion++;
                brand.UpdatedAt = await data.BrandClockAsync(context.BrandId, cancel);
                return true;
            }, ct);
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
            await session.RunEfStepAsync(true, async (data, cancel) =>
            {
                await data.RequireAdminAsync(actor, null, cancel);
                await data.LockBrandAsync(context.BrandId, cancel);
                var brand = await data.Data.Set<Entities.Brand>().SingleAsync(value => value.Id == context.BrandId && value.BotId == context.BotId, cancel);
                if (brand.PolicyVersion != revision) throw new InvalidOperationException("Brand configuration revision conflict.");
                DateTime now = await data.BrandClockAsync(context.BrandId, cancel);
                brand.PolicyVersion++;
                brand.UpdatedAt = now;
                await data.FlushAsync(cancel, false);
                await data.DeactivateChannelsAsync(context.BrandId, now, cancel);
                int count = 0;
                foreach (var channel in frozen)
                {
                    var row = await data.Data.Set<Entities.BrandChannel>().SingleOrDefaultAsync(value => value.BrandId == context.BrandId && value.ChatId == channel.ChatId, cancel);
                    if (row is null) { row = new Entities.BrandChannel { BrandId = context.BrandId, ChatId = channel.ChatId }; data.Data.Add(row); }
                    row.Title = channel.Title; row.InviteLink = channel.InviteLink; row.Required = true; row.Active = true; row.UpdatedAt = now;
                    if (++count % 128 == 0) { await data.FlushAsync(cancel, false); data.Data.ChangeTracker.Clear(); }
                }
                return true;
            }, ct);
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
    private static Task<BrandConfiguration> ReadAsync(DbSession session, BrandContext context, CancellationToken ct) =>
        session.RunEfStepAsync(false, async (data, token) => await data.Data.Set<Entities.Brand>().Where(value => value.Id == context.BrandId && value.BotId == context.BotId)
            .Select(value => new BrandConfiguration(value.Id, value.BotId, value.BotUsername, true, value.PolicyVersion, value.UpdatedAt)).SingleAsync(token), ct);
}
