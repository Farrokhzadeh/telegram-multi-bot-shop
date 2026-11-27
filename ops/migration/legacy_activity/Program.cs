using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Migration;
using Platform.SchemaRunner;
using Platform.Security;

int stage = 0;
try
{
    if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_LEGACY_ACTIVITY") != "1" || args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !Path.GetFileName(args[0]).StartsWith("teleg-activity-qualification-", StringComparison.Ordinal)) throw new MigrationRejectedException();
    string root = args[0], input = Path.Combine(root, "inputs"); byte[] sourceBytes = ArchivePrivateFile.Read(Path.Combine(input, "source-plan.json")), originalBytes = ArchivePrivateFile.Read(Path.Combine(input, "original.plan"));
    stage = 1;
    using var sourceDocument = JsonDocument.Parse(sourceBytes); var source = sourceDocument.RootElement;
    var original = VerifiedImportManifest.Open("protected:activity/original-memberships", SHA256.HashData(originalBytes), originalBytes);
    if (original.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba" || original.SourceRevision != source.GetProperty("source_revision").GetString() || original.SnapshotDigest != source.GetProperty("archive_sha256").GetString() || original.SourceSchemaDigest != source.GetProperty("schema_sha256").GetString()) throw new MigrationRejectedException();
    stage = 2;
    using var secretDocument = JsonDocument.Parse(ArchivePrivateFile.Read(Path.Combine(input, "keys", "service-config.json"))); var configuration = secretDocument.RootElement;
    Guid deployment = configuration.GetProperty("deploymentId").GetGuid(); long revision = configuration.GetProperty("revision").GetInt64();
    using var secrets = RecoverySecretFiles.Load(Path.Combine(input, "keys"), "service-config.json", new(SecretService.JobHost, deployment, revision, [original.TargetBrand], false));
    stage = 3;
    var registry = new BotBrandRegistry([new("protected-activity", "qualification", original.TargetBrand, 101, deployment, revision, true)]);
    if (!registry.TryResolveAuthenticated("protected-activity", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification")], "protected-activity")), out var context)) throw new MigrationRejectedException();
    stage = 4;
    byte[] commitmentKey = Convert.FromHexString(source.GetProperty("commitment_key").GetString()!);
    List<(string Id, string Body)> Rows(string table)
    {
        var expected = source.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("table").GetString() == table);
        if (expected.GetProperty("key").GetString() != "id") throw new MigrationRejectedException();
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, commitmentKey);
        void Add(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length); hash.AppendData(size); hash.AppendData(bytes); CryptographicOperations.ZeroMemory(bytes); }
        Add("accabad-source-table-v1"); Add(table); Add("id");
        var rows = new List<(string Id, string Body)>(); string last = ""; long bytes = 0;
        string[] files = Directory.GetFiles(input, table + ".*.jsonl").Order(StringComparer.Ordinal).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetFileName(files[i]) != table + "." + i.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl") throw new MigrationRejectedException();
            byte[] part = ArchivePrivateFile.Read(files[i]);
            try
            {
                using var reader = new StringReader(new UTF8Encoding(false, true).GetString(part)); int count = 0;
                while (reader.ReadLine() is string line)
                {
                    using var document = JsonDocument.Parse(line); var value = document.RootElement;
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2) throw new MigrationRejectedException();
                    string id = value[0].GetString()!, body = value[1].GetString()!;
                    if (string.CompareOrdinal(id, last) <= 0 || id.Length > 128 || body.Length > 1048576 || ++count > 500 || rows.Count >= 100000 || (bytes += Encoding.UTF8.GetByteCount(body)) > 134217728) throw new MigrationRejectedException();
                    using var record = JsonDocument.Parse(body); if (record.RootElement.GetProperty("id").GetRawText() != id) throw new MigrationRejectedException();
                    Add(id); Add(body); rows.Add((id, body)); last = id;
                }
                if (count == 0 || i < files.Length - 1 && count != 500) throw new MigrationRejectedException();
            }
            finally { CryptographicOperations.ZeroMemory(part); }
        }
        if (rows.Count != expected.GetProperty("count").GetInt64() || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected.GetProperty("commitment").GetString()!))) throw new MigrationRejectedException();
        return rows;
    }
    try
    {
        var members = original.Proofs.Where(p => p.Projection == LegacyProjection.User).ToArray();
        if (!Rows("users").Select(r => r.Id).ToHashSet(StringComparer.Ordinal).SetEquals(members.Select(p => p.LegacyId))) throw new MigrationRejectedException();
        stage = 5;
        Guid Target(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-activity-preservation-v1:" + original.TargetBrand.ToString("N") + ":" + original.SnapshotDigest + ":" + id)).AsSpan(0, 16));
        var rows = Rows("user_activity").Select(r => new LegacySourceRecord(LegacyProjection.UserActivityRecord, r.Id, r.Body)).ToArray();
        stage = 6;
        byte[] plan = VerifiedImportManifest.EncodePlan(original.Destination, original.SourceBrand, original.TargetBrand, original.SourceRevision, Convert.FromHexString(original.SourceSchemaDigest), original.SnapshotReference, Convert.FromHexString(original.SnapshotDigest), original.Batch, members.Concat(rows.Select(r => r.Proof(Target(r.Id), context!, secrets))));
        void Publish(string path, byte[] bytes)
        {
            if (File.Exists(path)) { if (!ArchivePrivateFile.Read(path).AsSpan().SequenceEqual(bytes)) throw new MigrationRejectedException(); }
            else { using var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }); file.Write(bytes); file.Flush(true); }
        }
        stage = 7;
        Publish(Path.Combine(root, "activity.plan"), plan);
        var manifest = VerifiedImportManifest.Open("protected:activity/preservation", SHA256.HashData(plan), plan);
        string archiveRoot = Path.Combine(root, "teleg-history-archive-activity"); Directory.CreateDirectory(archiveRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var archive = new LegacyArchiveFiles(archiveRoot, secrets); var review = new LegacyActivityReconciliation(context!, secrets, manifest);
        long[] reasons = new long[Enum.GetValues<LegacyActivityException>().Length]; long bound = 0;
        using var index = new MemoryStream(); using var indexWriter = new BinaryWriter(index, Encoding.UTF8, true); indexWriter.Write(rows.Length);
        stage = 8;
        foreach (var row in rows)
        {
            Guid target = Target(row.Id); var archived = await archive.WriteAsync(context!, manifest, row, target, []);
            var loaded = await new LegacyArchiveFiles(archiveRoot, secrets).ReadAsync(context!, manifest, target);
            if (loaded.Source != row || !archived.Digest.AsSpan().SequenceEqual(loaded.Digest)) throw new MigrationRejectedException();
            var disposition = review.Review(loaded); reasons[(int)disposition.Exception]++; if (disposition.Member is not null) bound++;
            if (disposition.OperationalEventImported) throw new MigrationRejectedException();
            indexWriter.Write(target.ToByteArray()); indexWriter.Write(row.Id); indexWriter.Write(disposition.OccurredAt.Ticks); indexWriter.Write((disposition.Member ?? Guid.Empty).ToByteArray()); indexWriter.Write((int)disposition.Exception); indexWriter.Write(loaded.Digest);
        }
        stage = 9;
        indexWriter.Flush(); Publish(Path.Combine(root, "dispositions.private.bin"), index.ToArray());
        if (Directory.GetFiles(archiveRoot).Length != rows.Length || Directory.GetDirectories(archiveRoot).Length != 0 || reasons.Sum() != rows.Length || Rows("user_activity").Count != rows.Length || Rows("users").Count != members.Length || !ArchivePrivateFile.Read(Path.Combine(input, "source-plan.json")).AsSpan().SequenceEqual(sourceBytes) || !ArchivePrivateFile.Read(Path.Combine(input, "original.plan")).AsSpan().SequenceEqual(originalBytes)) throw new MigrationRejectedException();
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteNumber("source_rows", rows.Length); writer.WriteNumber("bound_membership_rows", bound); writer.WriteNumber("protected_exception_rows", reasons.Sum()); writer.WriteNumber("operational_events_imported", 0); writer.WriteStartObject("exceptions"); foreach (var reason in Enum.GetValues<LegacyActivityException>()) writer.WriteNumber(reason.ToString(), reasons[(int)reason]); writer.WriteEndObject(); writer.WriteBoolean("source_commitments_matched_before_after", true); writer.WriteBoolean("originals_reloaded", true); writer.WriteBoolean("release_ready", false); writer.WriteEndObject();
        }
        Publish(Path.Combine(root, "result.redacted.json"), output.ToArray());
    }
    finally { CryptographicOperations.ZeroMemory(commitmentKey); CryptographicOperations.ZeroMemory(sourceBytes); CryptographicOperations.ZeroMemory(originalBytes); }
    return 0;
}
catch { Console.Error.WriteLine("Protected legacy activity qualification rejected at stage " + stage.ToString(CultureInfo.InvariantCulture) + "."); return 78; }
