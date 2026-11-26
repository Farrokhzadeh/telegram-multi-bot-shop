using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core;

namespace Platform.Persistence;

public sealed record SqliteStagingCopyReport(int Tables, long Rows);

public static class SqliteStagingCopy
{
    public static async Task<SqliteStagingCopyReport> CopyAsync(string source, string destination,
        CancellationToken ct = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(source);
        return await CopyAsync(dataSource, destination, ct);
    }

    public static async Task<SqliteStagingCopyReport> CopyAsync(NpgsqlDataSource source, string destination,
        CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        if (string.IsNullOrEmpty(builder.Database) ||
            !(builder.Database.StartsWith("shop_qualification_", StringComparison.Ordinal) ||
              builder.Database.StartsWith("shop_staging_", StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Only isolated staging/qualification PostgreSQL sources can be copied.");
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A private absolute SQLite database path ending in .db is required.");
        string target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Destination directory is required.");
        if (!Directory.Exists(parent) || File.Exists(target) || Directory.Exists(target) ||
            File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("A private, empty staging destination is required.");
        string temporary = Path.Combine(parent, "." + Path.GetFileName(target) + ".pending-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await SqliteSchema.InitializeAsync(temporary, ct);
            var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 30
            }.ToString());
            await using (sqlite)
            {
                await sqlite.OpenAsync(ct);
                SqliteSchema.Configure(sqlite);
                await using var postgres = await source.OpenConnectionAsync(ct);
                await using var snapshot = await postgres.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                await using (var readonlyCommand = new NpgsqlCommand("SET TRANSACTION READ ONLY", postgres, snapshot))
                    await readonlyCommand.ExecuteNonQueryAsync(ct);
                string[] tables = await TablesAsync(postgres, snapshot, ct);
                await using var write = sqlite.BeginTransaction(deferred: false);
                await using (var defer = SqliteCommand(write, "PRAGMA defer_foreign_keys=ON"))
                    await defer.ExecuteNonQueryAsync(ct);
                await using var sourceModel = new PostgresAkantiDbContext(new DbContextOptionsBuilder<PostgresAkantiDbContext>().UseNpgsql(postgres).Options);
                await sourceModel.Database.UseTransactionAsync(snapshot, ct);
                await using var targetModel = new SqliteSnapshotDbContext(new DbContextOptionsBuilder<SqliteSnapshotDbContext>().UseSqlite(sqlite).Options);
                await targetModel.Database.UseTransactionAsync(write, ct);
                long rows = 0;
                foreach (string table in tables)
                {
                    ct.ThrowIfCancellationRequested();
                    string[] columns = await ColumnsAsync(postgres, snapshot, table, ct);
                    await ValidateColumnsAsync(write, table, columns, ct);
                    string[] key = await PrimaryKeyAsync(postgres, snapshot, table, ct);
                    if (key.Length == 0 || key.Any(k => !columns.Contains(k, StringComparer.Ordinal)))
                        throw new InvalidOperationException("The source table has no qualified primary key.");
                    rows = checked(rows + await SnapshotCopyQueries.CopyAsync(sourceModel, targetModel, table, columns, key, HashValue, ConvertValue, ct));
                }
                await SqliteIntegrity.ValidateAsync(sqlite, write, ct);
                using (var check = SqliteCommand(write, "PRAGMA integrity_check"))
                {
                    object? result = await check.ExecuteScalarAsync(ct);
                    if (!string.Equals(result as string, "ok", StringComparison.Ordinal))
                        throw new InvalidOperationException("SQLite integrity_check rejected the staging snapshot.");
                }
                ct.ThrowIfCancellationRequested();
                await write.CommitAsync(CancellationToken.None);
                await snapshot.CommitAsync(ct);
                using (var checkpoint = sqlite.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    await using var result = await checkpoint.ExecuteReaderAsync(ct);
                    if (!await result.ReadAsync(ct) || result.GetInt32(0) != 0)
                        throw new InvalidOperationException("SQLite WAL could not be checkpointed before publication.");
                }
                await sqlite.CloseAsync();
                if (File.Exists(target)) throw new IOException("Staging destination changed during copying.");
                File.Move(temporary, target);
                return new(tables.Length, rows);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporary + "-wal")) File.Delete(temporary + "-wal");
            if (File.Exists(temporary + "-shm")) File.Delete(temporary + "-shm");
        }
    }

    private static async Task<string[]> TablesAsync(NpgsqlConnection connection, NpgsqlTransaction snapshot, CancellationToken ct)
    {
        using var resource = typeof(SqliteSchema).Assembly.GetManifestResourceStream("Platform.Persistence.Migrations.Sqlite.0001_core.sql")
            ?? throw new InvalidOperationException("Canonical SQLite schema is missing.");
        using var reader = new StreamReader(resource);
        string schema = await reader.ReadToEndAsync(ct);
        string[] tables = Regex.Matches(schema, @"(?m)^CREATE TABLE ([a-z_]+) \(")
            .Select(match => match.Groups[1].Value).ToArray();
        if (tables.Length == 0 || tables.Distinct(StringComparer.Ordinal).Count() != tables.Length)
            throw new InvalidOperationException("SQLite schema table inventory is unavailable.");
        using var command = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables WHERE table_schema='shop' AND table_type='BASE TABLE'
            """, connection, snapshot);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var source = new HashSet<string>(StringComparer.Ordinal);
        while (await rows.ReadAsync(ct)) source.Add(rows.GetString(0));
        if (!source.SetEquals(tables)) throw new InvalidOperationException("Staging source and SQLite table inventories differ.");
        return tables;
    }

    private static async Task<string[]> ColumnsAsync(NpgsqlConnection connection, NpgsqlTransaction snapshot, string table,
        CancellationToken ct)
    {
        using var command = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema='shop' AND table_name=$1 ORDER BY ordinal_position
            """, connection, snapshot);
        command.Parameters.AddWithValue(table);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rows.ReadAsync(ct)) names.Add(Identifier(rows.GetString(0)));
        if (names.Count == 0) throw new InvalidOperationException("Missing staging table column inventory.");
        return names.ToArray();
    }

    private static async Task<string[]> PrimaryKeyAsync(NpgsqlConnection connection, NpgsqlTransaction snapshot, string table,
        CancellationToken ct)
    {
        using var command = new NpgsqlCommand("""
            SELECT k.column_name FROM information_schema.table_constraints c
            JOIN information_schema.key_column_usage k ON k.constraint_name=c.constraint_name
                AND k.table_schema=c.table_schema AND k.table_name=c.table_name
            WHERE c.table_schema='shop' AND c.table_name=$1 AND c.constraint_type='PRIMARY KEY'
            ORDER BY k.ordinal_position
            """, connection, snapshot);
        command.Parameters.AddWithValue(table);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rows.ReadAsync(ct)) names.Add(Identifier(rows.GetString(0)));
        return names.ToArray();
    }

    private static async Task ValidateColumnsAsync(SqliteTransaction tx, string table, string[] sourceColumns,
        CancellationToken ct)
    {
        using var command = SqliteCommand(tx, "PRAGMA table_info(" + Identifier(table) + ")");
        await using var rows = await command.ExecuteReaderAsync(ct);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await rows.ReadAsync(ct)) columns.Add(rows.GetString(1));
        if (!columns.SetEquals(sourceColumns))
            throw new InvalidOperationException("Staging table columns do not match SQLite schema.");
    }

    internal static object? ConvertValue(object? value) => value switch
    {
        null or DBNull => null,
        bool boolean => boolean ? 1L : 0L,
        Guid id => id.ToString("D"),
        decimal amount => ExactDecimal.Format(amount),
        DateTime date => SqliteSchema.UtcText(date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime()),
        DateTimeOffset timestamp => SqliteSchema.UtcText(timestamp.UtcDateTime),
        int integer => (long)integer,
        short integer => (long)integer,
        byte integer => (long)integer,
        string text => text,
        long number => number,
        byte[] bytes => bytes,
        _ => throw new NotSupportedException("Unrecognized staging column type.")
    };

    internal static void HashValue(IncrementalHash hash, object? value)
    {
        byte tag;
        byte[] bytes;
        switch (value)
        {
            case null or DBNull:
                tag = 0; bytes = []; break;
            case byte[] blob:
                tag = 1; bytes = blob; break;
            case string text:
                tag = 2; bytes = Encoding.UTF8.GetBytes(text); break;
            case long number:
                tag = 3; bytes = Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture)); break;
            default:
                throw new InvalidOperationException("Unrecognized SQLite copy verification type.");
        }
        hash.AppendData([tag]);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string Identifier(string name) =>
        Regex.IsMatch(name, @"\A[a-z_][a-z0-9_]*\z", RegexOptions.CultureInvariant)
            ? name : throw new InvalidOperationException("Invalid staging schema identifier.");

    private static SqliteCommand SqliteCommand(SqliteTransaction transaction, string sql)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
