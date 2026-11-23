using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
using Platform.Security;

namespace Platform.Persistence;

public enum CustomerFlowState { Idle, Catalog, Quantity, Payment, Receipt, Support, Discounts }
public sealed record CustomerFlow(CustomerFlowState State, long Revision, Guid? OrderId, Guid? ProductId, long? AnchorMessageId, DateTime? ExpiresAt)
{
    public override string ToString() => "CustomerFlow [redacted]";
}
public sealed record BrandUser(Guid Id, string Language, bool Blocked, bool Banned, bool MarketingOptOut, CustomerFlow Flow)
{
    public override string ToString() => "BrandUser [redacted]";
}

public static class BrandUserStore
{
    public static async Task<BrandUser> ObservePrivateAsync(DbSession session, BrandContext brand, long verifiedActorId,
        CancellationToken ct = default, bool admit = true)
    {
        RequireIdentity(brand, verifiedActorId);
        byte[] identity = new byte[24];
        brand.BrandId.TryWriteBytes(identity.AsSpan(0, 16), bigEndian: true, out _);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(identity.AsSpan(16), verifiedActorId);
        byte[] digest = System.Security.Cryptography.SHA256.HashData(identity);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest))), ct);
        BrandUser member = await session.RunEfStepAsync(true, async (data, token) =>
        {
            var previous = await data.RequireMembershipAsync(brand, verifiedActorId, true, true, token);
            DateTime now = await data.BrandClockAsync(brand.BrandId, token);
            if (previous is null)
            {
                string language = await data.Data.Set<Entities.Brand>().Where(value => value.Id == brand.BrandId).Select(value => value.DefaultLanguage).SingleAsync(token);
                previous = new Entities.User { Id = Guid.NewGuid(), BrandId = brand.BrandId, TelegramId = verifiedActorId, Language = language };
                data.Data.Add(previous);
            }
            else
            {
                data.Data.Attach(previous);
                if (previous.LastSeenAt < now) previous.LastSeenAt = now;
            }
            return Project(previous);
        }, ct);
        if (admit) await session.AdmitWriterAsync(brand, ct);
        return member;
    }

    public static Task<BrandUser?> FindAsync(DbSession session, BrandContext brand, long verifiedActorId, CancellationToken ct = default) =>
        ReadAsync(session, brand, verifiedActorId, null, ct);

    public static async Task<BrandUser> RequireAsync(DbSession session, BrandContext brand, long verifiedActorId, Guid? membershipId,
        CancellationToken ct = default)
    {
        BrandUser? member = await ReadAsync(session, brand, verifiedActorId, membershipId, ct);
        if (member is null) Reject(session);
        return member!;
    }

    public static async Task SetPreferencesAsync(DbSession session, BrandContext brand, long verifiedActorId, Guid membershipId,
        string language, bool marketingOptOut, CancellationToken ct = default)
    {
        RequireIdentity(brand, verifiedActorId);
        RequireLanguage(language);
        await session.AdmitCustomerWriterAsync(brand, verifiedActorId, ct: ct);
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            var member = await data.RequireMembershipAsync(brand, verifiedActorId, false, false, token);
            if (member!.Id != membershipId) data.RejectScope("Customer membership is unavailable.");
            data.Data.Attach(member);
            member.Language = language;
            member.MarketingOptOutAt = marketingOptOut ? member.MarketingOptOutAt ?? await data.BrandClockAsync(brand.BrandId, token) : null;
            return true;
        }, ct);
        await session.AdmitWriterAsync(brand, ct);
    }

    public static async Task<BrandUser> TransitionAsync(DbSession session, BrandContext brand, long verifiedActorId, Guid membershipId,
        long expectedRevision, CustomerFlowState state, Guid? orderId = null, Guid? productId = null, long? anchorMessageId = null,
        DateTime? expiresAt = null, CancellationToken ct = default)
    {
        RequireIdentity(brand, verifiedActorId);
        if (membershipId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue || !Enum.IsDefined(state) ||
            orderId == Guid.Empty || productId == Guid.Empty || anchorMessageId <= 0 ||
            (state == CustomerFlowState.Idle && (orderId is not null || productId is not null || anchorMessageId is not null || expiresAt is not null)))
            throw new ArgumentException("Invalid customer flow transition.");
        await session.AdmitCustomerWriterAsync(brand, verifiedActorId, productId, ct);
        if (expiresAt is DateTime time) _ = DbValue.Utc(time);
        string code = StateCode(state);
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            var member = await data.RequireMembershipAsync(brand, verifiedActorId, false, false, token);
            if (member!.Id != membershipId) data.RejectScope("Customer membership is unavailable.");
            int changed = await data.UpdateAsync<Entities.User>(value => value.Id == membershipId && value.BrandId == brand.BrandId && value.TelegramId == verifiedActorId &&
                value.FlowRevision == expectedRevision && value.BlockedAt == null && value.BannedAt == null &&
                (expiresAt == null || expiresAt > EfValues.UtcNow()) &&
                (code == "idle" || code == "catalog" || value.FlowExpiresAt == null || value.FlowExpiresAt > EfValues.UtcNow()),
                setters => setters.SetProperty(value => value.FlowState, code).SetProperty(value => value.FlowRevision, value => value.FlowRevision + 1)
                    .SetProperty(value => value.FlowOrderId, orderId).SetProperty(value => value.FlowProductId, productId)
                    .SetProperty(value => value.FlowAnchorMessageId, anchorMessageId).SetProperty(value => value.FlowExpiresAt, expiresAt), token);
            if (changed != 1) data.RejectScope("Customer flow is unavailable or changed.");
            return true;
        }, ct);
        await session.AdmitWriterAsync(brand, ct);
        return await RequireAsync(session, brand, verifiedActorId, membershipId, ct);
    }

    public static async Task<long> ResolvePrivateDestinationAsync(DbSession session, BrandContext brand, Guid membershipId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(brand);
        long destination = await session.RunEfStepAsync(false, async (data, token) => await (
            from member in data.Data.Set<Entities.User>()
            join owner in data.Data.Set<Entities.Brand>() on member.BrandId equals owner.Id
            where member.Id == membershipId && owner.Id == brand.BrandId && owner.BotId == brand.BotId && owner.Active && member.BlockedAt == null && member.BannedAt == null
            select member.TelegramId).SingleOrDefaultAsync(token), ct);
        if (destination <= 0) Reject(session);
        return destination;
    }

    private static Task<BrandUser?> ReadAsync(DbSession session, BrandContext brand, long actorId, Guid? membershipId, CancellationToken ct)
    {
        RequireIdentity(brand, actorId);
        return session.RunEfStepAsync(false, async (data, token) =>
        {
            var member = await (from value in data.Data.Set<Entities.User>()
                                join owner in data.Data.Set<Entities.Brand>() on value.BrandId equals owner.Id
                                where owner.Id == brand.BrandId && owner.BotId == brand.BotId && owner.Active && value.TelegramId == actorId && (membershipId == null || value.Id == membershipId)
                                select value).AsNoTracking().SingleOrDefaultAsync(token);
            return member is null ? null : Project(member);
        }, ct);
    }
    internal static BrandUser Project(Entities.User member) => new(member.Id, member.Language, member.BlockedAt is not null, member.BannedAt is not null,
        member.MarketingOptOutAt is not null, new CustomerFlow(ParseState(member.FlowState ?? "idle"), member.FlowRevision, member.FlowOrderId, member.FlowProductId, member.FlowAnchorMessageId, member.FlowExpiresAt));

    private static void RequireIdentity(BrandContext brand, long actorId)
    {
        ArgumentNullException.ThrowIfNull(brand);
        if (actorId <= 0) throw new ArgumentOutOfRangeException(nameof(actorId));
    }
    internal static void RequireLanguage(string language)
    {
        if (string.IsNullOrEmpty(language) || language.Length > 35 || language[0] == '-' || language[^1] == '-' ||
            language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid language code.");
    }
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Customer membership or flow is unavailable."); }
    private static string StateCode(CustomerFlowState state) => state switch
    {
        CustomerFlowState.Idle => "idle",
        CustomerFlowState.Catalog => "catalog",
        CustomerFlowState.Quantity => "quantity",
        CustomerFlowState.Payment => "payment",
        CustomerFlowState.Receipt => "receipt",
        CustomerFlowState.Support => "support",
        CustomerFlowState.Discounts => "discounts",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
    private static CustomerFlowState ParseState(string code) => code switch
    {
        "idle" => CustomerFlowState.Idle,
        "catalog" => CustomerFlowState.Catalog,
        "quantity" => CustomerFlowState.Quantity,
        "payment" => CustomerFlowState.Payment,
        "receipt" => CustomerFlowState.Receipt,
        "support" => CustomerFlowState.Support,
        "discounts" => CustomerFlowState.Discounts,
        _ => throw new InvalidOperationException("Unknown customer flow state.")
    };
}
