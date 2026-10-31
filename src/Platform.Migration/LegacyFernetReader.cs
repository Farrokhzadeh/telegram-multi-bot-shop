using System.Security.Cryptography;
using System.Text;

namespace Platform.Migration;

public enum LegacyFieldKind
{
    Secret,
    Json,
    Text
}

public static class LegacyFernetReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Migration/recovery only; never a reader for new target ciphertext.
    public static string Decrypt(string value, LegacyFieldKind kind,
        IEnumerable<string> rawKeys, bool allowSourcePlaintext = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(rawKeys);
        if (kind is not (LegacyFieldKind.Secret or LegacyFieldKind.Json or LegacyFieldKind.Text))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (value.Length == 0)
        {
            return "";
        }

        string? prefix = kind switch
        {
            LegacyFieldKind.Secret when value.StartsWith("enc:v1:", StringComparison.Ordinal) => "enc:v1:",
            LegacyFieldKind.Secret when value.StartsWith("enc:", StringComparison.Ordinal) => "enc:",
            LegacyFieldKind.Json when value.StartsWith("encjson:v1:", StringComparison.Ordinal) => "encjson:v1:",
            LegacyFieldKind.Text when value.StartsWith("enctext:v1:", StringComparison.Ordinal) => "enctext:v1:",
            _ => null
        };
        if (prefix is null)
        {
            bool encryptedMarker = value.StartsWith("enc:", StringComparison.Ordinal) ||
                value.StartsWith("encjson:", StringComparison.Ordinal) ||
                value.StartsWith("enctext:", StringComparison.Ordinal);
            if (allowSourcePlaintext && !encryptedMarker)
            {
                return value;
            }

            throw new CryptographicException("Unsupported legacy representation.");
        }

        byte[] token;
        try
        {
            token = Decode(value[prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Invalid legacy ciphertext.");
        }

        if (token.Length < 73 || token[0] != 0x80 || (token.Length - 57) % 16 != 0)
        {
            throw new CryptographicException("Invalid legacy ciphertext.");
        }

        foreach (string rawKey in rawKeys.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(rawKey))
            {
                continue;
            }

            byte[] key = DeriveKey(rawKey);
            byte[] signature = HMACSHA256.HashData(key.AsSpan(0, 16), token.AsSpan(0, token.Length - 32));
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(signature, token.AsSpan(token.Length - 32)))
                {
                    continue;
                }

                using var aes = Aes.Create();
                byte[] encryptionKey = key.AsSpan(16, 16).ToArray();
                try
                {
                    aes.Key = encryptionKey;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptionKey);
                }
                byte[] plaintext = aes.DecryptCbc(token.AsSpan(25, token.Length - 57),
                    token.AsSpan(9, 16), PaddingMode.PKCS7);
                try
                {
                    return StrictUtf8.GetString(plaintext);
                }
                catch (DecoderFallbackException)
                {
                    throw new CryptographicException("Invalid legacy plaintext encoding.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(signature);
            }
        }

        throw new CryptographicException("No provided legacy key authenticated the ciphertext.");
    }

    private static byte[] DeriveKey(string raw)
    {
        string candidate = raw.StartsWith("fernet:", StringComparison.Ordinal) ? raw[7..] : raw;
        if (candidate.Length == 44)
        {
            try
            {
                byte[] decoded = Decode(candidate);
                if (decoded.Length == 32)
                {
                    return decoded;
                }

                CryptographicOperations.ZeroMemory(decoded);
            }
            catch (FormatException)
            {
                // The pinned Python reader hashes invalid/non-Fernet raw key text.
            }
        }

        byte[] rawBytes = StrictUtf8.GetBytes(candidate);
        try
        {
            return SHA256.HashData(rawBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
        }
    }

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
}
