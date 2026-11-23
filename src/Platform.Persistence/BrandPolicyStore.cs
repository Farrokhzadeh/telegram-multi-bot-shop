using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record RequiredChannel(long ChatId, string? Title, string? InviteLink);
public sealed class BrandPolicySnapshot
{
    internal BrandPolicySnapshot(BrandContext context, long actorId, Guid memberId, long revision, bool enforce, bool staff,
        string publicName, string language, string? logo, string? contact, IReadOnlyList<RequiredChannel> channels)
    {
        Context = context; ActorId = actorId; MemberId = memberId; Revision = revision; EnforceChannels = enforce; VerifiedStaff = staff;
        PublicName = publicName; Language = language; LogoAssetRef = logo; PublicContact = contact; Channels = channels;
    }
    internal BrandContext Context { get; }
    internal long ActorId { get; }
    public long VerifiedActorId => ActorId;
    internal Guid MemberId { get; }
    public long Revision { get; }
    public bool EnforceChannels { get; }
    public bool VerifiedStaff { get; }
    public string PublicName { get; }
    public string Language { get; }
    public string? LogoAssetRef { get; }
    public string? PublicContact { get; }
    public IReadOnlyList<RequiredChannel> Channels { get; }
    public override string ToString() => "BrandPolicySnapshot [redacted]";
}

public static class BrandPolicyStore
{
    public static async Task<BrandPolicySnapshot> ReadAsync(DbSession session, BrandContext context, long verifiedActorId, CancellationToken ct = default)
    {
        bool staff = await AdminBindingStore.IsVerifiedStaffAsync(session, verifiedActorId, ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, verifiedActorId, null, ct);
        if (member.Blocked || member.Banned) Deny(session);
        var policy = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockMemberAsync(context.BrandId, verifiedActorId, token);
            if (!await data.Data.Set<Entities.User>().AnyAsync(value => value.Id == member.Id && value.BrandId == context.BrandId && value.TelegramId == verifiedActorId && value.BlockedAt == null && value.BannedAt == null, token))
                data.RejectScope("Brand membership is unavailable.");
            await data.LockBrandAsync(context.BrandId, token);
            var brand = await data.Data.Set<Entities.Brand>().AsNoTracking().SingleAsync(value => value.Id == context.BrandId && value.BotId == context.BotId && value.Active, token);
            string? contact = await data.Data.Set<Entities.Admin>().Where(value => value.Id == brand.CustomerSupportAdminId && value.DisabledAt == null && value.TelegramUserId != null &&
                value.TelegramVerifiedAt != null && value.TelegramVerificationAuditId != null).Select(value => value.PublicContactHandle).SingleOrDefaultAsync(token);
            await data.LockChannelsAsync(context.BrandId, token);
            var channels = await data.Data.Set<Entities.BrandChannel>().Where(value => value.BrandId == context.BrandId && value.Active && value.Required).OrderBy(value => value.ChatId)
                .Select(value => new RequiredChannel(value.ChatId, value.Title, value.InviteLink)).Take(1001).ToArrayAsync(token);
            if (channels.Length > 1000) throw new InvalidOperationException("Required channel limit exceeded.");
            return (Revision: brand.PolicyVersion, Enforce: brand.RequireChannelJoin, Name: brand.PublicName, Logo: brand.LogoAssetRef, Contact: contact, DefaultLanguage: brand.DefaultLanguage, Channels: channels);
        }, ct);
        IReadOnlyList<RequiredChannel> channels = policy.Channels;
        string? contact = policy.Contact;
        if (contact is not null && (contact.Length is < 5 or > 32 || !contact.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))) contact = null;
        string language = member.Language;
        if (!(language.Equals("en", StringComparison.OrdinalIgnoreCase) || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("fa", StringComparison.OrdinalIgnoreCase) || language.StartsWith("fa-", StringComparison.OrdinalIgnoreCase))) language = policy.DefaultLanguage;
        return new(context, verifiedActorId, member.Id, policy.Revision, policy.Enforce, staff, policy.Name, language, policy.Logo, contact, channels);
    }

    public static async Task RequireCurrentAsync(DbSession session, BrandContext context, long verifiedActorId,
        BrandPolicySnapshot original, bool usableProof, CancellationToken ct = default)
    {
        if (!usableProof || context.BrandId != original.Context.BrandId || context.BotId != original.Context.BotId ||
            context.DeploymentId != original.Context.DeploymentId || context.ConfigurationRevision != original.Context.ConfigurationRevision ||
            verifiedActorId != original.ActorId) Deny(session);
        BrandPolicySnapshot current = await ReadAsync(session, context, verifiedActorId, ct);
        if (current.MemberId != original.MemberId || current.Revision != original.Revision || current.EnforceChannels != original.EnforceChannels ||
            current.VerifiedStaff != original.VerifiedStaff || !current.Channels.SequenceEqual(original.Channels)) Deny(session);
    }

    public static async Task<long> SetEnforcementAsync(DbSession session, AuthenticatedAdmin actor, Guid brandId,
        long expectedRevision, bool previous, bool enabled, Guid commandId, Guid correlationId, CancellationToken ct = default)
    {
        if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var audit = new AuditCommand(commandId, AuditAction.ChannelEnforcement, AuditEntity.Brand, brandId, brandId,
            AuditReason.ConfigurationChange, correlationId, SHA256.HashData("channel-enforcement-v1"u8), expectedRevision, expectedRevision + 1, previous, enabled);
        if (!await AdminAuditStore.AppendAsync(session, actor, audit, ct)) return expectedRevision + 1;
        await session.AdmitBrandWriterAsync(brandId, ct);
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireAdminAsync(actor, null, token);
            await data.LockBrandAsync(brandId, token);
            var brand = await data.Data.Set<Entities.Brand>().SingleOrDefaultAsync(value => value.Id == brandId && value.PolicyVersion == expectedRevision && value.RequireChannelJoin == previous, token);
            if (brand is null) data.RejectScope("Brand policy is unavailable or changed.");
            brand.RequireChannelJoin = enabled;
            brand.PolicyVersion++;
            brand.UpdatedAt = await data.BrandClockAsync(brandId, token);
            return true;
        }, ct);
        return expectedRevision + 1;
    }

    private static void Deny(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Brand policy is unavailable or changed."); }
}
