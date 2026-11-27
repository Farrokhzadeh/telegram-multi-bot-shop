using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;
using Platform.Security;
namespace Platform.SchemaRunner;

internal static class ReadOnlySourceArchiveCommand
{
    internal static async Task RunAsync(string job, string checkpointDirectory, string receipt, Func<string, string> read, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(receipt) || File.Exists(receipt) || !receipt.EndsWith(".receipt.json", StringComparison.Ordinal)) throw new MigrationRejectedException();
        string? output = Path.GetDirectoryName(receipt);
        using var document = JsonDocument.Parse(job);
        if (output is null || !Directory.Exists(output) || new DirectoryInfo(output).LinkTarget is not null || (File.GetUnixFileMode(output) & (UnixFileMode)63) != 0 || output == document.RootElement.GetProperty("work_directory").GetString()) throw new MigrationRejectedException();
        var result = await VerifyJobAsync(job, checkpointDirectory, read, ct);
        await WalletCopyQualification.WriteAsync(receipt, WalletCopyQualification.JsonBody(w =>
        {
            w.WriteString("scope", "read_only_source_archive_preservation_verification"); w.WriteNumber("source_tables", result.SourceTables); w.WriteNumber("source_rows", result.SourceRows); w.WriteNumber("original_archive_records", result.OriginalArchiveRecords); w.WriteNumber("recovered_fields", result.RecoveredFields); w.WriteBoolean("financial_obligations_verified", false); w.WriteBoolean("release_ready", false);
        }), ct);
    }
    internal static async Task<ReadOnlySourceArchiveEvidence> VerifyJobAsync(string job, string checkpointDirectory, Func<string, string> read, CancellationToken ct = default, Func<LegacySourceReader, VerifiedImportManifest, BrandContext, SecretSnapshot, CancellationToken, Task>? verifyTarget = null)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_RECONCILIATION") != "1" || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || job.Length > 16384) throw new MigrationRejectedException();
        using var jobDocument = JsonDocument.Parse(job); var item = jobDocument.RootElement;
        string[] names = ["version", "source_connection_file", "source_plan_file", "wallet_plan_file", "wallet_state_file", "register_file", "legacy_keys_file", "secrets_directory", "secrets_manifest", "work_directory"];
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != names.Length || item.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !item.TryGetProperty(n, out _)) || item.GetProperty("version").GetInt32() != 1) throw new MigrationRejectedException();
        string Value(string name) => item.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
        string directory = Value("work_directory"), connection = read(Value("source_connection_file")).Trim(), sourcePlan = read(Value("source_plan_file")), secretDirectory = Value("secrets_directory"), secretManifest = Value("secrets_manifest");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || !Path.GetFileName(directory).StartsWith("teleg-history-archive-", StringComparison.Ordinal) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        string archiveFile = Path.Combine(directory, "archive.plan");
        byte[] bytes = ArchivePrivateFile.Read(archiveFile, 128);
        try
        {
            using var sourceDocument = JsonDocument.Parse(sourcePlan); var source = sourceDocument.RootElement;
            string database = source.GetProperty("database").GetString() ?? throw new MigrationRejectedException(), archive = source.GetProperty("archive_sha256").GetString() ?? throw new MigrationRejectedException();
            string brandName = WalletCopyQualification.SourceBrand(database, archive);
            var manifest = VerifiedImportManifest.Open("protected:history-archive/read-only-reconciliation", SHA256.HashData(bytes), bytes);
            if (manifest.SourceBrand != brandName) throw new MigrationRejectedException();
            using var secretDocument = JsonDocument.Parse(read(Path.Combine(secretDirectory, secretManifest))); var secretRoot = secretDocument.RootElement;
            Guid deployment = secretRoot.GetProperty("deploymentId").GetGuid(); var brands = secretRoot.GetProperty("brands");
            if (brands.GetArrayLength() != 1 || brands[0].GetProperty("brandId").GetGuid() != manifest.TargetBrand || deployment == Guid.Empty) throw new MigrationRejectedException();
            using var legacyDocument = JsonDocument.Parse(read(Value("legacy_keys_file"))); var legacy = legacyDocument.RootElement;
            if (legacy.ValueKind != JsonValueKind.Object || legacy.EnumerateObject().Count() != 2 || legacy.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2 || legacy.GetProperty("version").GetInt32() != 1 || legacy.GetProperty("keys").GetArrayLength() is < 1 or > 4) throw new MigrationRejectedException();
            string[] keys = legacy.GetProperty("keys").EnumerateArray().Select(k => k.GetString() ?? throw new MigrationRejectedException()).ToArray();
            if (keys.Any(k => k.Length is < 1 or > 4096) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new MigrationRejectedException();
            using var secrets = RecoverySecretFiles.Load(secretDirectory, secretManifest, new SecretLoadGrant(SecretService.JobHost, deployment, 1, [manifest.TargetBrand], false));
            var registry = new BotBrandRegistry([new("protected-archive-copy", "qualification", manifest.TargetBrand, WalletOrderCopyQualification.SharedBotId(brandName), deployment, 1, true)]);
            if (!registry.TryResolveAuthenticated("protected-archive-copy", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification")], "protected-archive-copy")), out var context) || context is null) throw new MigrationRejectedException();
            return await ReadOnlySourceArchiveVerification.VerifyAsync(connection, sourcePlan, bytes, directory, checkpointDirectory, brandName, context, secrets, keys, ct, verifyTarget is null ? null : (sourceReader, original, token) => verifyTarget(sourceReader, original, context, secrets, token));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
