using System.Security.Cryptography;
using Platform.Core;

namespace Platform.Persistence;

public sealed record PaymentEvidenceContent(Guid PaymentId, Guid BrandId, string Digest, ReadOnlyMemory<byte> Content)
{
    public override string ToString() => "Payment evidence [redacted]";
}

internal static class PaymentEvidenceArchive
{
    internal static async Task<string> SaveAsync(
        Func<Evidence, CancellationToken, Task<string>> archive, Evidence proof,
        CancellationToken ct) => RequireAsset(await archive(proof, ct));

    internal static async Task<string> SavePlaintextAsync(
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, Guid payment, Guid brand,
        byte[] digest, byte[] content, CancellationToken ct) =>
        RequireAsset(await ArchivePlaintextAsync(archive, payment, brand, digest, content, ct));

    private static Task<string> ArchivePlaintextAsync(
        Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive, Guid payment, Guid brand,
        byte[] digest, byte[] content, CancellationToken ct)
    {
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), digest))
            throw new PaymentNoticeRejectedException();
        return archive(new(payment, brand, Convert.ToHexStringLower(digest), content.ToArray()), ct);
    }

    private static string RequireAsset(string asset)
    {
        if (asset is null || !asset.StartsWith("protected:", StringComparison.Ordinal) || asset.Length is < 12 or > 1024 ||
            asset.Any(char.IsControl)) throw new PaymentNoticeRejectedException();
        return asset;
    }

    internal static Func<Evidence, CancellationToken, Task<string>> Plain(Func<PaymentEvidenceContent, CancellationToken, Task<string>> archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return (value, token) => ArchivePlaintextAsync(archive, value.Payment, value.Brand,
            Convert.FromHexString(value.Digest), value.Content, token);
    }

    internal sealed record Evidence(Guid Payment, Guid Brand, string Digest, byte[] Content)
    {
        public override string ToString() => "Payment evidence [redacted]";
    }

    internal sealed record Pending(byte[]? Digest, byte[]? Content)
    {
        public override string ToString() => "Pending payment evidence [redacted]";
    }
}
