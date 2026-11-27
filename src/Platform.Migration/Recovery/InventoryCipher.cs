namespace Platform.Security;

/// <summary>Shared unsold inventory only; snapshot needs the independent inventory grant.</summary>
public sealed class InventoryCipher(SecretSnapshot secrets)
{
    public byte[] Encrypt(Guid itemId, ReadOnlySpan<byte> plaintext) =>
        CipherEnvelope.Encrypt(secrets, null, "inventory_items", itemId, "credential_ciphertext", plaintext);

    public byte[] Decrypt(Guid itemId, string keyId, ReadOnlySpan<byte> ciphertext) =>
        CipherEnvelope.Decrypt(secrets, null, "inventory_items", itemId, "credential_ciphertext", keyId, ciphertext);
}
