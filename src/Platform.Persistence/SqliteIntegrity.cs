using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal static class SqliteIntegrity
{
    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 1) return;
        command.CommandText = "PRAGMA foreign_key_check";
        await using (var rows = await command.ExecuteReaderAsync(ct)) if (await rows.ReadAsync(ct)) throw new InvalidOperationException("SQLite commit rejected: foreign_key_check.");
        await using var data = new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite(connection).Options);
        await data.Database.UseTransactionAsync(transaction, ct);
        var ledger = data.Set<WalletEntry>();
        if (await ledger.AnyAsync(e => e.IsPosting && e.PostingVersion != ledger.LongCount(previous => previous.UserId == e.UserId && previous.IsPosting && previous.PostingVersion <= e.PostingVersion), ct)) throw new InvalidOperationException("SQLite commit rejected: wallet_posting_chain.");
        if (await data.Set<Wallet>().AnyAsync(w => w.Version != ledger.LongCount(e => e.UserId == w.UserId && e.IsPosting), ct)) throw new InvalidOperationException("SQLite commit rejected: wallet_posting_balance.");
        await ValidateRowsAsync(data, data.Set<Wallet>().OrderBy(row => row.UserId), row => row.UserId, ct);
        await ValidateRowsAsync(data, ledger.OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<Order>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<Fulfillment>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<InventoryItem>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<SupplierRequest>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<Payment>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<Discount>().OrderBy(row => row.Id), row => row.Id, ct);
        await ValidateRowsAsync(data, data.Set<DiscountUse>().OrderBy(row => row.OrderId), row => row.OrderId, ct);
        await ValidateRowsAsync(data, data.Set<AuditEvent>().OrderBy(row => row.Id), row => row.Id, ct);
    }
    private static async Task ValidateRowsAsync<T>(AkantiDbContext data, IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, Guid>> key, CancellationToken ct) where T : class
    {
        Guid? after = null; var readKey = key.Compile();
        while (true)
        {
            var filter = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(System.Linq.Expressions.Expression.GreaterThan(System.Linq.Expressions.Expression.Call(key.Body, nameof(Guid.CompareTo), null, System.Linq.Expressions.Expression.Constant(after ?? Guid.Empty)), System.Linq.Expressions.Expression.Constant(0)), key.Parameters);
            var rows = await (after == null ? query : query.Where(filter)).AsNoTracking().Take(128).ToListAsync(ct);
            if (rows.Count == 0) return;
            var integrity = new EfIntegrity(data, () => false);
            foreach (var row in rows) integrity.Capture(row);
            await integrity.ValidateAsync(ct);
            after = readKey(rows[^1]); if (rows.Count < 128) return;
        }
    }
}
