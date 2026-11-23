using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public static class SqliteAdminSessionStore
{
    public static async Task<AuthenticatedAdmin> AuthenticateAsync(SqliteSessionFactory sessions, AdminToken token, CancellationToken ct = default)
    {
        byte[] hash = token.Hash();
        try { return await sessions.RunEfAsync(false, null, (data, cancel) => AdminSessionStore.AuthenticateAsync(data, hash, cancel), ct); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(hash); }
    }
    internal static Task RequireActiveAsync(SqliteTransaction tx, AuthenticatedAdmin actor, CancellationToken ct) =>
        SqliteSessionFactory.ReadEfAsync(tx, async (data, cancel) => { await AdminSessionStore.RequireActiveAsync(data, actor, null, cancel); return true; }, ct);
}

public sealed class SqliteAdminCatalogStore(SqliteSessionFactory sessions)
{
    public Task<AdminPage<AdminBrand>> BrandsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            AdminCommand.Page(limit);
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using var query = SqliteWallet.Command(tx, """
                SELECT id,public_name,market,default_language,active,policy_version
                FROM brands WHERE ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit
                """, ("$after", after), ("$limit", limit + 1));
            var items = new List<AdminBrand>();
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                items.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                    rows.GetBoolean(4), rows.GetInt64(5)));
            return new AdminPage<AdminBrand>(items.Take(limit).ToArray(),
                items.Count > limit ? items[limit - 1].Id : null);
        }, ct);

    public Task<BrandPolicyDetail> PolicyAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            BrandPolicyEdit value;
            bool require;
            long revision;
            using (var query = SqliteWallet.Command(tx, """
                SELECT public_name,default_language,active,allow_wallet,allow_crypto,allow_bank,allow_wallet_topup,
                    zero_balance_topup,support_enabled,customer_support_admin_id,fx_enabled,logo_asset_ref,
                    require_channel_join,policy_version
                FROM brands WHERE id=$brand AND bot_id=$bot
                """, ("$brand", context.BrandId), ("$bot", context.BotId)))
            await using (var row = await query.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token)) throw new UnauthorizedAccessException();
                value = new(row.GetString(0), row.GetString(1), row.GetBoolean(2), row.GetBoolean(3),
                    row.GetBoolean(4), row.GetBoolean(5), row.GetBoolean(6), row.GetBoolean(7),
                    row.GetBoolean(8), row.IsDBNull(9) ? null : row.GetGuid(9), row.GetBoolean(10),
                    row.IsDBNull(11) ? null : row.GetString(11));
                require = row.GetBoolean(12);
                revision = row.GetInt64(13);
            }
            var channels = new List<RequiredChannel>();
            using (var query = SqliteWallet.Command(tx, """
                SELECT chat_id,title,invite_link FROM brand_channels
                WHERE brand_id=$brand AND active=1 AND required=1 ORDER BY chat_id
                """, ("$brand", context.BrandId)))
            await using (var row = await query.ExecuteReaderAsync(token))
                while (await row.ReadAsync(token))
                {
                    if (channels.Count >= 1000) throw new InvalidOperationException("Channel result limit exceeded.");
                    channels.Add(new(row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1),
                        row.IsDBNull(2) ? null : row.GetString(2)));
                }
            return new BrandPolicyDetail(value, require, channels.ToArray(), revision);
        }, ct);

    public Task<ExchangeRateMetadata> FxAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using var query = SqliteWallet.Command(tx, """
                SELECT current_fx,fx_source,fx_fetched_at,fx_error_code
                FROM brands WHERE id=$brand AND bot_id=$bot
                """, ("$brand", context.BrandId), ("$bot", context.BotId));
            await using var row = await query.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new UnauthorizedAccessException();
            return new ExchangeRateMetadata(row.IsDBNull(0) ? null : row.GetString(0),
                row.IsDBNull(1) ? null : row.GetString(1),
                row.IsDBNull(2) ? null : Utc(row.GetString(2)),
                row.IsDBNull(3) ? null : row.GetString(3));
        }, ct);

    public Task<AdminPage<AdminProduct>> ProductsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (connection, tx, token) =>
        {
            AdminCommand.Page(limit);
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            var options = new DbContextOptionsBuilder<AdminProductQuery>().UseSqlite(connection).Options;
            await using var catalog = new AdminProductQuery(options);
            await catalog.Database.UseTransactionAsync(tx, token);
            var products = await catalog.ListAsync(after, limit + 1, token);
            return new AdminPage<AdminProduct>(products.Take(limit).ToArray(),
                products.Count > limit ? products[limit - 1].Id : null);
        }, ct);

    public Task<AdminPage<InventoryMetadata>> InventoryAsync(AuthenticatedAdmin actor, Guid product, Guid? after,
        int limit, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            AdminCommand.Page(limit);
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using var query = SqliteWallet.Command(tx, """
                SELECT id,product_id,state,origin_kind,created_at
                FROM inventory_items WHERE product_id=$product AND ($after IS NULL OR id>$after)
                ORDER BY id LIMIT $limit
                """, ("$product", product), ("$after", after), ("$limit", limit + 1));
            var items = new List<InventoryMetadata>();
            await using var row = await query.ExecuteReaderAsync(token);
            while (await row.ReadAsync(token))
                items.Add(new(row.GetGuid(0), row.GetGuid(1), row.GetString(2), row.GetString(3), Utc(row.GetString(4))));
            return new AdminPage<InventoryMetadata>(items.Take(limit).ToArray(),
                items.Count > limit ? items[limit - 1].Id : null);
        }, ct);

    private static DateTime Utc(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
