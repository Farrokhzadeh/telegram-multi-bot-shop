using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Security;
namespace Platform.SchemaRunner;

internal sealed record ReadOnlySourceArchiveEvidence(long SourceTables, long SourceRows, long OriginalArchiveRecords, long RecoveredFields)
{
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
internal static partial class ReadOnlySourceArchiveVerification
{
    internal static LegacySourceEndpoint ValidateEndpoint(string connection, string host, string database, string archive, string revision, int version, bool granted)
    {
        if (!granted || !OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(host) || Path.GetFileName(host) != "socket" || Path.GetFileName(Path.GetDirectoryName(host))?.StartsWith("teleg-source-qualification-", StringComparison.Ordinal) != true || database is not ("teleg_source_prod1" or "teleg_source_prod2" or "teleg_source_prod3") || archive.Length != 64 || !archive.All(char.IsAsciiHexDigit) || revision != "66371a23a1c8149dc5bdf89a0355aa689d454dba" || version is < 160000 or >= 170000) throw new MigrationRejectedException();
        var value = new NpgsqlConnectionStringBuilder(connection);
        if (value.Host != host || value.Database != database || value.Username != "teleg_source_reader" || value.Port != 5432 || value.IncludeErrorDetail || value.LogParameters || value.Enlist || value.Multiplexing) throw new MigrationRejectedException();
        return new(host, 5432, database, "teleg_source_reader");
    }
    internal static async Task<ReadOnlySourceArchiveEvidence> VerifyAsync(string connection, string sourcePlan, ReadOnlyMemory<byte> archivePlan, string archiveDirectory, string checkpointDirectory, string expectedBrand, BrandContext context, SecretSnapshot secrets, IReadOnlyList<string> legacyKeys, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(checkpointDirectory) || !Directory.Exists(checkpointDirectory) || !Path.GetFileName(checkpointDirectory).StartsWith("teleg-reconcile-checkpoints-", StringComparison.Ordinal) || new DirectoryInfo(checkpointDirectory).LinkTarget is not null || (File.GetUnixFileMode(checkpointDirectory) & (UnixFileMode)63) != 0 || Path.GetFullPath(checkpointDirectory) == Path.GetFullPath(archiveDirectory)) throw new MigrationRejectedException();
        if (Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_RECONCILIATION") != "1" || legacyKeys.Count is < 1 or > 4 || legacyKeys.Any(string.IsNullOrEmpty) || sourcePlan.Length > 1048576 || archivePlan.Length is < 128 or > 16777216) throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(sourcePlan); var plan = document.RootElement;
        string[] names = ["socket", "database", "archive_sha256", "source_revision", "server_version", "schema_sha256", "commitment_key", "tables"];
        if (plan.ValueKind != JsonValueKind.Object || plan.EnumerateObject().Count() != names.Length || plan.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !plan.TryGetProperty(n, out _))) throw new MigrationRejectedException();
        string host = plan.GetProperty("socket").GetString() ?? throw new MigrationRejectedException(), database = plan.GetProperty("database").GetString() ?? throw new MigrationRejectedException(), archive = plan.GetProperty("archive_sha256").GetString() ?? throw new MigrationRejectedException();
        var endpoint = ValidateEndpoint(connection, host, database, archive, plan.GetProperty("source_revision").GetString() ?? "", plan.GetProperty("server_version").GetInt32(), true);
        byte[] schema = Convert.FromHexString(plan.GetProperty("schema_sha256").GetString() ?? ""), key = Convert.FromHexString(plan.GetProperty("commitment_key").GetString() ?? "");
        try
        {
            var expected = plan.GetProperty("tables").EnumerateArray().Select(t => new LegacySourceTableExpectation(t.GetProperty("table").GetString() ?? "", t.GetProperty("key").GetString() ?? "", t.GetProperty("count").GetInt64(), Convert.FromHexString(t.GetProperty("commitment").GetString() ?? ""))).ToArray();
            if (expected.Length != 56 || schema.Length != 32 || key.Length != 32) throw new MigrationRejectedException();
            byte[] frozenPlan = archivePlan.ToArray(); var manifest = VerifiedImportManifest.Open("protected:history-archive/read-only-reconciliation", SHA256.HashData(frozenPlan), frozenPlan);
            if (manifest.SourceBrand != expectedBrand || manifest.TargetBrand != context.BrandId || manifest.SnapshotDigest != archive || manifest.SourceSchemaDigest != Convert.ToHexStringLower(schema) || manifest.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba") throw new MigrationRejectedException();
            var allowed = LegacySourceRecord.Projections.ToHashSet(); var proofs = manifest.Proofs.Where(p => allowed.Contains(p.Projection)).ToArray();
            if (manifest.Proofs.Any(p => p.Projection != LegacyProjection.User && !allowed.Contains(p.Projection)) || proofs.Select(p => p.Target).Distinct().Count() != proofs.Length) throw new MigrationRejectedException();
            var files = new LegacyArchiveFiles(archiveDirectory, secrets); var expectedFiles = proofs.Select(p => p.Target.ToString("N") + ".archive.json").ToHashSet(StringComparer.Ordinal);
            var actualFiles = Directory.EnumerateFiles(archiveDirectory, "*.archive.json", SearchOption.TopDirectoryOnly).Where(p => !p.EndsWith(".zero.archive.json", StringComparison.Ordinal)).Select(Path.GetFileName).ToArray();
            if (actualFiles.Length != expectedFiles.Count || actualFiles.Any(p => p is null || !expectedFiles.Contains(p))) throw new MigrationRejectedException();
            await using var data = new NpgsqlSlimDataSourceBuilder(connection).Build();
            async Task CheckDatabaseAsync()
            {
                await using (var opened = await data.OpenConnectionAsync(ct))
                await using (var transaction = await opened.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
                {
                    await using var set = new NpgsqlCommand("SET TRANSACTION READ ONLY", opened, transaction); await set.ExecuteNonQueryAsync(ct);
                    await using var check = new NpgsqlCommand("SELECT current_database()=$1 AND current_user='teleg_source_reader' AND inet_server_addr() IS NULL AND current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read' AND current_setting('server_version_num')::integer=$2 AND shobj_description(oid,'pg_database')=$3 FROM pg_database WHERE datname=current_database()", opened, transaction);
                    check.Parameters.AddWithValue(database); check.Parameters.AddWithValue(plan.GetProperty("server_version").GetInt32()); check.Parameters.AddWithValue("teleg-offline-copy:" + archive);
                    if (await check.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
                    await ReadOnlySourceRole.RequireAsync(opened, transaction, ct);
                }
            }
            await CheckDatabaseAsync();
            var source = new LegacySourceReader(data, endpoint); var before = await source.VerifyCopyAsync(schema, expected, key, ct);
            _ = await source.VerifyWalletCoverageAsync(manifest, ct);
            string indexPath = Path.Combine(archiveDirectory, "archive-index.sha256");
            byte[] index = await ReadPrivateAsync(indexPath, ct), tool = await ArchiveToolIdentity.CurrentAsync(ct);
            if (index.Length != 64 || index.Any(b => !char.IsAsciiHexDigit((char)b))) throw new MigrationRejectedException();
            string currentKey = secrets.CurrentBrandKeyId(context); byte[] common = CommonBinding(sourcePlan, frozenPlan, index, tool, endpoint, expectedBrand, context.BrandId, currentKey, legacyKeys);
            var batchInputs = new List<(LegacyMapProof[] Proofs, byte[] Digest)>(); long count = 0, fields = 0;
            foreach (var projection in LegacySourceRecord.Projections)
            {
                var selected = proofs.Where(p => p.Projection == projection).OrderBy(p => p.LegacyId, StringComparer.Ordinal).ToArray();
                LegacyMapProof[][] batches = selected.Length == 0 ? [Array.Empty<LegacyMapProof>()] : selected.Chunk(500).ToArray(); bool sourceCovered = false;
                for (int batch = 0; batch < batches.Length; batch++)
                {
                    var members = batches[batch]; byte[] fileSet = await FamilyHashAsync(archiveDirectory, members, ct); batchInputs.Add((members, fileSet));
                    byte[] binding = SHA256.HashData(common.Concat(fileSet).Concat(BitConverter.GetBytes((int)projection)).Concat(BitConverter.GetBytes(batch)).Concat(BitConverter.GetBytes(selected.Length)).ToArray()); string path = Path.Combine(checkpointDirectory, ((int)projection).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + batch.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".checkpoint");
                    ArchiveFamilyCheckpointResult? saved = null;
                    if (File.Exists(path))
                    {
                        byte[] encoded = await ReadPrivateAsync(path, ct); saved = secrets.UseBrandKey(context, currentKey, k => ArchiveFamilyCheckpoint.Open(encoded, (int)projection, binding, k));
                    }
                    var result = await ArchiveFamilyCheckpoint.VerifyAsync(saved, async () =>
                    {
                        if (!sourceCovered)
                        {
                            long sourceCount = await source.VerifyRecordCoverageAsync(context, secrets, manifest, projection, ct);
                            if (sourceCount != selected.LongLength) throw new MigrationRejectedException(); sourceCovered = true;
                        }
                        long recovered = 0;
                        foreach (var proof in members)
                        {
                            var record = await files.ReadAsync(context, manifest, proof.Target, legacyKeys, ct);
                            if (record.Source.Projection != projection || record.Source.Id != proof.LegacyId) throw new MigrationRejectedException();
                            recovered = checked(recovered + (record.Evidence.ReencryptedFields?.Count ?? 0));
                        }
                        byte[] afterBatch = await FamilyHashAsync(archiveDirectory, members, ct); if (!CryptographicOperations.FixedTimeEquals(fileSet, afterBatch)) throw new MigrationRejectedException();
                        return new(members.LongLength, recovered);
                    });
                    if (saved is null)
                    {
                        byte[] encoded = secrets.UseBrandKey(context, currentKey, k => ArchiveFamilyCheckpoint.Create((int)projection, result.Count, result.RecoveredFields, binding, k));
                        await PublishCheckpointAsync(path, encoded, ct);
                    }
                    if (result.Count != members.LongLength || result.RecoveredFields > checked(result.Count * 32)) throw new MigrationRejectedException();
                    count = checked(count + result.Count); fields = checked(fields + result.RecoveredFields);
                }
            }
            var after = await source.VerifyCopyAsync(schema, expected, key, ct);
            await CheckDatabaseAsync();
            byte[] afterIndex = await ReadPrivateAsync(indexPath, ct), afterTool = await ArchiveToolIdentity.CurrentAsync(ct);
            if (before.Count != after.Count || before.Any(x => !after.TryGetValue(x.Key, out var n) || n != x.Value) || count != proofs.LongLength || !CryptographicOperations.FixedTimeEquals(index, afterIndex) || !CryptographicOperations.FixedTimeEquals(tool, afterTool)) throw new MigrationRejectedException();
            foreach (var batch in batchInputs) { byte[] afterBatch = await FamilyHashAsync(archiveDirectory, batch.Proofs, ct); if (!CryptographicOperations.FixedTimeEquals(batch.Digest, afterBatch)) throw new MigrationRejectedException(); }
            return new(before.Count, before.Values.Sum(), count, fields);
        }
        finally { CryptographicOperations.ZeroMemory(schema); CryptographicOperations.ZeroMemory(key); }
    }
}
