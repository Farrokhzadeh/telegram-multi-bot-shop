using System.Globalization;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteAdminOrderStore(SqliteSessionFactory sessions)
{
    public Task<AdminPage<AdminOrder>> SearchAsync(AuthenticatedAdmin actor, BrandContext context,
        Guid? member, Guid? order, Guid? after, int limit, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            AdminCommand.Page(limit);
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using var query = SqliteWallet.Command(tx, """
                SELECT o.id,u.brand_id,o.user_id,o.product_id,o.quantity,o.payment_status,
                    o.fulfillment_status,o.is_preorder,o.hold_at IS NOT NULL,o.review_code,
                    o.revision,o.product_title_snapshot,o.created_at
                FROM orders o JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                WHERE b.id=$brand AND b.bot_id=$bot AND
                    ($member IS NULL OR u.id=$member) AND ($order IS NULL OR o.id=$order)
                    AND ($after IS NULL OR o.id>$after)
                ORDER BY o.id LIMIT $limit
                """, ("$brand", context.BrandId), ("$bot", context.BotId),
                ("$member", member), ("$order", order), ("$after", after),
                ("$limit", limit + 1));
            var list = new List<AdminOrder>();
            await using var row = await query.ExecuteReaderAsync(token);
            while (await row.ReadAsync(token))
                list.Add(new(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2),
                    row.IsDBNull(3) ? null : row.GetGuid(3), row.GetInt32(4),
                    row.GetString(5), row.GetString(6), row.GetBoolean(7),
                    row.GetBoolean(8), row.IsDBNull(9) ? null : row.GetString(9),
                    row.GetInt64(10), row.GetString(11),
                    DateTime.ParseExact(row.GetString(12), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal |
                        DateTimeStyles.AdjustToUniversal)));
            return new AdminPage<AdminOrder>(list.Take(limit).ToArray(),
                list.Count > limit ? list[limit - 1].Id : null);
        }, ct);
}
