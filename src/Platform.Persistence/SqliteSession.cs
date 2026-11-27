using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed partial class SqliteSessionFactory
{
    private readonly string connectionString;
    private readonly int timeoutSeconds;

    public static SqliteSessionFactory ForPlaintext(string path, int timeoutSeconds = 5) => new(path, timeoutSeconds);

    internal SqliteSessionFactory(string path, int timeoutSeconds = 5)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute SQLite database path is required.", nameof(path));
        if (timeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        this.timeoutSeconds = timeoutSeconds;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = timeoutSeconds
        }.ToString();
    }

    internal async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var journal = connection.CreateCommand();
        await SchemaManifest.RequirePlaintextSqliteAsync(journal, ct);
        journal.CommandText = "PRAGMA journal_mode";
        if (!string.Equals((string?)await journal.ExecuteScalarAsync(ct), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SQLite database must be initialized in WAL mode.");
        if (write)
        {
            journal.CommandText = "PRAGMA recursive_triggers=ON";
            await journal.ExecuteNonQueryAsync(ct);
        }
        else
        {
            journal.CommandText = "PRAGMA query_only=ON";
            await journal.ExecuteNonQueryAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: !write);
        efTransactions.Add(transaction, new EfTransaction(write, timeoutSeconds));
        try
        {
            var result = await operation(connection, transaction, ct);
            ct.ThrowIfCancellationRequested();
            await ValidateEfAsync(transaction, ct);
            if (write) await SqliteIntegrity.ValidateAsync(connection, transaction, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        finally { efTransactions.Remove(transaction); }
    }

    internal Task<List<AdminProduct>> ListAdminProductsAsync(Guid? after, int count, CancellationToken ct) =>
        RunEfAsync(false, null, (data, token) => CatalogAdministration.ProductsAsync(data, after, count, token), ct);
}
