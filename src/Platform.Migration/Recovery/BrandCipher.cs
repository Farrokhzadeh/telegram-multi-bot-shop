namespace Platform.Security;

/// <summary>Brand-owned bytes only. Callers own and must clear returned plaintext.</summary>
public sealed class BrandCipher(SecretSnapshot secrets)
{
    public byte[] Encrypt(BrandContext context, string table, Guid row, string field, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CipherEnvelope.Encrypt(secrets, context, table, row, field, plaintext);
    }

    public byte[] Decrypt(BrandContext context, string table, Guid row, string field, string keyId, ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CipherEnvelope.Decrypt(secrets, context, table, row, field, keyId, ciphertext);
    }
}
