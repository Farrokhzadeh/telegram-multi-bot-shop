
namespace Platform.Security;

public sealed class CustomerHistoryFiles
{
    private readonly string directory;
    private readonly SecretSnapshot secrets;
    public CustomerHistoryFiles(string directory, SecretSnapshot secrets)
    {
        this.directory = directory; this.secrets = secrets;
        RequireDirectory();
    }
    private void RequireDirectory()
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
            !Path.GetFileName(directory).StartsWith("teleg-customer-history-", StringComparison.Ordinal) ||
            new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Historical order storage rejected.");
    }
    private string Name(Guid member)
    {
        RequireDirectory();
        if (member == Guid.Empty) throw new UnauthorizedAccessException("Historical order storage rejected.");
        return Path.Combine(directory, member.ToString("N") + ".history");
    }
    public async Task PublishAsync(BrandContext brand, Guid member, ReadOnlyMemory<byte> sealedBytes, CancellationToken ct = default)
    {
        string path = Name(member);
        var archive = new CustomerHistoryArchive(secrets);
        var wanted = archive.Open(brand, member, sealedBytes.Span);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(brand, member, ct);
            if (!archive.Open(brand, member, existing.Span).SequenceEqual(wanted)) throw new UnauthorizedAccessException("Historical order snapshot changed.");
            return;
        }
        string temp = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { await stream.WriteAsync(sealedBytes, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            File.Move(temp, path, false);
        }
        catch (IOException) { throw new UnauthorizedAccessException("Historical order snapshot publication rejected."); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<ReadOnlyMemory<byte>> ReadAsync(BrandContext brand, Guid member, CancellationToken ct = default)
    {
        string path = Name(member);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 40 or > 1048769 || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Historical order storage rejected.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        _ = new CustomerHistoryArchive(secrets).Open(brand, member, bytes);
        return bytes;
    }
}
