using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Migrations;
using Platform.Migration;
using Microsoft.Data.Sqlite;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<string> PreparePlainSqliteConversionAsync(string path, CancellationToken ct = default)
    {
        RequireQualification();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        RequirePlainQualificationPath(path);
        string directory = Path.GetDirectoryName(path)!;
        ArchivePrivateFile.RequireDirectory(directory);
        if (!path.EndsWith(".db", StringComparison.Ordinal) || EntryExists(path) ||
            EntryExists(path + "-wal") || EntryExists(path + "-shm") || EntryExists(path + "-journal"))
            throw new MigrationRejectedException();
        _ = DescribePlain("sqlite");
        string pending = Path.Combine(directory, ".pending-target-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }
        try
        {
            string digest;
            await using (var connection = await OpenPlainConnectionAsync(pending, SqliteOpenMode.ReadWrite, ct))
            await using (var data = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(connection).Options))
            {
                await PlainTargetSchema.CreateAsync(data, ct, forConversion: true);
                await using var snapshot = connection.BeginTransaction(deferred: true);
                await CheckDatabaseAsync(connection, snapshot, ct);
                digest = Convert.ToHexStringLower(await PlainTargetSchema.SqliteSchemaDigestAsync(connection, snapshot, ct));
            }
            ct.ThrowIfCancellationRequested();
            ArchivePrivateFile.Publish(pending, path);
            return digest;
        }
        finally { File.Delete(pending); }
    }

    private static async Task<SqliteConnection> OpenPlainConnectionAsync(string path, SqliteOpenMode mode, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            SqliteSchema.Configure(connection);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static void RequirePlainQualificationPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path ||
            Path.GetFileName(Path.GetDirectoryName(path))?.StartsWith(QualificationPrefix, StringComparison.Ordinal) != true)
            throw new MigrationRejectedException();
    }

    internal static async Task<(string Path, string SchemaSha256)> CreatePlainSqliteQualificationAsync(CancellationToken ct = default)
    {
        RequireQualification();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        _ = DescribePlain("sqlite");
        var directory = Directory.CreateTempSubdirectory(QualificationPrefix);
        File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = Path.Combine(directory.FullName, "shop.db");
        using (new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }

        await using var connection = await OpenQualificationAsync(path, ct, initializeWal: true);
        await using var data = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(connection).Options);
        await PlainTargetSchema.CreateAsync(data, ct);
        byte[] digest;
        await using (var snapshot = connection.BeginTransaction(deferred: true))
        {
            await CheckDatabaseAsync(connection, snapshot, ct);
            digest = await PlainTargetSchema.SqliteSchemaDigestAsync(connection, snapshot, ct);
        }
        await CheckpointAsync(connection, ct);
        return (path, Convert.ToHexStringLower(digest));
    }
}
