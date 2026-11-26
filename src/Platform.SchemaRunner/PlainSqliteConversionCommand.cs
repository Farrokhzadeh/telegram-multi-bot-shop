using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Migration;
using Platform.Migration.Recovery;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Persistence.Migrations;
using Platform.Security;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<TargetConversionReceipt> RunPlainSqliteConversionAsync(string job, bool verify, CancellationToken ct = default, string? runtimeCopy = null)
    {
        RequireQualification();
        RequirePlainQualificationPath(job);
        string directory = Path.GetDirectoryName(job)!;
        ArchivePrivateFile.RequireDirectory(directory);
        if (runtimeCopy is not null && (!verify || Path.GetDirectoryName(runtimeCopy) != directory))
            throw new MigrationRejectedException();
        byte[] bytes = ArchivePrivateFile.Read(job);
        byte[]? previewKey = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            RequireFields(root, "version", "source", "destination", "source_assets", "destination_assets", "snapshot_sha256", "target_schema_sha256", "keys", "brands", "assets");
            if (root.GetProperty("version").GetInt32() != 1) throw new MigrationRejectedException();
            string Child(string property) => Path.Combine(directory, Leaf(root, property));
            string sourcePath = Child("source"), destinationPath = Child("destination");
            string sourceAssets = Child("source_assets"), destinationAssets = Child("destination_assets");
            if (!sourcePath.EndsWith(".db", StringComparison.Ordinal) || !destinationPath.EndsWith(".db", StringComparison.Ordinal) ||
                !job.EndsWith(".json", StringComparison.Ordinal) ||
                new[] { job, sourcePath, destinationPath, sourceAssets, destinationAssets }.Distinct(StringComparer.Ordinal).Count() != 5)
                throw new MigrationRejectedException();
            ArchivePrivateFile.RequireDirectory(sourceAssets);
            ArchivePrivateFile.RequireDirectory(destinationAssets);
            var frozen = ArchivePrivateFile.FrozenSqlite(sourcePath);
            ArchivePrivateFile.RequireFile(destinationPath);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                if (EntryExists(destinationPath + suffix)) ArchivePrivateFile.RequireFile(destinationPath + suffix);

            var keySpec = root.GetProperty("keys");
            RequireFields(keySpec, "directory", "manifest", "deployment", "revision", "preview");
            string keyDirectory = Path.Combine(directory, Leaf(keySpec, "directory"));
            if (keyDirectory == sourceAssets || keyDirectory == destinationAssets) throw new MigrationRejectedException();
            ArchivePrivateFile.RequireDirectory(keyDirectory);
            Guid deployment = keySpec.GetProperty("deployment").GetGuid();
            long revision = keySpec.GetProperty("revision").GetInt64();
            var brands = new Dictionary<Guid, BotBrandBinding>();
            foreach (var brand in root.GetProperty("brands").EnumerateArray())
            {
                RequireFields(brand, "id", "bot_id");
                Guid id = brand.GetProperty("id").GetGuid();
                string subject = id.ToString("D");
                if (brands.Count >= 32 || !brands.TryAdd(id, new("offline-recovery", subject, id, brand.GetProperty("bot_id").GetInt64(), deployment, revision, true)))
                    throw new MigrationRejectedException();
            }
            var registry = new BotBrandRegistry(brands.Values);
            var contexts = brands.Values.ToDictionary(brand => brand.BrandId, brand =>
                registry.TryResolveAuthenticated(brand.Scheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, brand.Subject)], brand.Scheme)), out var scope)
                    ? scope! : throw new MigrationRejectedException());
            using var keys = SecretFiles.Load(keyDirectory, Leaf(keySpec, "manifest"), new(SecretService.JobHost, deployment, revision, brands.Keys, true));

            var manifest = new TargetConversionManifest(Value(root, "snapshot_sha256"), root.GetProperty("assets").EnumerateArray().Select(ReadAsset));
            byte[] schema = Convert.FromHexString(Value(root, "target_schema_sha256"));
            byte[] expectedSchema = await PlainTargetSchema.ExpectedSqliteSchemaAsync(ct, staged: true);
            if (!CryptographicOperations.FixedTimeEquals(schema, expectedSchema)) throw new MigrationRejectedException();
            bool hasPreview = manifest.OrderedAssets().Any(asset => asset.Format == TargetAssetFormat.PreviewCapture);
            if (hasPreview) previewKey = ArchivePrivateFile.Read(Path.Combine(keyDirectory, Leaf(keySpec, "preview")), 32, 32);
            else if (keySpec.GetProperty("preview").ValueKind != JsonValueKind.Null) throw new MigrationRejectedException();
            using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            identityHash.AppendData(bytes);
            if (previewKey is not null) identityHash.AppendData(previewKey);
            string assetIdentity = Convert.ToHexStringLower(identityHash.GetHashAndReset());
            using var conversionLock = verify && runtimeCopy is null ? null : TakeLock(destinationPath + ".conversion-lock");
            using var assetLock = verify && runtimeCopy is null ? null : TakeLock(destinationAssets + ".asset-conversion-lock");
            CheckAssetDirectory(sourceAssets, manifest, source: true, complete: true, resume: false);
            CheckAssetDirectory(destinationAssets, manifest, source: false, complete: verify, resume: !verify);
            var frozenAssets = manifest.OrderedAssets().Select(asset => AssetPath(sourceAssets, asset, source: true))
                .ToDictionary(path => path, ArchivePrivateFile.FrozenAsset, StringComparer.Ordinal);
            await using var source = await OpenPlainConnectionAsync(sourcePath, SqliteOpenMode.ReadOnly, ct);
            using var bundle = runtimeCopy is null ? null : new PlainRuntimeCopy(runtimeCopy);
            if (bundle is not null) await bundle.BackupAsync(destinationPath, ct);
            await using var destination = await OpenPlainConnectionAsync(bundle?.Database ?? destinationPath, verify ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, ct);
            BrandContext Scope(Guid id) => contexts.TryGetValue(id, out var scope) ? scope : throw new MigrationRejectedException();
            await ValidateAssetScopesAsync(source, contexts.Values, manifest, ct);
            var conversion = new TargetPlaintextConversion(keys, Scope);
            var receipt = await conversion.RunAsync(source, destination, manifest, schema, assetIdentity,
                (asset, _) => Task.FromResult(ArchivePrivateFile.Read(AssetPath(sourceAssets, asset, source: true), minimum: 0)),
                (asset, stored) => asset.Format switch
                {
                    TargetAssetFormat.Plain => TargetPlaintextAssets.Plain(asset, stored.Span),
                    TargetAssetFormat.PaymentEvidence => TargetPlaintextAssets.PaymentEvidence(asset, keys, Scope(asset.Brand), asset.Row, asset.KeyId!, stored.Span),
                    TargetAssetFormat.CustomerHistory => TargetPlaintextAssets.CustomerHistory(asset, keys, Scope(asset.Brand), asset.Row, stored.Span),
                    TargetAssetFormat.PreviewCapture => TargetPlaintextAssets.PreviewCapture(asset, previewKey!, asset.UpdateId, stored.Span),
                    TargetAssetFormat.PreviewCursor => TargetPlaintextAssets.PreviewCursor(asset, stored.Span),
                    _ => throw new MigrationRejectedException()
                },
                (asset, _) => Task.FromResult(ReadOutput(AssetPath(destinationAssets, asset, source: false))),
                verify ? null : (asset, content, token) => WriteOutputAsync(AssetPath(destinationAssets, asset, source: false), content, token),
                () =>
                {
                    if (ArchivePrivateFile.FrozenSqlite(sourcePath) != frozen) throw new MigrationRejectedException();
                    CheckAssetDirectory(sourceAssets, manifest, source: true, complete: true, resume: false);
                    CheckAssetDirectory(destinationAssets, manifest, source: false, complete: true, resume: false);
                    foreach (var (path, identity) in frozenAssets)
                        if (ArchivePrivateFile.FrozenAsset(path) != identity) throw new MigrationRejectedException();
                }, ct, stagedDestination: bundle is null ? null : destinationPath,
                visit: bundle is null ? null : bundle.RecordReference);
            if (bundle is not null)
            {
                await destination.CloseAsync();
                receipt = receipt with { RuntimeReceiptSha256 = await bundle.PublishAsync(manifest, destinationAssets, receipt, ct) };
            }
            return receipt;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (previewKey is not null) CryptographicOperations.ZeroMemory(previewKey);
        }
    }

    private static TargetConversionAsset ReadAsset(JsonElement value)
    {
        var format = Value(value, "format") switch
        {
            "plain" => TargetAssetFormat.Plain,
            "payment-evidence" => TargetAssetFormat.PaymentEvidence,
            "customer-history" => TargetAssetFormat.CustomerHistory,
            "preview-capture" => TargetAssetFormat.PreviewCapture,
            "preview-cursor" => TargetAssetFormat.PreviewCursor,
            _ => throw new MigrationRejectedException()
        };
        RequireFields(value, "format", "reference_sha256", "stored_sha256", "plaintext_sha256", "brand_id", "row_id", "key_id", "update_id");
        Guid brand = value.GetProperty("brand_id").ValueKind == JsonValueKind.Null ? Guid.Empty : value.GetProperty("brand_id").GetGuid();
        Guid row = value.GetProperty("row_id").ValueKind == JsonValueKind.Null ? Guid.Empty : value.GetProperty("row_id").GetGuid();
        long update = value.GetProperty("update_id").ValueKind == JsonValueKind.Null ? 0 : value.GetProperty("update_id").GetInt64();
        bool detached = format is TargetAssetFormat.CustomerHistory or TargetAssetFormat.PreviewCapture or TargetAssetFormat.PreviewCursor;
        if ((format is TargetAssetFormat.PreviewCapture or TargetAssetFormat.PreviewCursor) && value.GetProperty("update_id").ValueKind == JsonValueKind.Null)
            throw new MigrationRejectedException();
        if (detached != (value.GetProperty("reference_sha256").ValueKind == JsonValueKind.Null)) throw new MigrationRejectedException();
        string reference = detached ? TargetConversionManifest.DetachedCommitment(format, brand, row, update) : Value(value, "reference_sha256");
        return new(reference, Value(value, "stored_sha256"), Value(value, "plaintext_sha256"), format, brand, row,
            value.GetProperty("key_id").ValueKind == JsonValueKind.Null ? null : Leaf(value, "key_id"),
            update);
    }

    private static async Task ValidateAssetScopesAsync(SqliteConnection source, IEnumerable<BrandContext> contexts, TargetConversionManifest manifest, CancellationToken ct)
    {
        await using var transaction = source.BeginTransaction(deferred: true);
        await using var data = new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite(source).Options);
        await data.Database.UseTransactionAsync(transaction, ct);
        var configured = contexts.ToDictionary(context => context.BrandId, context => context.BotId);
        var stored = await data.Set<Brand>().Select(brand => new { brand.Id, brand.BotId }).Take(33).ToListAsync(ct);
        if (configured.Count != stored.Count || stored.Any(brand => !configured.TryGetValue(brand.Id, out long id) || id != brand.BotId))
            throw new MigrationRejectedException();
        foreach (var asset in manifest.OrderedAssets())
        {
            if (asset.Format == TargetAssetFormat.CustomerHistory &&
                !await data.Set<User>().AnyAsync(user => user.Id == asset.Row && user.BrandId == asset.Brand, ct)) throw new MigrationRejectedException();
            if (asset.Format == TargetAssetFormat.PaymentEvidence &&
                !await (from payment in data.Set<Payment>()
                        from user in data.Set<User>()
                        where payment.Id == asset.Row && user.BrandId == asset.Brand &&
                            (user.Id == payment.TopupUserId || data.Set<Order>().Any(order => order.Id == payment.OrderId && order.UserId == user.Id))
                        select user.Id).AnyAsync(ct)) throw new MigrationRejectedException();
        }
    }

    private static string AssetFileName(TargetConversionAsset asset, bool source) => asset.Format switch
    {
        TargetAssetFormat.CustomerHistory => $"{asset.Brand:N}-{asset.Row:N}" + (source ? ".history" : ".history-v2"),
        TargetAssetFormat.PreviewCapture => asset.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture) + (source ? ".capture" : ".capture-v2"),
        TargetAssetFormat.PreviewCursor => "next-offset",
        _ => asset.ReferenceCommitment + ".asset"
    };

    private static string AssetPath(string directory, TargetConversionAsset asset, bool source) => Path.Combine(directory, AssetFileName(asset, source));

    private static byte[]? ReadOutput(string path) => EntryExists(path) ? ArchivePrivateFile.Read(path, minimum: 0) : null;

    private static bool EntryExists(string path) => File.Exists(path) || new FileInfo(path).LinkTarget is not null || Directory.Exists(path);

    private static void CheckAssetDirectory(string directory, TargetConversionManifest manifest, bool source, bool complete, bool resume)
    {
        var expected = manifest.OrderedAssets().Select(asset => AssetFileName(asset, source)).ToHashSet(StringComparer.Ordinal);
        var pending = resume ? expected.Select(name => ".pending-" + name).ToHashSet(StringComparer.Ordinal) : null;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(path);
            if (expected.Remove(name)) continue;
            if (pending is null || !pending.Remove(name)) throw new MigrationRejectedException();
            ArchivePrivateFile.RequireFile(path);
        }
        if (complete && expected.Count != 0) throw new MigrationRejectedException();
    }

    private static FileStream TakeLock(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (EntryExists(path)) ArchivePrivateFile.RequireFile(path);
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }

    private static async Task WriteOutputAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string directory = Path.GetDirectoryName(path)!;
        string pending = Path.Combine(directory, ".pending-" + Path.GetFileName(path));
        if (EntryExists(pending))
        {
            ArchivePrivateFile.RequireFile(pending);
            File.Delete(pending);
        }
        try
        {
            await using (var stream = new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                await stream.WriteAsync(content, ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            ArchivePrivateFile.Publish(pending, path);
        }
        finally { File.Delete(pending); }
    }

    private static string Value(JsonElement value, string name) =>
        value.GetProperty(name).GetString() ?? throw new MigrationRejectedException();

    private static string Leaf(JsonElement value, string name)
    {
        string leaf = Value(value, name);
        if (leaf.Length is < 1 or > 128 || leaf is "." or ".." || leaf.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new MigrationRejectedException();
        return leaf;
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != fields.Length ||
            !element.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new MigrationRejectedException();
    }
}
