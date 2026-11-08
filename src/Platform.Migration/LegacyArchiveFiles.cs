using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Security;
namespace Platform.Migration;

public sealed class LegacyArchiveFiles
{
    private readonly string directory;
    private readonly SecretSnapshot secrets;
    public LegacyArchiveFiles(string directory, SecretSnapshot secrets)
    {
        RequireDirectory(directory); this.directory = directory; this.secrets = secrets;
    }
    private static void RequireDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || !Path.GetFileName(directory).StartsWith("teleg-history-archive-", StringComparison.Ordinal) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
    }
    private string PathFor(Guid target, bool zero = false)
    { RequireDirectory(directory); if (target == Guid.Empty) throw new MigrationRejectedException(); return Path.Combine(directory, target.ToString("N") + (zero ? ".zero.archive.json" : ".archive.json")); }
    private static string Reference(VerifiedImportManifest manifest, Guid target, bool zero = false) => "protected:history-archive/" + manifest.Batch.ToString("N") + "/" + target.ToString("N") + (zero ? "/native-zero" : "");
    public Task<ArchivedLegacyRecord> WriteAsync(BrandContext context, VerifiedImportManifest manifest, LegacySourceRecord source, Guid target, IEnumerable<string> legacyKeys, CancellationToken ct = default) => WriteCoreAsync(context, manifest, source, target, legacyKeys, false, ct);
    public Task<ArchivedLegacyRecord> WriteZeroAsync(BrandContext context, VerifiedImportManifest manifest, LegacySourceRecord source, Guid target, CancellationToken ct = default)
    {
        if (source.Projection != LegacyProjection.ZeroTransaction) throw new MigrationRejectedException();
        return WriteCoreAsync(context, manifest, source, target, [], true, ct);
    }
    private async Task<ArchivedLegacyRecord> WriteCoreAsync(BrandContext context, VerifiedImportManifest manifest, LegacySourceRecord source, Guid target, IEnumerable<string> legacyKeys, bool zero, CancellationToken ct)
    {
        manifest.Require(source.Proof(target, context, secrets)); string path = PathFor(target, zero); string[] keys = legacyKeys.ToArray();
        if (!File.Exists(path))
        {
            try
            {
                await new LegacyRecordArchive(secrets).WriteAsync(context, manifest, source, target, async (evidence, token) =>
                {
                    byte[] body = Encode(manifest, evidence); string pending = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
                        await using (var stream = new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                        { await stream.WriteAsync(body, token); await stream.FlushAsync(token); stream.Flush(true); }
                        File.Move(pending, path, false); return Reference(manifest, target, zero);
                    }
                    finally { CryptographicOperations.ZeroMemory(body); if (File.Exists(pending)) File.Delete(pending); }
                }, ct, keys);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        var row = await ReadCoreAsync(context, manifest, target, keys, zero, ct);
        if (row.Source != source) throw new MigrationRejectedException(); return row;
    }
    public Task<ArchivedLegacyRecord> ReadAsync(BrandContext context, VerifiedImportManifest manifest, Guid target, CancellationToken ct = default) => ReadCoreAsync(context, manifest, target, null, false, ct);
    public Task<ArchivedLegacyRecord> ReadAsync(BrandContext context, VerifiedImportManifest manifest, Guid target, IEnumerable<string>? legacyKeys, CancellationToken ct = default) => ReadCoreAsync(context, manifest, target, legacyKeys, false, ct);
    public Task<ArchivedLegacyRecord> ReadZeroAsync(BrandContext context, VerifiedImportManifest manifest, Guid target, CancellationToken ct = default) => ReadCoreAsync(context, manifest, target, null, true, ct);
    private async Task<ArchivedLegacyRecord> ReadCoreAsync(BrandContext context, VerifiedImportManifest manifest, Guid target, IEnumerable<string>? legacyKeys, bool zero, CancellationToken ct)
    {
        string path = PathFor(target, zero);
        if (!OperatingSystem.IsLinux() || context.BrandId != manifest.TargetBrand || !File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length is < 128 or > 16777216 || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        byte[] bytes = await File.ReadAllBytesAsync(path, ct), plain = [];
        try
        {
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            RequireFields(root, ["version", "manifest_sha256", "target", "brand", "projection", "key_id", "ciphertext", "fields", "digest"]);
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("manifest_sha256").GetString() != Convert.ToHexStringLower(manifest.Digest.Span) || root.GetProperty("target").GetGuid() != target || root.GetProperty("brand").GetGuid() != context.BrandId) throw new MigrationRejectedException();
            var projection = (LegacyProjection)root.GetProperty("projection").GetInt32(); if (zero && projection != LegacyProjection.ZeroTransaction) throw new MigrationRejectedException(); string table = LegacySourceRecord.Table(projection);
            var fields = new List<ProtectedLegacyField>(); var array = root.GetProperty("fields");
            if (array.GetArrayLength() > 32) throw new MigrationRejectedException();
            foreach (var field in array.EnumerateArray())
            {
                RequireFields(field, ["field", "key_id", "ciphertext"]);
                fields.Add(new(field.GetProperty("field").GetString()!, field.GetProperty("key_id").GetString()!, field.GetProperty("ciphertext").GetBytesFromBase64()));
            }
            var evidence = new ProtectedLegacyEvidence(target, context.BrandId, projection, root.GetProperty("key_id").GetString()!, root.GetProperty("ciphertext").GetBytesFromBase64(), fields.AsReadOnly());
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(root.GetProperty("digest").GetString()!), LegacyRecordArchive.Digest(evidence))) throw new MigrationRejectedException();
            plain = new BrandCipher(secrets).Decrypt(context, "legacy_id_map", target, table, evidence.KeyId, evidence.Ciphertext.Span);
            string body = new UTF8Encoding(false, true).GetString(plain);
            using var sourceDocument = JsonDocument.Parse(body); var identity = sourceDocument.RootElement.GetProperty(projection == LegacyProjection.DiscountRecord ? "code" : "id");
            string id = identity.ValueKind == JsonValueKind.String ? identity.GetString()! : identity.GetRawText();
            var source = new LegacySourceRecord(projection, id, body); manifest.Require(source.Proof(target, context, secrets));
            var row = new ArchivedLegacyRecord(source, target, Reference(manifest, target, zero), evidence);
            var archive = new LegacyRecordArchive(secrets);
            if (!(legacyKeys is null ? archive.Matches(context, row, row.Evidence) : archive.MatchesRecovered(context, row, legacyKeys))) throw new MigrationRejectedException(); return row;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArithmeticException or ArgumentException or CryptographicException or CipherRejectedException) { throw new MigrationRejectedException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(plain); }
    }
    private static void RequireFields(JsonElement value, string[] names)
    { if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !value.TryGetProperty(n, out _))) throw new MigrationRejectedException(); }
    private static byte[] Encode(VerifiedImportManifest manifest, ProtectedLegacyEvidence evidence)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("manifest_sha256", Convert.ToHexStringLower(manifest.Digest.Span)); writer.WriteString("target", evidence.Target); writer.WriteString("brand", evidence.Brand); writer.WriteNumber("projection", (int)evidence.Projection); writer.WriteString("key_id", evidence.KeyId); writer.WriteBase64String("ciphertext", evidence.Ciphertext.Span); writer.WriteStartArray("fields");
            foreach (var field in evidence.ReencryptedFields ?? []) { writer.WriteStartObject(); writer.WriteString("field", field.Field); writer.WriteString("key_id", field.KeyId); writer.WriteBase64String("ciphertext", field.Ciphertext.Span); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteString("digest", Convert.ToHexStringLower(LegacyRecordArchive.Digest(evidence))); writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}
