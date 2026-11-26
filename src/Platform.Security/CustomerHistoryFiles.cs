using System.Security.Cryptography;

namespace Platform.Security;

public sealed class CustomerHistoryFiles
{
    private readonly string directory;
    public CustomerHistoryFiles(string directory)
    {
        this.directory = directory;
        if (!Path.GetFileName(directory).EndsWith("-v2", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Historical order storage rejected.");
        RequireDirectory();
    }
    private void RequireDirectory()
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
            !Path.GetFileName(directory).StartsWith("teleg-customer-history-", StringComparison.Ordinal) ||
            new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Historical order storage rejected.");
    }
    private string Name(BrandContext brand, Guid member)
    {
        RequireDirectory();
        if (member == Guid.Empty) throw new UnauthorizedAccessException("Historical order storage rejected.");
        return Path.Combine(directory, $"{brand.BrandId:N}-{member:N}.history-v2");
    }
    public async Task PublishAsync(BrandContext brand, Guid member, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        string path = Name(brand, member);
        _ = Open(member, content);
        if (File.Exists(path))
        {
            byte[] existing = ReadBytes(brand, member, ct);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(existing, content.Span)) throw new UnauthorizedAccessException("Historical order snapshot changed.");
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
            return;
        }
        string temp = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException("Historical order storage rejected.");
        try
        {
            await using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                await stream.WriteAsync(content, ct);
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead);
                stream.Flush(true);
            }
            File.Move(temp, path, false);
        }
        catch (IOException) { throw new UnauthorizedAccessException("Historical order snapshot publication rejected."); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task<ReadOnlyMemory<byte>> ReadAsync(BrandContext brand, Guid member, CancellationToken ct = default)
    {
        byte[] bytes = ReadBytes(brand, member, ct);
        try { _ = CustomerHistoryContent.Decode(member, bytes); return Task.FromResult<ReadOnlyMemory<byte>>(bytes); }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    public Task<IReadOnlyList<CustomerHistoricalOrder>> ReadOrdersAsync(BrandContext brand, Guid member, CancellationToken ct = default)
    {
        byte[] bytes = ReadBytes(brand, member, ct);
        try { return Task.FromResult(CustomerHistoryContent.Decode(member, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public Task<(string Sha256, int Orders)> ReadCommitmentAsync(BrandContext brand, Guid member, CancellationToken ct = default)
    {
        byte[] bytes = ReadBytes(brand, member, ct);
        try { return Task.FromResult((Convert.ToHexStringLower(SHA256.HashData(bytes)), CustomerHistoryContent.Decode(member, bytes).Count)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private byte[] ReadBytes(BrandContext brand, Guid member, CancellationToken ct)
    {
        string path = Name(brand, member);
        var info = new FileInfo(path);
        if (!OperatingSystem.IsLinux() || !info.Exists || info.LinkTarget is not null ||
            info.Length is < 1 or > 1048576 || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException("Historical order storage rejected.");
        ct.ThrowIfCancellationRequested();
        try
        {
            using var files = new LinuxSecretDirectory(directory);
            return files.Read(Path.GetFileName(path), 1048576);
        }
        catch (SecretConfigurationException) { throw new UnauthorizedAccessException("Historical order storage rejected."); }
    }
    private static IReadOnlyList<CustomerHistoricalOrder> Open(Guid member, ReadOnlyMemory<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        try { return CustomerHistoryContent.Decode(member, copy); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
}
