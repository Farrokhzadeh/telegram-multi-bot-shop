using System.Security.Cryptography;
using System.Text;
using Platform.Persistence.Migrations;

namespace Platform.SchemaRunner;

public static partial class EfSchemaCommand
{
    internal static async Task<string> WritePlainPostgresSchemaAsync(string path, CancellationToken ct = default)
    {
        RequireQualification();
        RequirePlainQualificationPath(path);
        if (!path.EndsWith(".sql", StringComparison.Ordinal)) throw new InvalidOperationException("A private SQL output is required.");
        string directory = Path.GetDirectoryName(path)!;
        ArchivePrivateFile.RequireDirectory(directory);
        _ = DescribePlain("postgres");
        using var model = PlainModel("postgres");
        byte[] sql = Encoding.UTF8.GetBytes(PlainTargetSchema.Script(model));
        string pending = Path.Combine(directory, ".schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WalletCopyQualification.WriteAsync(pending, sql, ct);
            ct.ThrowIfCancellationRequested();
            ArchivePrivateFile.Publish(pending, path);
            return Convert.ToHexStringLower(SHA256.HashData(sql));
        }
        finally { File.Delete(pending); }
    }
}
