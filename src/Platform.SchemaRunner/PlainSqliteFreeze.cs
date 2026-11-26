using Microsoft.Data.Sqlite;
using Platform.Migration;
using Platform.Migration.Recovery;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<string> FreezePlainSqliteSourceAsync(string sourcePath, string snapshotPath, CancellationToken ct = default)
    {
        RequireQualification();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        RequirePlainQualificationPath(sourcePath);
        RequirePlainQualificationPath(snapshotPath);
        string directory = Path.GetDirectoryName(snapshotPath)!;
        if (Path.GetDirectoryName(sourcePath) != directory || !sourcePath.EndsWith(".db", StringComparison.Ordinal) ||
            !snapshotPath.EndsWith(".db", StringComparison.Ordinal) || EntryExists(snapshotPath) || EntryExists(sourcePath + "-journal"))
            throw new MigrationRejectedException();
        bool wal = ArchivePrivateFile.RequireSqliteSource(sourcePath);
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            if (wal) ArchivePrivateFile.RequireFile(sourcePath + suffix);
            else if (EntryExists(sourcePath + suffix)) throw new MigrationRejectedException();
        }
        string pending = Path.Combine(directory, ".snapshot-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }
        try
        {
            await using (var source = await OpenPlainConnectionAsync(sourcePath, SqliteOpenMode.ReadOnly, ct))
            await using (var destination = await OpenPlainConnectionAsync(pending, SqliteOpenMode.ReadWrite, ct))
            {
                ct.ThrowIfCancellationRequested();
                source.BackupDatabase(destination);
                await using var command = destination.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                if (await command.ExecuteScalarAsync(ct) is not string mode || mode != "delete") throw new MigrationRejectedException();
            }
            using (var file = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead);
                file.Flush(flushToDisk: true);
            }
            _ = ArchivePrivateFile.FrozenSqlite(pending);
            string digest;
            await using (var frozen = await OpenPlainConnectionAsync(pending, SqliteOpenMode.ReadOnly, ct))
            {
                digest = Convert.ToHexStringLower(await SqlitePlaintextRowCopy.InspectAsync(frozen, ct));
            }
            ct.ThrowIfCancellationRequested();
            ArchivePrivateFile.Publish(pending, snapshotPath);
            return digest;
        }
        finally { File.Delete(pending); }
    }
}
