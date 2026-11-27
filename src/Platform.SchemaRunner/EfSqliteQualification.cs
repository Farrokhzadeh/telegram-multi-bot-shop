using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Persistence;

namespace Platform.SchemaRunner;

public sealed record EfSqliteQualificationReceipt(string Path, string SchemaSha256, string ScriptSha256, bool HistoryWritten);

public static partial class EfSchemaCommand
{
    private const string QualificationPrefix = "akanti-ef-qualification-";
    public static async Task<EfSqliteQualificationReceipt> CreateSqliteQualificationAsync(string expectedScriptSha256, CancellationToken ct = default)
    {
        RequireQualification();
        var description = RequireSqliteScript(expectedScriptSha256);
        var directory = Directory.CreateTempSubdirectory(QualificationPrefix);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = System.IO.Path.Combine(directory.FullName, "shop.db");
        var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsLinux()) file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(path, file)) { }
        CryptographicOperations.ZeroMemory(ArchivePrivateFile.Read(path, 0, 0));
        SchemaState state;
        await using (var connection = await OpenQualificationAsync(path, ct, true))
        {
            await using (var context = Context(connection))
            {
                await context.Database.MigrateAsync(ct);
                if ((await context.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("The qualified schema is incomplete.");
            }
            state = await ReadSchemaAsync(connection, null, ct);
            await CheckDatabaseAsync(connection, null, ct);
            await CheckpointAsync(connection, ct);
        }
        CryptographicOperations.ZeroMemory(ArchivePrivateFile.Read(path, 512));
        return new(path, state.Hash, description.ScriptSha256, true);
    }

    public static async Task<EfSqliteQualificationReceipt> AdoptSqliteQualificationAsync(string path, string expectedScriptSha256, CancellationToken ct = default)
    {
        RequireQualification();
        var description = RequireSqliteScript(expectedScriptSha256);
        EfSqliteQualificationReceipt? reference = null;
        try
        {
            await using var connection = await OpenQualificationAsync(path, ct);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var actual = await ReadSchemaAsync(connection, transaction, ct);
            reference = await CreateSqliteQualificationAsync(expectedScriptSha256, ct);
            SchemaState expected;
            await using (var comparison = await OpenQualificationAsync(reference.Path, ct)) expected = await ReadSchemaAsync(comparison, null, ct);
            if (actual.Hash != expected.Hash || (actual.History is not null && actual.History != expected.History) ||
                (actual.Lock is not null && actual.Lock != expected.Lock)) throw new InvalidOperationException("The qualification schema identity differs from the reviewed model.");
            await CheckDatabaseAsync(connection, transaction, ct);
            await SqliteSchema.ValidateIntegrityAsync(transaction, ct);
            await using var context = Context(connection);
            await context.Database.UseTransactionAsync(transaction, ct);
            var history = context.GetService<IHistoryRepository>();
            string[] migrations = context.Database.GetMigrations().ToArray();
            string version = context.GetService<IDesignTimeModel>().Model.FindAnnotation("ProductVersion")?.Value as string
                ?? throw new InvalidOperationException("The EF model version is unavailable.");
            var applied = await history.GetAppliedMigrationsAsync(ct);
            if (applied.Count != 0 && (!applied.Select(row => row.MigrationId).SequenceEqual(migrations, StringComparer.Ordinal) ||
                applied.Any(row => row.ProductVersion != version))) throw new InvalidOperationException("Migration ownership differs from the reviewed model.");
            bool written = applied.Count == 0;
            if (written)
            {
                await history.CreateIfNotExistsAsync(ct);
                foreach (string migration in migrations)
                    await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(migration, version)), ct);
            }
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            await context.Database.UseTransactionAsync(null, ct);
            await CheckpointAsync(connection, ct);
            return new(path, actual.Hash, description.ScriptSha256, written);
        }
        finally
        {
            if (reference is not null) Directory.Delete(System.IO.Path.GetDirectoryName(reference.Path)!, true);
        }
    }

    private sealed record SchemaState(string Hash, string? History, string? Lock);
    private static void RequireQualification()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1")
            throw new UnauthorizedAccessException("Explicit isolated synthetic qualification mode is required.");
    }

    private static EfSchemaDescription RequireSqliteScript(string expected)
    {
        var description = Describe("sqlite");
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(description.ScriptSha256)))
            throw new InvalidOperationException("The reviewed EF script digest differs.");
        return description;
    }

    private static async Task<SqliteConnection> OpenQualificationAsync(string path, CancellationToken ct, bool initializeWal = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux qualification is required.");
        string full = System.IO.Path.GetFullPath(path);
        string? parent = System.IO.Path.GetDirectoryName(full);
        string relative = System.IO.Path.GetRelativePath(System.IO.Path.GetTempPath(), full);
        if (!System.IO.Path.IsPathFullyQualified(path) || path != full || parent is null ||
            System.IO.Path.GetFileName(full) != "shop.db" || !System.IO.Path.GetFileName(parent).StartsWith(QualificationPrefix, StringComparison.Ordinal) ||
            relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || new DirectoryInfo(parent).LinkTarget is not null ||
            (File.GetUnixFileMode(parent) & (UnixFileMode)63) != 0) throw new UnauthorizedAccessException("Only a private qualification database is accepted.");
        CryptographicOperations.ZeroMemory(ArchivePrivateFile.Read(path, 0));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            SqliteSchema.Configure(connection);
            await using var journal = connection.CreateCommand();
            journal.CommandText = initializeWal ? "PRAGMA journal_mode=WAL" : "PRAGMA journal_mode";
            if (!string.Equals((string?)await journal.ExecuteScalarAsync(ct), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WAL qualification failed.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static SqlitePlainTargetDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);

    private static async Task<SchemaState> ReadSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? history = null, migrationLock = null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(3)) throw new InvalidOperationException("An unqualified schema object is present.");
                string name = reader.GetString(1), definition = reader.GetString(3);
                if (name is "__EFMigrationsHistory" or "__EFMigrationsLock")
                {
                    if (reader.GetString(0) != "table") throw new InvalidOperationException("Migration ownership metadata is invalid.");
                    if (name == "__EFMigrationsHistory") history = definition; else migrationLock = definition;
                }
                else for (int i = 0; i < 4; i++) Append(hash, reader.GetString(i));
            }
        command.CommandText = "PRAGMA user_version";
        Append(hash, ((long)(await command.ExecuteScalarAsync(ct))!).ToString(CultureInfo.InvariantCulture));
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), history, migrationLock);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static async Task CheckDatabaseAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_keys";
        if ((long)(await command.ExecuteScalarAsync(ct))! != 1) throw new InvalidOperationException("Foreign-key enforcement is required.");
        command.CommandText = "PRAGMA integrity_check";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(ct), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("SQLite integrity qualification failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        await using var violations = await command.ExecuteReaderAsync(ct);
        if (await violations.ReadAsync(ct)) throw new InvalidOperationException("SQLite reference qualification failed.");
    }

    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var result = await command.ExecuteReaderAsync(ct);
        if (!await result.ReadAsync(ct) || result.GetInt64(0) != 0) throw new InvalidOperationException("The qualification WAL checkpoint is busy.");
    }
}
