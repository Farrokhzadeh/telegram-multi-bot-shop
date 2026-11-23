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
        sessions.RunEfAsync(false, null, async (data, token) => { await AdminSessionStore.RequireActiveAsync(data, actor, null, token); return await CatalogAdministration.FxAsync(data, context, token); }, ct);

    public Task<AdminPage<AdminProduct>> ProductsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) =>
        {
            AdminCommand.Page(limit);
            await AdminSessionStore.RequireActiveAsync(data, actor, null, token);
            var rows = await CatalogAdministration.ProductsAsync(data, after, limit + 1, token);
            return new AdminPage<AdminProduct>(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
        }, ct);

    public Task<AdminPage<InventoryMetadata>> InventoryAsync(AuthenticatedAdmin actor, Guid product, Guid? after,
        int limit, CancellationToken ct = default) =>
        sessions.RunEfAsync(false, null, async (data, token) =>
        {
            AdminCommand.Page(limit);
            await AdminSessionStore.RequireActiveAsync(data, actor, null, token);
            return await CatalogAdministration.InventoryAsync(data, product, after, limit, token);
        }, ct);
}
