using NpgsqlTypes;
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
        int changed = await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.users(id,brand_id,telegram_id,language)
            SELECT $1,b.id,$2,b.default_language FROM shop.brands b WHERE b.id=$3 AND b.bot_id=$4 AND b.active
            ON CONFLICT (brand_id,telegram_id) DO UPDATE SET last_seen_at=GREATEST(shop.users.last_seen_at,clock_timestamp())
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Bigint(verifiedActorId), DbValue.Uuid(brand.BrandId), DbValue.Bigint(brand.BotId)), ct);
        if (changed != 1) Reject(session);
        var member = await RequireAsync(session, brand, verifiedActorId, null, ct);
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
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.users u SET language=$1,marketing_opt_out_at=CASE WHEN $2 THEN COALESCE(u.marketing_opt_out_at,clock_timestamp()) ELSE NULL END
            FROM shop.brands b WHERE u.id=$3 AND u.brand_id=$4 AND u.telegram_id=$5 AND b.id=u.brand_id AND b.bot_id=$6 AND b.active
                AND u.banned_at IS NULL AND u.blocked_at IS NULL
            """, DbValue.Text(language), DbValue.Boolean(marketingOptOut), DbValue.Uuid(membershipId), DbValue.Uuid(brand.BrandId),
            DbValue.Bigint(verifiedActorId), DbValue.Bigint(brand.BotId)), ct);
        if (changed != 1) Reject(session);
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
        DbValue expiry = expiresAt is DateTime time ? DbValue.Utc(time) : DbValue.Null(NpgsqlDbType.TimestampTz);
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.users u SET flow_state=$1,flow_revision=u.flow_revision+1,flow_order_id=$2,flow_product_id=$3,
                flow_anchor_message_id=$4,flow_expires_at=$5
            FROM shop.brands b WHERE u.id=$6 AND u.brand_id=$7 AND u.telegram_id=$8 AND u.flow_revision=$9
                AND b.id=u.brand_id AND b.bot_id=$10 AND b.active AND u.banned_at IS NULL AND u.blocked_at IS NULL
                AND ($5 IS NULL OR $5>clock_timestamp())
                AND ($1 IN ('idle','catalog') OR u.flow_expires_at IS NULL OR u.flow_expires_at>clock_timestamp())
            """, DbValue.Text(StateCode(state)), NullableGuid(orderId), NullableGuid(productId),
            anchorMessageId is long anchor ? DbValue.Bigint(anchor) : DbValue.Null(NpgsqlDbType.Bigint), expiry,
            DbValue.Uuid(membershipId), DbValue.Uuid(brand.BrandId), DbValue.Bigint(verifiedActorId), DbValue.Bigint(expectedRevision), DbValue.Bigint(brand.BotId)), ct);
        if (changed != 1) Reject(session);
        await session.AdmitWriterAsync(brand, ct);
        return await RequireAsync(session, brand, verifiedActorId, membershipId, ct);
    }

    public static async Task<long> ResolvePrivateDestinationAsync(DbSession session, BrandContext brand, Guid membershipId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(brand);
        long destination = await session.QuerySingleAsync(new DbStatement("""
            SELECT COALESCE((SELECT u.telegram_id FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id
                WHERE u.id=$1 AND u.brand_id=$2 AND b.bot_id=$3 AND b.active AND u.blocked_at IS NULL AND u.banned_at IS NULL),0)
            """, DbValue.Uuid(membershipId), DbValue.Uuid(brand.BrandId), DbValue.Bigint(brand.BotId)), row => row.GetInt64(0), ct);
        if (destination <= 0) Reject(session);
        return destination;
    }

    private static Task<BrandUser?> ReadAsync(DbSession session, BrandContext brand, long actorId, Guid? membershipId, CancellationToken ct)
    {
        RequireIdentity(brand, actorId);
        return session.QuerySingleAsync(new DbStatement("""
            SELECT u.id,u.language,u.blocked_at IS NOT NULL,u.banned_at IS NOT NULL,u.marketing_opt_out_at IS NOT NULL,
                u.flow_state,u.flow_revision,u.flow_order_id,u.flow_product_id,u.flow_anchor_message_id,u.flow_expires_at
            FROM (SELECT 1) seed LEFT JOIN (SELECT u.* FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id
                WHERE u.brand_id=$1 AND u.telegram_id=$2 AND ($3 IS NULL OR u.id=$3) AND b.bot_id=$4 AND b.active) u ON true
            """, DbValue.Uuid(brand.BrandId), DbValue.Bigint(actorId), NullableGuid(membershipId), DbValue.Bigint(brand.BotId)),
            row => row.IsDBNull(0) ? null : new BrandUser(row.GetGuid(0), row.GetString(1), row.GetBoolean(2), row.GetBoolean(3), row.GetBoolean(4),
                new CustomerFlow(ParseState(row.GetString(5)), row.GetInt64(6), row.IsDBNull(7) ? null : row.GetGuid(7),
                    row.IsDBNull(8) ? null : row.GetGuid(8), row.IsDBNull(9) ? null : row.GetInt64(9), row.IsDBNull(10) ? null : row.GetUtc(10))), ct);
    }

    private static DbValue NullableGuid(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    private static void RequireIdentity(BrandContext brand, long actorId)
    {
        ArgumentNullException.ThrowIfNull(brand);
        if (actorId <= 0) throw new ArgumentOutOfRangeException(nameof(actorId));
    }
    private static void RequireLanguage(string language)
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
