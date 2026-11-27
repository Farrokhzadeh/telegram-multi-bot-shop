using System.Security.Cryptography;
using System.Text;

namespace Platform.Security;

public sealed class CipherRejectedException : Exception
{
    internal CipherRejectedException() : base("Protected value rejected.") { }
}

/// <summary>SC02 binary envelope: magic, ASCII key-id length/id, 12-byte nonce, ciphertext, 16-byte tag.</summary>
internal static class CipherEnvelope
{
    private const int MaximumPlaintext = 1048576;

    public static byte[] Encrypt(SecretSnapshot secrets, BrandContext? context, string table, Guid row, string field, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaximumPlaintext) throw new CipherRejectedException();
        string id = context is null ? secrets.CurrentInventoryKeyId : secrets.CurrentBrandKeyId(context);
        byte[] aad = AssociatedData(context, table, row, field, id);
        byte[] output = new byte[5 + id.Length + 12 + plaintext.Length + 16];
        "SC02"u8.CopyTo(output);
        output[4] = checked((byte)id.Length);
        Encoding.ASCII.GetBytes(id, output.AsSpan(5, id.Length));
        int offset = 5 + id.Length;
        RandomNumberGenerator.Fill(output.AsSpan(offset, 12));
        byte[] copy = plaintext.ToArray();
        try
        {
            int EncryptKey(ReadOnlySpan<byte> key)
            {
                using var cipher = new AesGcm(key, 16);
                cipher.Encrypt(output.AsSpan(offset, 12), copy, output.AsSpan(offset + 12, copy.Length), output.AsSpan(output.Length - 16), aad);
                return 0;
            }
            if (context is null) secrets.UseInventoryKey(id, EncryptKey);
            else secrets.UseBrandKey(context, id, EncryptKey);
            return output;
        }
        catch (Exception error) when (error is CryptographicException or SecretConfigurationException)
        {
            throw new CipherRejectedException();
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    public static byte[] Decrypt(SecretSnapshot secrets, BrandContext? context, string table, Guid row, string field,
        string keyId, ReadOnlySpan<byte> ciphertext)
    {
        if (!SecretFiles.IsFileName(keyId) || ciphertext.Length < 33 || ciphertext.Length > MaximumPlaintext + 193 ||
            !ciphertext[..4].SequenceEqual("SC02"u8) || ciphertext[4] != keyId.Length ||
            ciphertext.Length < 5 + keyId.Length + 28 ||
            !ciphertext.Slice(5, keyId.Length).SequenceEqual(Encoding.ASCII.GetBytes(keyId)))
            throw new CipherRejectedException();
        byte[] aad = AssociatedData(context, table, row, field, keyId);
        byte[] copy = ciphertext.ToArray();
        int offset = 5 + keyId.Length;
        if (copy.Length - offset - 28 > MaximumPlaintext) throw new CipherRejectedException();
        byte[] plaintext = new byte[copy.Length - offset - 28];
        bool success = false;
        try
        {
            int DecryptKey(ReadOnlySpan<byte> key)
            {
                using var cipher = new AesGcm(key, 16);
                cipher.Decrypt(copy.AsSpan(offset, 12), copy.AsSpan(offset + 12, plaintext.Length), copy.AsSpan(copy.Length - 16), plaintext, aad);
                return 0;
            }
            if (context is null) secrets.UseInventoryKey(keyId, DecryptKey);
            else secrets.UseBrandKey(context, keyId, DecryptKey);
            success = true;
            return plaintext;
        }
        catch (Exception error) when (error is CryptographicException or SecretConfigurationException)
        {
            throw new CipherRejectedException();
        }
        finally { if (!success) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] AssociatedData(BrandContext? context, string table, Guid row, string field, string keyId)
    {
        if (row == Guid.Empty || !Identifier(table) || !Identifier(field) || !SecretFiles.IsFileName(keyId))
            throw new CipherRejectedException();
        using var bytes = new MemoryStream();
        bytes.Write("SC02"u8);
        bytes.WriteByte(context is null ? (byte)2 : (byte)1);
        bytes.Write((context?.BrandId ?? Guid.Empty).ToByteArray(bigEndian: true));
        Write(table);
        bytes.Write(row.ToByteArray(bigEndian: true));
        Write(field);
        Write(keyId);
        return bytes.ToArray();

        void Write(string value)
        {
            bytes.WriteByte(checked((byte)value.Length));
            bytes.Write(Encoding.ASCII.GetBytes(value));
        }
    }

    private static bool Identifier(string value) => !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
