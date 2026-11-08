using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Security;
namespace Platform.SchemaRunner;

internal static class ArchiveCopyQualification
{
    internal static async Task RunAsync(string job, string receipt, Func<string, string> read, CancellationToken ct = default, string? targetConnectionFile = null)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_ARCHIVE_QUALIFICATION") != "1" || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || job.Length > 16384) throw new MigrationRejectedException();
        if (targetConnectionFile is not null && Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_WALLET_ORDER_QUALIFICATION") != "1") throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(job); var root = document.RootElement;
        string[] names = ["version", "source_connection_file", "source_plan_file", "wallet_plan_file", "wallet_state_file", "register_file", "legacy_keys_file", "secrets_directory", "secrets_manifest", "work_directory"];
        if (root.EnumerateObject().Count() != names.Length || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !root.TryGetProperty(n, out _)) || root.GetProperty("version").GetInt32() != 1) throw new MigrationRejectedException();
        string Value(string name) => root.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
        string directory = Value("work_directory"), connection = read(Value("source_connection_file")).Trim(), plan = read(Value("source_plan_file")), walletPath = Value("wallet_plan_file"), walletState = read(Value("wallet_state_file")), registerText = read(Value("register_file")), legacyText = read(Value("legacy_keys_file"));
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || !Path.GetFileName(directory).StartsWith("teleg-history-archive-", StringComparison.Ordinal) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0 || !Path.IsPathFullyQualified(receipt) || Path.GetDirectoryName(receipt) != directory || !receipt.EndsWith(".receipt.json", StringComparison.Ordinal) || File.Exists(receipt) || registerText.Length > 67108864 || legacyText.Length > 16384 || !Path.IsPathFullyQualified(walletPath) || new FileInfo(walletPath).Length > 16777216 || new FileInfo(walletPath).LinkTarget is not null || (File.GetUnixFileMode(walletPath) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        byte[] walletBytes = await File.ReadAllBytesAsync(walletPath, ct), register = Encoding.UTF8.GetBytes(registerText);
        using var sourcePlan = JsonDocument.Parse(plan); var sourceRoot = sourcePlan.RootElement;
        string archiveHash = sourceRoot.GetProperty("archive_sha256").GetString()!, database = sourceRoot.GetProperty("database").GetString()!, brandName = WalletCopyQualification.SourceBrand(database, archiveHash);
        var wallet = VerifiedImportManifest.Open("protected:history-archive/verified-wallet-plan", SHA256.HashData(walletBytes), walletBytes);
        if (wallet.SourceBrand != brandName || wallet.SnapshotDigest != archiveHash || wallet.SourceSchemaDigest != sourceRoot.GetProperty("schema_sha256").GetString() || wallet.SourceRevision != "66371a23a1c8149dc5bdf89a0355aa689d454dba") throw new MigrationRejectedException();
        Guid brand = new(SHA256.HashData(Encoding.UTF8.GetBytes("protected-wallet-copy-brand-v1:" + brandName)).AsSpan(0, 16));
        if (wallet.TargetBrand != brand) throw new MigrationRejectedException();
        using var stateDocument = JsonDocument.Parse(walletState); var stateRoot = stateDocument.RootElement;
        if (stateRoot.GetProperty("version").GetInt32() != 1 || stateRoot.GetProperty("batch").GetGuid() != wallet.Batch) throw new MigrationRejectedException();
        DateTime observed = DateTime.ParseExact(stateRoot.GetProperty("observed_at").GetString()!, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        using var legacyDocument = JsonDocument.Parse(legacyText); var legacyRoot = legacyDocument.RootElement;
        if (legacyRoot.EnumerateObject().Count() != 2 || legacyRoot.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2 || legacyRoot.GetProperty("version").GetInt32() != 1 || legacyRoot.GetProperty("keys").GetArrayLength() is < 1 or > 4) throw new MigrationRejectedException();
        string[] legacyKeys = legacyRoot.GetProperty("keys").EnumerateArray().Select(k => k.GetString() ?? throw new MigrationRejectedException()).ToArray();
        if (legacyKeys.Any(k => k.Length is < 1 or > 4096) || legacyKeys.Distinct(StringComparer.Ordinal).Count() != legacyKeys.Length) throw new MigrationRejectedException();
        string statePath = Path.Combine(directory, "state.json"); Guid batch;
        byte[] inputHash = SHA256.HashData(Encoding.UTF8.GetBytes(job + "\n" + connection + "\n" + plan + "\n" + walletState + "\n" + Convert.ToHexStringLower(SHA256.HashData(walletBytes)) + "\n" + Convert.ToHexStringLower(SHA256.HashData(register)) + "\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyText)))));
        if (File.Exists(statePath))
        {
            using var state = JsonDocument.Parse(read(statePath));
            if (state.RootElement.GetProperty("version").GetInt32() != 1 || state.RootElement.GetProperty("input_sha256").GetString() != Convert.ToHexStringLower(inputHash)) throw new MigrationRejectedException();
            batch = state.RootElement.GetProperty("batch").GetGuid();
        }
        else
        {
            using var secretManifest = JsonDocument.Parse(read(Path.Combine(Value("secrets_directory"), Value("secrets_manifest")))); batch = secretManifest.RootElement.GetProperty("deploymentId").GetGuid();
            await WalletCopyQualification.WriteAsync(statePath, WalletCopyQualification.JsonBody(w => { w.WriteNumber("version", 1); w.WriteString("input_sha256", Convert.ToHexStringLower(inputHash)); w.WriteString("batch", batch); }), ct);
        }
        if (batch == Guid.Empty) throw new MigrationRejectedException();
        using var secrets = SecretFiles.Load(Value("secrets_directory"), Value("secrets_manifest"), new SecretLoadGrant(SecretService.JobHost, batch, 1, [brand], false));
        var registry = new BotBrandRegistry([new("protected-archive-copy", "qualification", brand, 101, batch, 1, true)]);
        if (!registry.TryResolveAuthenticated("protected-archive-copy", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification")], "protected-archive-copy")), out var context)) throw new MigrationRejectedException();
        string keyState = Path.Combine(directory, "target-keys.sha256");
        string keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(read(Path.Combine(Value("secrets_directory"), Value("secrets_manifest"))) + ":" + secrets.CurrentBrandKeyId(context!) + ":" + secrets.UseBrandKey(context!, secrets.CurrentBrandKeyId(context!), key => Convert.ToHexStringLower(SHA256.HashData(key))))));
        if (File.Exists(keyState)) { if (read(keyState) != keyHash) throw new MigrationRejectedException(); }
        else await WalletCopyQualification.WriteAsync(keyState, Encoding.UTF8.GetBytes(keyHash), ct);
        try
        {
            var records = new List<LegacySourceRecord>(); long bytes = 0;
            ValueTask Collect(string table, string id, string body, CancellationToken token)
            {
                var row = LegacySourceRecord.CopyCandidate(table, id, body);
                if (row is not null) { bytes += Encoding.UTF8.GetByteCount(row.Body); if (records.Count >= 100000 || bytes > 67108864) throw new MigrationRejectedException(); records.Add(row); }
                return ValueTask.CompletedTask;
            }
            await SourceCopyInspection.RunAsync(connection, plan, Path.Combine(directory, "source-start-" + Guid.NewGuid().ToString("N") + ".json"), ct, visit: Collect);
            var options = new NpgsqlConnectionStringBuilder(connection); await using var data = new NpgsqlSlimDataSourceBuilder(connection).Build();
            var source = new LegacySourceReader(data, new(options.Host!, options.Port, options.Database!, options.Username!));
            var retained = LegacyWalletRetention.OpenAll("protected:wallet-copy/original-native-history", SHA256.HashData(register), register, brandName, archiveHash);
            var wallets = await source.PlanWalletsAsync(Convert.FromHexString(wallet.SourceSchemaDigest), wallet.Batch, brand, retained, observed, ct);
            foreach (var member in wallets) foreach (var proof in member.Proofs(brand)) wallet.Require(proof);
            if (wallets.Count != wallet.Proofs.Count(p => p.Projection == LegacyProjection.User) || wallets.Sum(r => r.Proofs(brand).Count) != wallet.Proofs.Count) throw new MigrationRejectedException();
            Guid Target(LegacySourceRecord row) => new(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-history-archive-v1:" + batch.ToString("N") + ":" + brand.ToString("N") + ":" + row.Projection + ":" + row.Id)).AsSpan(0, 16));
            byte[] body = VerifiedImportManifest.EncodePlan(wallet.Destination, brandName, brand, wallet.SourceRevision, Convert.FromHexString(wallet.SourceSchemaDigest), wallet.SnapshotReference, Convert.FromHexString(wallet.SnapshotDigest), batch, wallet.Proofs.Where(p => p.Projection == LegacyProjection.User).Concat(records.Select(r => r.Proof(Target(r), context!, secrets))));
            string manifestPath = Path.Combine(directory, "archive.plan");
            if (File.Exists(manifestPath)) { if (new FileInfo(manifestPath).LinkTarget is not null || (File.GetUnixFileMode(manifestPath) & (UnixFileMode)63) != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, ct)), SHA256.HashData(body))) throw new MigrationRejectedException(); }
            else await WalletCopyQualification.WriteAsync(manifestPath, body, ct);
            var manifest = VerifiedImportManifest.Open("protected:history-archive/" + batch.ToString("N"), SHA256.HashData(body), body);
            foreach (var projection in LegacySourceRecord.Projections) await source.VerifyRecordCoverageAsync(context!, secrets, manifest, projection, ct);
            var loadedRecords = new List<ArchivedLegacyRecord>();
            var files = new LegacyArchiveFiles(directory, secrets); long fields = 0, orders = 0, incomplete = 0; var reader = new LegacyReadOnlyOrderHistory(secrets);
            var owners = manifest.Proofs.Where(p => p.Projection == LegacyProjection.User).ToDictionary(p => p.LegacyId, p => p.Target, StringComparer.Ordinal);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var row in records.OrderBy(r => r.Projection).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                Guid target = Target(row); var first = await files.WriteAsync(context!, manifest, row, target, legacyKeys, ct);
                var replay = await files.WriteAsync(context!, manifest, row, target, legacyKeys, ct); var loaded = await new LegacyArchiveFiles(directory, secrets).ReadAsync(context!, manifest, target, legacyKeys, ct);
                var targetOnly = await files.ReadAsync(context!, manifest, target, ct);
                if (!CryptographicOperations.FixedTimeEquals(first.Digest, targetOnly.Digest) || !CryptographicOperations.FixedTimeEquals(first.Digest, replay.Digest) || !CryptographicOperations.FixedTimeEquals(first.Digest, loaded.Digest) || loaded.Source != row) throw new MigrationRejectedException();
                loadedRecords.Add(loaded);
                digest.AppendData(first.Digest); fields += first.Evidence.ReencryptedFields?.Count ?? 0;
                if (row.Projection == LegacyProjection.OrderRecord)
                {
                    using var original = JsonDocument.Parse(row.Body); string owner = original.RootElement.GetProperty("user_id").GetInt64().ToString(CultureInfo.InvariantCulture);
                    if (!owners.TryGetValue(owner, out Guid member)) throw new MigrationRejectedException();
                    var view = reader.Read(context!, manifest, member, loaded, loaded.Evidence);
                    if (!view.ReadOnly || view.ImportApproved || view.SettlementVerified || view.DeliveryVerified) throw new MigrationRejectedException();
                    orders++; if (view.IncompleteTerms) incomplete++;
                }
            }
            byte[] archiveDigest = digest.GetHashAndReset(); string index = Path.Combine(directory, "archive-index.sha256");
            if (File.Exists(index)) { if (read(index) != Convert.ToHexStringLower(archiveDigest)) throw new MigrationRejectedException(); }
            else await WalletCopyQualification.WriteAsync(index, Encoding.UTF8.GetBytes(Convert.ToHexStringLower(archiveDigest)), ct);
            var typed = targetConnectionFile is null ? null : await WalletOrderCopyQualification.RunAsync(read(targetConnectionFile).Trim(), directory, context!, secrets, source, manifest, wallet, wallets, loadedRecords, Value("register_file"), read, ct);
            await SourceCopyInspection.RunAsync(connection, plan, Path.Combine(directory, "source-end-" + Guid.NewGuid().ToString("N") + ".json"), ct);
            await WalletCopyQualification.WriteAsync(receipt, WalletCopyQualification.JsonBody(w =>
            {
                w.WriteString("scope", typed is null ? "protected_source_history_archive_only" : "protected_wallet_orders_with_complete_archive_review"); w.WriteString("source_brand", brandName); w.WriteString("archive_sha256", archiveHash); w.WriteNumber("memberships", wallets.Count); w.WriteNumber("archived_records", records.Count); w.WriteNumber("reencrypted_fields", fields); w.WriteNumber("read_only_orders", orders); w.WriteNumber("incomplete_order_terms", incomplete); w.WriteBoolean("replay_identical", true); w.WriteBoolean("source_unchanged", true); w.WriteBoolean("canonical_commerce_imported", typed is not null && typed.Imported > 0); w.WriteBoolean("paid_obligations_verified", false); w.WriteBoolean("release_ready", false); w.WriteStartObject("record_counts");
                foreach (var projection in LegacySourceRecord.Projections) w.WriteNumber(projection.ToString(), records.Count(r => r.Projection == projection));
                w.WriteEndObject();
                if (typed is not null) { w.WriteNumber("typed_wallet_orders", typed.Imported); w.WriteNumber("typed_historical_topups", typed.Topups); w.WriteNumber("typed_paid_direct_orders", typed.Direct); w.WriteNumber("typed_inactive_master_catalog", typed.Catalog); w.WriteNumber("review_records", typed.Review); w.WriteBoolean("typed_wallet_order_parity", true); }
            }), ct);
        }
        finally { CryptographicOperations.ZeroMemory(walletBytes); CryptographicOperations.ZeroMemory(register); }
    }
}
