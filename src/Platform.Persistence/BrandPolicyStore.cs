using System.Security.Cryptography;
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
        bool reachable = await session.QuerySingleAsync(new DbStatement("""
            WITH member AS (SELECT id FROM shop.users WHERE id=$1 AND brand_id=$2 AND telegram_id=$3
                AND blocked_at IS NULL AND banned_at IS NULL FOR SHARE)
            SELECT EXISTS(SELECT 1 FROM member)
            """, DbValue.Uuid(member.Id), DbValue.Uuid(context.BrandId), DbValue.Bigint(verifiedActorId)), row => row.GetBoolean(0), ct);
        if (!reachable) Deny(session);
        var policy = await session.QuerySingleAsync(new DbStatement("""
            SELECT b.policy_version,b.require_channel_join,b.public_name,b.logo_asset_ref,
                (SELECT a.public_contact_handle FROM shop.admins a WHERE a.id=b.customer_support_admin_id AND a.disabled_at IS NULL
                    AND a.telegram_user_id IS NOT NULL AND a.telegram_verified_at IS NOT NULL AND a.telegram_verification_audit_id IS NOT NULL),b.default_language
            FROM shop.brands b WHERE b.id=$1 AND b.bot_id=$2 AND b.active FOR SHARE OF b
            """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), row =>
                (Revision: row.GetInt64(0), Enforce: row.GetBoolean(1), Name: row.GetString(2),
                    Logo: row.IsDBNull(3) ? null : row.GetString(3), Contact: row.IsDBNull(4) ? null : row.GetString(4), DefaultLanguage: row.GetString(5)), ct);
        IReadOnlyList<RequiredChannel> channels = await session.QueryManyAsync(new DbStatement("""
            SELECT chat_id,title,invite_link FROM shop.brand_channels WHERE brand_id=$1 AND active AND required ORDER BY chat_id FOR SHARE
            """, DbValue.Uuid(context.BrandId)), row => new RequiredChannel(row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2)), 1000, ct);
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
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.brands SET require_channel_join=$1,policy_version=policy_version+1,updated_at=clock_timestamp()
            WHERE id=$2 AND policy_version=$3 AND require_channel_join=$4
            """, DbValue.Boolean(enabled), DbValue.Uuid(brandId), DbValue.Bigint(expectedRevision), DbValue.Boolean(previous)), ct);
        if (changed != 1) Deny(session);
        return expectedRevision + 1;
    }

    private static void Deny(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Brand policy is unavailable or changed."); }
}
