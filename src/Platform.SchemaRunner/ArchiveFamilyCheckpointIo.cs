using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static partial class ReadOnlySourceArchiveVerification
{
    private static Task<byte[]> ReadPrivateAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(ArchivePrivateFile.Read(path));
    }

    internal static byte[] CommonBinding(string sourcePlan, byte[] archivePlan, byte[] index, byte[] tool, LegacySourceEndpoint endpoint, string brand, Guid brandId, string keyId, IReadOnlyList<string> legacyKeys)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(sourcePlan); using var stream = new MemoryStream();
        try
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write("teleg-archive-family-input-v1"); writer.Write(SHA256.HashData(sourceBytes)); writer.Write(SHA256.HashData(archivePlan)); writer.Write(SHA256.HashData(index)); writer.Write(tool); writer.Write(endpoint.Host); writer.Write(endpoint.Port); writer.Write(endpoint.Database); writer.Write(endpoint.Username); writer.Write(brand); writer.Write(brandId.ToByteArray()); writer.Write(keyId); writer.Write(legacyKeys.Count); foreach (string key in legacyKeys) writer.Write(key); writer.Flush(); }
            byte[] body = stream.ToArray(); try { return SHA256.HashData(body); } finally { CryptographicOperations.ZeroMemory(body); }
        }
        finally { CryptographicOperations.ZeroMemory(sourceBytes); if (stream.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan(0, (int)stream.Length)); }
    }
    internal static Task<byte[]> FamilyHashAsync(string directory, IReadOnlyList<LegacyMapProof> proofs, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var proof in proofs.OrderBy(p => p.LegacyId, StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, proof.Target.ToString("N") + ".archive.json");
            ct.ThrowIfCancellationRequested(); byte[] bytes = ArchivePrivateFile.Read(path, 128), digest;
            try { digest = SHA256.HashData(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
            byte[] identity = Encoding.UTF8.GetBytes(proof.LegacyId);
            try { hash.AppendData(BitConverter.GetBytes((int)proof.Projection)); hash.AppendData(BitConverter.GetBytes(identity.Length)); hash.AppendData(identity); hash.AppendData(proof.Target.ToByteArray()); hash.AppendData(proof.Fingerprint.Span); hash.AppendData(digest); }
            finally { CryptographicOperations.ZeroMemory(identity); }
        }
        return Task.FromResult(hash.GetHashAndReset());
    }
    private static async Task PublishCheckpointAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        string pending = Path.Combine(Path.GetDirectoryName(path)!, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            try { File.Move(pending, path, false); }
            catch (IOException) when (File.Exists(path)) { byte[] existing = await ReadPrivateAsync(path, ct); if (!CryptographicOperations.FixedTimeEquals(bytes, existing)) throw new MigrationRejectedException(); }
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
