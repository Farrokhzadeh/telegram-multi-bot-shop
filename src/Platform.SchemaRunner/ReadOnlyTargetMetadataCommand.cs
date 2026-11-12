using System.Text;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class ReadOnlyTargetMetadataCommand
{
    internal static async Task RunAsync(string connectionFile, string hashFile, string output, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_RECONCILIATION") != "1" || !Path.IsPathFullyQualified(output) || File.Exists(output)) throw new MigrationRejectedException();
        string? directory = Path.GetDirectoryName(output);
        if (directory is null || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        string metadata = ReadOnlyTargetMetadata.Parse(ArchivePrivateFile.ReadText(connectionFile), ArchivePrivateFile.ReadText(hashFile).Trim());
        await WalletCopyQualification.WriteAsync(output, Encoding.UTF8.GetBytes(metadata), ct);
    }
}
