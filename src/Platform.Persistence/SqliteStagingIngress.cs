using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteStagingFlow(string State, Guid? Product, Guid? Order, long? Anchor, long Revision);

public sealed class SqliteStagingIngress(SqliteSessionFactory sessions)
{
    public Task<bool> AdmitAsync(BrandContext context, long update, CancellationToken ct = default)
    {
        if (update < 0 || update > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(update));
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                UPDATE brands SET telegram_update_highwater=$update
                WHERE id=$brand AND bot_id=$bot AND active=1 AND telegram_update_highwater<$update
                """, ("$update", update), ("$brand", context.BrandId), ("$bot", context.BotId));
            return await command.ExecuteNonQueryAsync(token) == 1;
        }, ct);
    }

    public Task<long> NextOffsetAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                SELECT telegram_update_highwater FROM brands WHERE id=$brand AND bot_id=$bot AND active=1
                """, ("$brand", context.BrandId), ("$bot", context.BotId));
            object? current = await command.ExecuteScalarAsync(token);
            if (current is not long id) throw new UnauthorizedAccessException("Bot update cursor is unavailable.");
            return checked(id + 1);
        }, ct);

    public Task<SqliteStagingFlow> ReadFlowAsync(BrandContext context, long actor, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) => await ReadAsync(tx, context, actor, token), ct);

    public Task<SqliteStagingFlow> UpdateFlowAsync(BrandContext context, long actor, string state, Guid? product, Guid? order,
        long? anchor, long expectedRevision, CancellationToken ct = default)
    {
        if (state is not ("idle" or "catalog" or "quantity" or "payment" or "support") ||
            expectedRevision < 0 || anchor is <= 0 ||
            (state == "payment" && (product is null || order is null || anchor is null)) ||
            (state is ("quantity" or "catalog") && product is null))
            throw new UnauthorizedAccessException("Invalid staged customer flow.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                UPDATE users SET flow_state=$state,flow_product_id=$product,flow_order_id=$order,
                    flow_anchor_message_id=$anchor,flow_revision=flow_revision+1,
                    flow_expires_at=$expires
                WHERE brand_id=$brand AND telegram_id=$actor AND flow_revision=$revision
                    AND blocked_at IS NULL AND banned_at IS NULL
                """, ("$state", state), ("$product", product), ("$order", order), ("$anchor", anchor),
                ("$expires", state == "idle" ? null : SqliteSchema.UtcText(DateTime.UtcNow.AddMinutes(15))),
                ("$brand", context.BrandId), ("$actor", actor), ("$revision", expectedRevision));
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new UnauthorizedAccessException("Staging flow was superseded.");
            return await ReadAsync(tx, context, actor, token);
        }, ct);
    }

    private static async Task<SqliteStagingFlow> ReadAsync(SqliteTransaction tx, BrandContext context, long actor,
        CancellationToken ct)
    {
        using var command = SqliteWallet.Command(tx, """
            SELECT flow_state,flow_product_id,flow_order_id,flow_anchor_message_id,flow_revision,
                flow_expires_at
            FROM users u JOIN brands b ON b.id=u.brand_id
            WHERE u.brand_id=$brand AND b.bot_id=$bot AND b.active=1 AND u.telegram_id=$actor
                AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """, ("$brand", context.BrandId), ("$bot", context.BotId), ("$actor", actor));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Staging customer is unavailable.");
        string state = row.GetString(0);
        if (!row.IsDBNull(5) && string.CompareOrdinal(row.GetString(5), SqliteSchema.UtcText(DateTime.UtcNow)) <= 0)
            state = "idle";
        return new(state, row.IsDBNull(1) ? null : row.GetGuid(1), row.IsDBNull(2) ? null : row.GetGuid(2),
            row.IsDBNull(3) ? null : row.GetInt64(3), row.GetInt64(4));
    }
}
