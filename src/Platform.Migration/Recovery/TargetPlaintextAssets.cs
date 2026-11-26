using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Security;

namespace Platform.Migration.Recovery;

internal static class TargetPlaintextAssets
{
    internal static byte[] PreviewCursor(TargetConversionAsset asset, ReadOnlySpan<byte> stored)
    {
        if (stored.Length is < 1 or > 24) throw new MigrationRejectedException();
        string expected = asset.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Encoding.ASCII.GetString(stored).Trim() != expected) throw new MigrationRejectedException();
        return Plain(asset, stored);
    }

    internal static byte[] Plain(TargetConversionAsset asset, ReadOnlySpan<byte> stored)
    {
        RequireDigest(stored, asset.StoredSha256);
        if (asset.StoredSha256 != asset.PlaintextSha256) throw new MigrationRejectedException();
        return stored.ToArray();
    }

    internal static byte[] CustomerHistory(TargetConversionAsset asset, SecretSnapshot keys, BrandContext brand, Guid member, ReadOnlySpan<byte> stored)
    {
        RequireDigest(stored, asset.StoredSha256);
        byte[] plain;
        try { plain = new CustomerHistoryArchive(keys).OpenContent(brand, member, stored); }
        catch (UnauthorizedAccessException) { throw new MigrationRejectedException(); }
        try
        {
            RequireDigest(plain, asset.PlaintextSha256);
            CustomerHistoryContent.Decode(member, plain);
            return plain;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    internal static byte[] PaymentEvidence(TargetConversionAsset asset, SecretSnapshot keys, BrandContext brand, Guid payment, string keyId, ReadOnlySpan<byte> stored)
    {
        RequireDigest(stored, asset.StoredSha256);
        byte[] plain = BrandValue(keys, brand, "payments", payment, "ipn_evidence", keyId, stored);
        try { RequireDigest(plain, asset.PlaintextSha256); return plain; }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }

    internal static byte[] PreviewCapture(TargetConversionAsset asset, ReadOnlySpan<byte> key, long updateId, ReadOnlySpan<byte> stored)
    {
        if (key.Length != 32 || updateId is <= 0 or >= long.MaxValue || stored.Length is < 30 or > 16777216)
            throw new MigrationRejectedException();
        RequireDigest(stored, asset.StoredSha256);
        byte[] plain = new byte[stored.Length - 28];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(stored[..12], stored[28..], stored.Slice(12, 16), plain);
            RequireDigest(plain, asset.PlaintextSha256);
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count(field => field.NameEquals("update_id")) != 1 ||
                !root.GetProperty("update_id").TryGetInt64(out long actual) || actual != updateId)
                throw new MigrationRejectedException();
            return plain;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidOperationException or MigrationRejectedException)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new MigrationRejectedException();
        }
    }

    private static byte[] BrandValue(SecretSnapshot keys, BrandContext brand, string table, Guid row, string field, string keyId, ReadOnlySpan<byte> stored)
    {
        try { return new BrandCipher(keys).Decrypt(brand, table, row, field, keyId, stored); }
        catch (CipherRejectedException) { throw new MigrationRejectedException(); }
    }

    private static void RequireDigest(ReadOnlySpan<byte> value, string expected)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(value)) != expected) throw new MigrationRejectedException();
    }
}
