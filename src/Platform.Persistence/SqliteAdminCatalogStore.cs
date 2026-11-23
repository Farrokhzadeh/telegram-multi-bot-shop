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
        sessions.RunEfAsync(false, null, (data, token) => BrandAdministrationQueries.ListAsync(data, actor, after, limit, token), ct);

    public Task<BrandPolicyDetail> PolicyAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, (data, token) => BrandAdministrationQueries.ReadAsync(data, actor, context, token), ct);

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
