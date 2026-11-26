using Platform.Core;
using Platform.Fulfillment;
using System.Security.Cryptography;

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
        RequirePrivateDirectory();
    }

    private static void RequirePrivateDirectory()
    {
        var directory = new DirectoryInfo(Root);
        if (!OperatingSystem.IsLinux() || !directory.Exists || directory.LinkTarget is not null ||
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
        RequirePrivateDirectory();
        if (await ExistingAsync(target, evidence, ct)) return Reference(filename);
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
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            RequirePrivateDirectory();
            try { File.Move(staging, target, overwrite: false); }
            catch (IOException)
            {
                if (!await ExistingAsync(target, evidence, ct)) throw;
            }
            return Reference(filename);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static async Task<bool> ExistingAsync(string path, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || Directory.Exists(path)) throw new UnauthorizedAccessException("Synthetic supplier evidence path is unavailable.");
        if (!file.Exists) return false;
        if (file.Length != expected.Length || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Synthetic supplier evidence differs from the accepted attempt.");
        byte[] bytes = new byte[expected.Length];
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await input.ReadExactlyAsync(bytes, ct);
            if (input.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(bytes, expected.Span))
                throw new UnauthorizedAccessException("Synthetic supplier evidence differs from the accepted attempt.");
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Reference(string filename) => "protected:synthetic-stage/supplier-evidence/" + filename;
}
