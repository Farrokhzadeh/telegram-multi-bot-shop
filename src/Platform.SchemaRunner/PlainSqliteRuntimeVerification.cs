using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;
using Platform.Persistence.Migrations;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<int> VerifyPlainRuntimeCopyAsync(string path, string expectedReceipt, CancellationToken ct)
    {
        RequireQualification();
        RequirePlainQualificationPath(path);
        if (!Path.GetFileName(path).StartsWith("runtime-", StringComparison.Ordinal)) throw new MigrationRejectedException();
        ArchivePrivateFile.RequireDirectory(path);
        string receiptPath = Path.Combine(path, "receipt.json");
        byte[] bytes = ArchivePrivateFile.Read(receiptPath);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), ParseDigest(expectedReceipt)))
            throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        RequireFields(root, "format", "snapshot_sha256", "staged_schema_sha256", "schema_sha256", "database_sha256", "model_sha256", "asset_manifest_sha256", "rows", "assets");
        if (root.GetProperty("format").GetInt32() != PlainTargetSchema.SqliteVersion || root.GetProperty("rows").GetInt64() < 0)
            throw new MigrationRejectedException();
        var described = DescribePlain("sqlite");
        if (Value(root, "model_sha256") != described.Model ||
            !CryptographicOperations.FixedTimeEquals(await PlainTargetSchema.ExpectedSqliteSchemaAsync(ct, staged: false), ParseDigest(Value(root, "schema_sha256"))) ||
            !CryptographicOperations.FixedTimeEquals(await PlainTargetSchema.ExpectedSqliteSchemaAsync(ct, staged: true), ParseDigest(Value(root, "staged_schema_sha256"))))
            throw new MigrationRejectedException();
        _ = ParseDigest(Value(root, "snapshot_sha256"));
        _ = ParseDigest(Value(root, "asset_manifest_sha256"));

        var references = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("assets").EnumerateArray())
        {
            RequireFields(entry, "reference_sha256", "file", "sha256");
            string reference = Value(entry, "reference_sha256"), file = Value(entry, "file"), digest = Value(entry, "sha256");
            _ = ParseDigest(reference);
            _ = ParseDigest(digest);
            if (references.Count >= 100000 || !references.Add(reference) || !IsRuntimeAssetPath(file) ||
                !files.TryAdd(file, digest) && files[file] != digest) throw new MigrationRejectedException();
        }

        string databasePath = Path.Combine(path, "shop.db");
        var directories = files.Keys.Select(file => file[..file.IndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        var rootEntries = directories.Append("shop.db").Append("receipt.json").ToHashSet(StringComparer.Ordinal);
        if (!rootEntries.SetEquals(Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry))))
            throw new MigrationRejectedException();
        foreach (string directory in directories)
        {
            string full = Path.Combine(path, directory);
            ArchivePrivateFile.RequireDirectory(full);
            if (!files.Keys.Where(file => file.StartsWith(directory + "/", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal).SetEquals(Directory.EnumerateFileSystemEntries(full)
                    .Select(entry => directory + "/" + Path.GetFileName(entry))))
                throw new MigrationRejectedException();
        }
        foreach (var (file, digest) in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] asset = ArchivePrivateFile.Read(Path.Combine(path, file), minimum: 0);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(asset), ParseDigest(digest))) throw new MigrationRejectedException();
            }
            finally { CryptographicOperations.ZeroMemory(asset); }
        }
        if (!CryptographicOperations.FixedTimeEquals(await ArchivePrivateFile.HashRuntimeDatabaseAsync(databasePath, PlainTargetSchema.SqliteVersion, ct), ParseDigest(Value(root, "database_sha256"))))
            throw new MigrationRejectedException();
        if (!rootEntries.SetEquals(Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry))))
            throw new MigrationRejectedException();
        return files.Count;
    }

    private static bool IsRuntimeAssetPath(string path)
    {
        int slash = path.IndexOf('/');
        if (slash <= 0) return false;
        string folder = path[..slash], file = path[(slash + 1)..];
        return (folder is "assets" or "supplier-evidence" or "state-v2" or "teleg-customer-history-reviewed-v2") &&
            file.Length is > 0 and <= 128 && file is not ("." or "..") &&
            file.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static byte[] ParseDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new MigrationRejectedException();
        return Convert.FromHexString(digest);
    }
}
