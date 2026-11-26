using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Platform.Migration;
using Platform.Migration.Recovery;
using Platform.Persistence;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<(string Snapshot, string References, long FileReferences, long DatabaseManifests)> InspectPlainSqliteSourceAsync(string path, string? inventory, CancellationToken ct = default)
    {
        RequireQualification();
        RequirePlainQualificationPath(path);
        if (inventory is not null && (Path.GetDirectoryName(inventory) != Path.GetDirectoryName(path) ||
            Path.GetFullPath(inventory) != inventory || !inventory.EndsWith(".json", StringComparison.Ordinal) || EntryExists(inventory)))
            throw new MigrationRejectedException();

        var before = ArchivePrivateFile.FrozenSqlite(path);
        using var output = inventory is null ? null : new PrivateReferenceInventory(inventory);
        await using var source = await OpenPlainConnectionAsync(path, SqliteOpenMode.ReadOnly, ct);
        using var references = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(references, "akanti-sqlite-asset-references-v1");
        long fileReferences = 0, databaseManifests = 0;
        byte[] snapshot = await SqlitePlaintextRowCopy.InspectAsync(source, ct, entry =>
        {
            TargetAssetReferences.Hash(references, entry);
            output?.Write(entry);
            if (entry.DatabaseManifest) databaseManifests = checked(databaseManifests + 1);
            else fileReferences = checked(fileReferences + 1);
        });
        if (ArchivePrivateFile.FrozenSqlite(path) != before) throw new MigrationRejectedException();
        output?.Publish();
        return (Convert.ToHexStringLower(snapshot), Convert.ToHexStringLower(references.GetHashAndReset()), fileReferences, databaseManifests);
    }

    private sealed class PrivateReferenceInventory : IDisposable
    {
        private readonly string destination, pending;
        private readonly FileStream stream;
        private readonly Utf8JsonWriter writer;

        internal PrivateReferenceInventory(string destination)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            this.destination = destination;
            pending = Path.Combine(Path.GetDirectoryName(destination)!, ".inventory-" + Guid.NewGuid().ToString("N"));
            stream = new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            writer = new Utf8JsonWriter(stream);
            writer.WriteStartArray();
        }

        internal void Write(TargetAssetReference asset)
        {
            writer.WriteStartObject();
            writer.WriteString("reference_sha256", TargetConversionManifest.ReferenceCommitment(asset));
            writer.WriteString("table", asset.Table);
            writer.WriteString("field", asset.Field);
            writer.WriteString("row_sha256", asset.RowCommitment);
            writer.WriteString("reference", asset.Reference);
            writer.WriteBoolean("database_manifest", asset.DatabaseManifest);
            writer.WriteString("plaintext_sha256", asset.PayloadSha256);
            writer.WriteEndObject();
            writer.Flush();
        }

        internal void Publish()
        {
            writer.WriteEndArray();
            writer.Flush();
            stream.Flush(flushToDisk: true);
            ArchivePrivateFile.Publish(pending, destination);
        }

        public void Dispose()
        {
            try { writer.Dispose(); }
            finally { stream.Dispose(); File.Delete(pending); }
        }
    }
}
