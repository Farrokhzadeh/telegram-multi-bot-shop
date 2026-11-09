using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Migration;
using Platform.Security;

namespace Platform.SchemaRunner;

internal static class CustomerHistoryCopyQualification
{
    internal static async Task RunAsync(string job, string receipt, Func<string, string> read, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_CUSTOMER_HISTORY_QUALIFICATION") != "1" ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_ARCHIVE_QUALIFICATION") != "1" || job.Length > 16384) throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(job);
        var root = document.RootElement;
        string[] fields = ["version", "source_connection_file", "source_plan_file", "wallet_plan_file", "wallet_state_file", "register_file", "legacy_keys_file", "secrets_directory", "secrets_manifest", "work_directory"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length || fields.Any(f => !root.TryGetProperty(f, out _)) ||
            root.GetProperty("version").GetInt32() != 1) throw new MigrationRejectedException();
        string Value(string name) => root.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
        string directory = Value("work_directory"), keys = Value("secrets_directory"), secretFile = Value("secrets_manifest");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || !Path.GetFileName(directory).StartsWith("teleg-history-archive-", StringComparison.Ordinal) ||
            new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0 ||
            !Path.IsPathFullyQualified(receipt) || Path.GetDirectoryName(receipt) != directory || !receipt.EndsWith(".receipt.json", StringComparison.Ordinal) ||
            File.Exists(receipt) || !Path.IsPathFullyQualified(keys)) throw new MigrationRejectedException();
        using var sourcePlan = JsonDocument.Parse(read(Value("source_plan_file")));
        string archiveHash = sourcePlan.RootElement.GetProperty("archive_sha256").GetString()!, database = sourcePlan.RootElement.GetProperty("database").GetString()!;
        string sourceBrand = WalletCopyQualification.SourceBrand(database, archiveHash);
        string originalPlan = Path.Combine(directory, "archive.plan"); var info = new FileInfo(originalPlan);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 128 or > 16777216 ||
            (File.GetUnixFileMode(originalPlan) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        byte[] body = await File.ReadAllBytesAsync(originalPlan, ct);
        try
        {
            var manifest = VerifiedImportManifest.Open("protected:customer-history/source-archive", SHA256.HashData(body), body);
            if (manifest.SourceBrand != sourceBrand || manifest.SnapshotDigest != archiveHash ||
                manifest.SourceSchemaDigest != sourcePlan.RootElement.GetProperty("schema_sha256").GetString() ||
                manifest.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba") throw new MigrationRejectedException();
            int expectedOrders = sourceBrand switch { "acc-abad" => 4167, "kuala" => 0, "zikotech" => 749, _ => throw new MigrationRejectedException() };
            var originals = manifest.Proofs.Where(p => p.Projection == LegacyProjection.OrderRecord).ToArray();
            if (originals.Length != expectedOrders) throw new MigrationRejectedException();
            using var secretDoc = JsonDocument.Parse(read(Path.Combine(keys, secretFile)));
            Guid deployment = secretDoc.RootElement.GetProperty("deploymentId").GetGuid();
            using var secrets = SecretFiles.Load(keys, secretFile, new SecretLoadGrant(SecretService.JobHost, deployment, 1, [manifest.TargetBrand], false));
            var registry = new BotBrandRegistry([new("protected-customer-history", "qualification", manifest.TargetBrand, 101, deployment, 1, true)]);
            if (!registry.TryResolveAuthenticated("protected-customer-history",
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification")], "protected-customer-history")), out var context))
                throw new MigrationRejectedException();
            var archive = new LegacyArchiveFiles(directory, secrets);
            var records = new List<ArchivedLegacyRecord>(expectedOrders);
            foreach (var proof in originals)
            {
                var row = await archive.ReadAsync(context!, manifest, proof.Target, ct);
                if (row.Source.Projection != LegacyProjection.OrderRecord || row.Source.Id != proof.LegacyId) throw new MigrationRejectedException();
                records.Add(row);
            }
            var memberSnapshots = new LegacyCustomerHistoryProjection(secrets).SealAll(context!, manifest, records);
            int members = manifest.Proofs.Count(p => p.Projection == LegacyProjection.User);
            if (memberSnapshots.Count != members) throw new MigrationRejectedException();
            var sealedReader = new CustomerHistoryArchive(secrets);
            string output = Path.Combine(directory, "teleg-customer-history-reviewed");
            Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var storage = new CustomerHistoryFiles(output, secrets);
            int count = 0;
            foreach (var member in memberSnapshots)
            {
                var original = sealedReader.Open(context!, member.Key, member.Value);
                await storage.PublishAsync(context!, member.Key, member.Value, ct);
                var reread = await storage.ReadAsync(context!, member.Key, ct);
                if (!sealedReader.Open(context!, member.Key, reread.Span).SequenceEqual(original)) throw new MigrationRejectedException();
                int offset = 0;
                while (true)
                {
                    var page = sealedReader.Page(context!, member.Key, reread.Span, offset);
                    if (page.Orders.Count > 20 || !page.Orders.SequenceEqual(original.Skip(offset).Take(20))) throw new MigrationRejectedException();
                    offset = checked(offset + page.Orders.Count);
                    if (page.NextOffset is null) break;
                    if (page.Orders.Count != 20 || page.NextOffset != offset || offset > original.Count) throw new MigrationRejectedException();
                }
                if (offset != original.Count) throw new MigrationRejectedException();
                count = checked(count + original.Count);
            }
            if (count != expectedOrders || memberSnapshots.Count < 2) throw new MigrationRejectedException();
            var first = memberSnapshots.First();
            Guid foreign = memberSnapshots.Keys.First(id => id != first.Key);
            try { _ = sealedReader.Open(context!, foreign, first.Value); throw new MigrationRejectedException(); }
            catch (UnauthorizedAccessException) { }
            await WalletCopyQualification.WriteAsync(receipt, WalletCopyQualification.JsonBody(w =>
            {
                w.WriteString("scope", "protected_customer_visible_history_source_archive_only");
                w.WriteString("source_brand", sourceBrand); w.WriteString("archive_sha256", archiveHash);
                w.WriteNumber("original_orders", count); w.WriteNumber("memberships", members);
                w.WriteBoolean("all_original_orders_accounted", true);
                w.WriteBoolean("per_member_brand_key_encrypted", true);
                w.WriteBoolean("actual_source_history_pagination_verified", true);
                w.WriteBoolean("foreign_member_decryption_rejected", true);
                w.WriteBoolean("unchanged_private_archive", true);
                w.WriteBoolean("source_copy_database_accessed", false);
                w.WriteBoolean("runtime_customer_route_deployed", false);
                w.WriteBoolean("paid_obligations_adjudicated", false); w.WriteBoolean("release_ready", false);
            }), ct);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }
}
