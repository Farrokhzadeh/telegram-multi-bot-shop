using System.Globalization;
using Platform.Security;

namespace Platform.Persistence;

public sealed record CustomerHome(string Language, string Brand, decimal Balance, long Orders, DateTime Joined, string? Contact);
public sealed record CustomerScreenEntry(Guid Id, string Title, string Status, decimal? Amount, string? Currency, DateTime Created, int? Quantity = null);
public sealed record CustomerScreenPage(IReadOnlyList<CustomerScreenEntry> Entries, bool HasNext);
public enum CustomerScreenList { Orders, Transactions, Deposits, Discounts }

public sealed class CustomerScreens(DbSessionFactory sessions)
{
    public async Task<CustomerHome> HomeAsync(BrandContext context, long actor, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandPolicySnapshot policy = await BrandPolicyStore.ReadAsync(session, context, actor, ct);
        return await session.QuerySingleAsync(new DbStatement("""
            SELECT u.first_seen_at,COALESCE(w.balance,0)::text,(SELECT count(*) FROM shop.orders o WHERE o.user_id=u.id)
            FROM shop.users u LEFT JOIN shop.wallets w ON w.user_id=u.id WHERE u.id=$1
            """, DbValue.Uuid(policy.MemberId)), row =>
                new CustomerHome(policy.Language, policy.PublicName, Amount(row.GetString(1)), row.GetInt64(2), row.GetUtc(0), policy.PublicContact), ct);
    }

    public async Task SetLanguageAsync(BrandContext context, long actor, string language, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        await BrandUserStore.SetPreferencesAsync(session, context, actor, member.Id, language, member.MarketingOptOut, ct);
        await session.CommitAsync(ct);
    }

    public async Task<CustomerScreenPage> ListAsync(BrandContext context, long actor, CustomerScreenList screen,
        string filter, int page, CancellationToken ct = default)
    {
        const int pageSize = 5;
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / pageSize);
        if (filter is not ("all" or "active" or "finished")) throw new ArgumentOutOfRangeException(nameof(filter));
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        DbValue owner = DbValue.Uuid(member.Id);
        DbValue offset = DbValue.Integer(page * pageSize);
        DbStatement statement = screen switch
        {
            CustomerScreenList.Orders => new("""
                SELECT id,product_title_snapshot,fulfillment_status,NULL::text,NULL::text,created_at,quantity
                FROM shop.orders WHERE user_id=$1 AND ($3='all' OR
                    ((fulfillment_status IN ('ready','completed','cancelled','compensated') OR payment_status IN ('expired','cancelled')) = ($3='finished')))
                ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $2
                """, owner, offset, DbValue.Text(filter)),
            CustomerScreenList.Transactions => new("""
                SELECT id,movement_kind,'',signed_amount::text,'USD',created_at,NULL::integer
                FROM shop.wallet_entries WHERE user_id=$1 AND is_posting
                ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $2
                """, owner, offset),
            CustomerScreenList.Deposits => new("""
                SELECT p.id,p.payment_method,p.status,p.amount_due::text,p.quote_currency,p.created_at,NULL::integer
                FROM shop.payments p LEFT JOIN shop.orders o ON o.id=p.order_id
                WHERE p.topup_user_id=$1 OR o.user_id=$1
                ORDER BY p.created_at DESC,p.id DESC LIMIT 6 OFFSET $2
                """, owner, offset),
            CustomerScreenList.Discounts => new("""
                SELECT id,display_prefix,kind,percent_off::text,'%',created_at,NULL::integer
                FROM shop.discounts WHERE brand_id=$3 AND eligible_user_id=$1 AND active
                    AND (starts_at IS NULL OR starts_at <= clock_timestamp()) AND (ends_at IS NULL OR ends_at > clock_timestamp())
                ORDER BY created_at DESC,id DESC LIMIT 6 OFFSET $2
                """, owner, offset, DbValue.Uuid(context.BrandId)),
            _ => throw new ArgumentOutOfRangeException(nameof(screen))
        };
        IReadOnlyList<CustomerScreenEntry> rows = await session.QueryManyAsync(statement, Row, pageSize + 1, ct);
        return new(rows.Take(pageSize).ToArray(), rows.Count > pageSize);
    }

    public async Task<CustomerScreenEntry?> DetailAsync(BrandContext context, long actor, CustomerScreenList list,
        Guid id, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        BrandUser member = await MemberAsync(session, context, actor, ct);
        DbValue owner = DbValue.Uuid(member.Id);
        DbValue key = DbValue.Uuid(id);
        DbStatement statement = list switch
        {
            CustomerScreenList.Orders => new("""
                SELECT o.id,o.product_title_snapshot,o.fulfillment_status,
                    (SELECT CASE WHEN o.payment_method='wallet' THEN -w.signed_amount ELSE NULL END
                    FROM shop.wallet_entries w WHERE w.order_id=o.id AND w.user_id=$1 AND w.movement_kind='purchase_debit' LIMIT 1)::text,
                    o.currency,o.created_at,o.quantity
                FROM shop.orders o WHERE o.user_id=$1 AND o.id=$2
                """, owner, key),
            CustomerScreenList.Deposits => new("""
                SELECT p.id,p.payment_method,p.status,p.amount_due::text,p.quote_currency,p.created_at,NULL::integer
                FROM shop.payments p LEFT JOIN shop.orders o ON o.id=p.order_id
                WHERE p.id=$2 AND (p.topup_user_id=$1 OR o.user_id=$1)
                """, owner, key),
            _ => throw new ArgumentOutOfRangeException(nameof(list))
        };
        var rows = await session.QueryManyAsync(statement, Row, 1, ct);
        return rows.SingleOrDefault();
    }

    private static decimal Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static CustomerScreenEntry Row(DbRow row) =>
        new(row.GetGuid(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : Amount(row.GetString(3)),
            row.IsDBNull(4) ? null : row.GetString(4), row.GetUtc(5), row.IsDBNull(6) ? null : row.GetInt32(6));

    private static async Task<BrandUser> MemberAsync(DbSession session, BrandContext context, long actor, CancellationToken ct)
    {
        var member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        if (member.Blocked || member.Banned) throw new UnauthorizedAccessException();
        return member;
    }
}
