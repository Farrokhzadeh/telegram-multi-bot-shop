using Platform.Core;
using Platform.Security;
using Npgsql;

namespace Platform.Persistence;

public sealed class AiContextStore
{
    private readonly DbSessionFactory reads;
    public AiContextStore(NpgsqlDataSource data) => reads = new(data, new DbSessionOptions { ReadOnly = true });
    public async Task<AiShopContext?> ReadAsync(AiReadScope scope, CancellationToken ct = default)
    {
        if (!scope.IsActive) throw new UnauthorizedAccessException();
        await using var session = await reads.BeginAsync(ct);
        var members = await session.QueryManyAsync(new DbStatement("""
            SELECT u.id,b.public_name,CASE WHEN split_part(lower(u.language),'-',1) IN ('en','fa') THEN split_part(lower(u.language),'-',1)
                WHEN b.default_language IN ('en','fa') THEN b.default_language ELSE 'en' END,
                b.support_enabled AND a.disabled_at IS NULL AND a.telegram_user_id IS NOT NULL AND a.public_contact_handle IS NOT NULL,
                CASE WHEN b.support_enabled AND a.disabled_at IS NULL AND a.telegram_user_id IS NOT NULL THEN a.public_contact_handle END
            FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id LEFT JOIN shop.admins a ON a.id=b.customer_support_admin_id
            WHERE u.brand_id=$1 AND b.bot_id=$2 AND u.telegram_id=$3 AND b.active AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, DbValue.Uuid(scope.Brand.BrandId), DbValue.Bigint(scope.Brand.BotId), DbValue.Bigint(scope.ActorId)), r => (Member: r.GetGuid(0), Brand: r.GetString(1), Language: r.GetString(2), Support: r.GetBoolean(3), Contact: r.IsDBNull(4) ? null : r.GetString(4)), 1, ct);
        if (members.Count != 1) return null;
        var member = members[0]; AiOrderContext? order = null;
        if (scope.OrderId is Guid id)
        {
            var orders = await session.QueryManyAsync(new DbStatement("SELECT id,product_title_snapshot,payment_status,fulfillment_status FROM shop.orders WHERE id=$1 AND user_id=$2", DbValue.Uuid(id), DbValue.Uuid(member.Member)), r => new AiOrderContext(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)), 1, ct);
            if (orders.Count != 1) return null; order = orders[0];
        }
        if (!scope.IsActive) throw new UnauthorizedAccessException();
        return new(1, scope.Brand.BrandId, member.Brand, member.Language, member.Support, member.Contact, order);
    }
}
