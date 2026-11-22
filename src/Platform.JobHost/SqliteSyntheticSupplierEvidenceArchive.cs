using Platform.Core;
using Platform.Fulfillment;

namespace Platform.JobHost;

internal sealed class SqliteSyntheticSupplierEvidenceArchive : ISupplierEvidenceArchive
{
    private const string Root = "/stage/sqlite/supplier-evidence";

    public SqliteSyntheticSupplierEvidenceArchive()
    {
        if (!OperatingSystem.IsLinux())
            throw new UnauthorizedAccessException("Synthetic evidence is supported only on Linux staging.");
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        var directory = new DirectoryInfo(Root);
        if (directory.LinkTarget is not null ||
            (File.GetUnixFileMode(Root) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Synthetic supplier archive is not private.");
    }

    public async Task<string> StoreAsync(SupplierBinding binding, Guid attempt,
        ReadOnlyMemory<byte> evidence, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || binding.Provider != "synthetic" ||
            binding.Account != "public-test-account" || attempt == Guid.Empty ||
            evidence.Length is < 1 or > 65536)
            throw new UnauthorizedAccessException("Only synthetic supplier evidence may be archived.");
        string filename = attempt.ToString("N");
        string target = Path.Combine(Root, filename);
        string staging = Path.Combine(Root, "." + filename + "." + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            await using (var stream = new FileStream(staging, options))
            {
                await stream.WriteAsync(evidence, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(staging, target);
            return "protected:synthetic-stage/supplier-evidence/" + filename;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
