using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Migration;
using Platform.Migration.Recovery;
using Platform.Persistence;
using Platform.Persistence.Migrations;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    private sealed class PlainRuntimeCopy : IDisposable
    {
        private const string SupplierPrefix = "protected:synthetic-stage/supplier-evidence/";
        private readonly string output;
        private readonly string pending;
        private readonly Dictionary<string, TargetAssetReference> references = new(StringComparer.Ordinal);
        private bool published;
        internal string Database => Path.Combine(pending, "shop.db");

        internal PlainRuntimeCopy(string output)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            RequirePlainQualificationPath(output);
            if (!Path.GetFileName(output).StartsWith("runtime-", StringComparison.Ordinal) || EntryExists(output))
                throw new MigrationRejectedException();
            this.output = output;
            pending = Path.Combine(Path.GetDirectoryName(output)!, ".pending-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal void RecordReference(TargetAssetReference reference, TargetConversionAsset asset) =>
            references.Add(asset.ReferenceCommitment, reference);

        internal async Task BackupAsync(string staged, CancellationToken ct)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            using (new FileStream(Database, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            })) { }
            await using var source = await OpenPlainConnectionAsync(staged, SqliteOpenMode.ReadOnly, ct);
            await using var clone = await OpenPlainConnectionAsync(Database, SqliteOpenMode.ReadWrite, ct);
            ct.ThrowIfCancellationRequested();
            source.BackupDatabase(clone);
        }

        internal async Task<string> PublishAsync(TargetConversionManifest manifest, string stagedAssets, TargetConversionReceipt receipt, CancellationToken ct)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in manifest.OrderedAssets())
            {
                ct.ThrowIfCancellationRequested();
                references.TryGetValue(asset.ReferenceCommitment, out var reference);
                if (reference is null && !asset.Detached) throw new MigrationRejectedException();
                string relative = RuntimeAssetPath(asset, reference);
                if (files.TryGetValue(relative, out string? digest) && digest != asset.PlaintextSha256)
                    throw new MigrationRejectedException();
                byte[] bytes = ArchivePrivateFile.Read(AssetPath(stagedAssets, asset, source: false), minimum: 0);
                try
                {
                    if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.PlaintextSha256)
                        throw new MigrationRejectedException();
                    if (files.TryAdd(relative, asset.PlaintextSha256))
                    {
                        string path = Path.Combine(pending, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        await WriteOutputAsync(path, bytes, ct);
                        if (asset.Format == TargetAssetFormat.CustomerHistory)
                        {
                            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead);
                            file.Flush(flushToDisk: true);
                        }
                    }
                    paths.Add(asset.ReferenceCommitment, relative);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            string runtimeSchema = await FinalizeDatabaseAsync(ct);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                if (EntryExists(Database + suffix)) throw new MigrationRejectedException();
            string databaseDigest = Convert.ToHexStringLower(await ArchivePrivateFile.HashRuntimeDatabaseAsync(Database, PlainTargetSchema.SqliteVersion, ct));
            WriteReceipt(manifest, receipt, paths, runtimeSchema, databaseDigest);
            byte[] privateReceipt = ArchivePrivateFile.Read(Path.Combine(pending, "receipt.json"));
            string receiptDigest = Convert.ToHexStringLower(SHA256.HashData(privateReceipt));
            ct.ThrowIfCancellationRequested();
            ArchivePrivateFile.FlushDirectory(pending);
            ArchivePrivateFile.Publish(pending, output);
            published = true;
            return receiptDigest;
        }

        private static string RuntimeAssetPath(TargetConversionAsset asset, TargetAssetReference? reference)
        {
            string? locator = reference?.Reference;
            if (locator is not null && locator.StartsWith(SupplierPrefix, StringComparison.Ordinal))
            {
                string filename = locator[SupplierPrefix.Length..];
                if (asset.Format != TargetAssetFormat.Plain || reference!.Table is not ("supplier_requests" or "supplier_request_attempts") ||
                    !Guid.TryParseExact(filename, "N", out var id) || id == Guid.Empty || filename != id.ToString("N"))
                    throw new MigrationRejectedException();
                return "supplier-evidence/" + filename;
            }
            string directory = asset.Format switch
            {
                TargetAssetFormat.CustomerHistory => "teleg-customer-history-reviewed-v2",
                TargetAssetFormat.PreviewCapture or TargetAssetFormat.PreviewCursor => "state-v2",
                _ => "assets"
            };
            return directory + "/" + AssetFileName(asset, source: false);
        }

        private async Task<string> FinalizeDatabaseAsync(CancellationToken ct)
        {
            byte[] expected = await PlainTargetSchema.ExpectedSqliteSchemaAsync(ct, staged: false);
            await using var connection = await OpenPlainConnectionAsync(Database, SqliteOpenMode.ReadWrite, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            if (await command.ExecuteScalarAsync(ct) is not string mode || mode != "wal")
                throw new MigrationRejectedException();
            string digest;
            await using (var transaction = connection.BeginTransaction(deferred: false))
            {
                command.Transaction = transaction;
                command.CommandText = $"DROP TABLE {PlainTargetSchema.RowCopyTable}; PRAGMA application_id=0;";
                await command.ExecuteNonQueryAsync(ct);
                await SchemaManifest.RequirePlaintextSqliteAsync(command, ct);
                await CheckDatabaseAsync(connection, transaction, ct);
                await SqliteIntegrity.ValidateAsync(connection, transaction, ct);
                byte[] actual = await PlainTargetSchema.SqliteSchemaDigestAsync(connection, transaction, ct);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new MigrationRejectedException();
                digest = Convert.ToHexStringLower(actual);
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
            await CheckpointAsync(connection, ct);
            return digest;
        }

        private void WriteReceipt(TargetConversionManifest manifest, TargetConversionReceipt receipt,
            Dictionary<string, string> paths, string schema, string database)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            using var stream = new FileStream(Path.Combine(pending, "receipt.json"), new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteNumber("format", PlainTargetSchema.SqliteVersion);
            json.WriteString("snapshot_sha256", receipt.SourceSha256);
            json.WriteString("staged_schema_sha256", receipt.TargetSchemaSha256);
            json.WriteString("schema_sha256", schema);
            json.WriteString("database_sha256", database);
            json.WriteString("model_sha256", receipt.ModelSha256);
            json.WriteString("asset_manifest_sha256", receipt.AssetManifestSha256);
            json.WriteNumber("rows", receipt.Rows);
            json.WriteStartArray("assets");
            foreach (var asset in manifest.OrderedAssets())
            {
                json.WriteStartObject();
                json.WriteString("reference_sha256", asset.ReferenceCommitment);
                json.WriteString("file", paths[asset.ReferenceCommitment]);
                json.WriteString("sha256", asset.PlaintextSha256);
                json.WriteEndObject();
                json.Flush();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
            if (stream.Length > 16777216) throw new MigrationRejectedException();
            stream.Flush(flushToDisk: true);
        }

        public void Dispose()
        {
            if (!published && Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        }
    }
}
