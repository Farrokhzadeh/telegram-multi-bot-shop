using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal static class SqliteIntegrity
{
    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA user_version";
        long version = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        if (version != Migrations.PlainTargetSchema.SqliteVersion) return;
        command.CommandText = "PRAGMA foreign_key_check";
        await using (var rows = await command.ExecuteReaderAsync(ct)) if (await rows.ReadAsync(ct)) throw new InvalidOperationException("SQLite commit rejected: foreign_key_check.");
        await using AkantiDbContext data = new SqlitePlainTargetDbContext(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);
        await data.Database.UseTransactionAsync(transaction, ct);
        var ledger = data.Set<WalletEntry>();
        await ValidateChainAsync(ledger, ct);
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
    private static async Task ValidateChainAsync(IQueryable<WalletEntry> ledger, CancellationToken ct)
    {
        Guid? member = null; long version = 0; decimal balance = 0m;
        while (true)
        {
            var rows = await ledger.Where(row => row.IsPosting && (member == null || row.UserId.CompareTo(member.Value) > 0 || row.UserId == member && row.PostingVersion > version)).OrderBy(row => row.UserId).ThenBy(row => row.PostingVersion).Select(row => new { row.UserId, row.PostingVersion, row.SignedAmount, row.BalanceAfter }).Take(128).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (row.UserId != member) { version = 0; balance = 0m; }
                try { balance = checked(balance + row.SignedAmount); version = checked(version + 1); }
                catch (OverflowException) { throw new InvalidOperationException("SQLite commit rejected: wallet_posting_chain."); }
                if (row.PostingVersion != version || row.BalanceAfter != balance) throw new InvalidOperationException("SQLite commit rejected: wallet_posting_chain.");
                member = row.UserId;
            }
            if (rows.Count < 128) return;
        }
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
