using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Testing;

public static class CipherQualification
{
    public static void Run()
    {
        using var files = new SyntheticSecretFiles();
        // Public all-zero AES key and nonce are used only to decrypt an independent Python AESGCM vector.
        files.Write("a-old", new byte[32]);
        using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var cipher = new BrandCipher(secrets);
        var inventory = new InventoryCipher(secrets);
        Guid row = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var a = Context(SyntheticSecretFiles.BrandA);
        var b = Context(SyntheticSecretFiles.BrandB);
        byte[] golden = Convert.FromHexString("53433032036f6c64000000000000000000000000bed2225124034b096822a1b6d4d3eb7d11146cb8890e406274e5ed1ac67641c401f96263");
        byte[] decoded = cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "old", golden);
        try { Check(decoded.AsSpan().SequenceEqual("public golden vector"u8)); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        byte[] plain = Encoding.UTF8.GetBytes("synthetic protected content / داده");
        try
        {
            byte[] encrypted = cipher.Encrypt(a, "brands", row, "bot_token_ciphertext", plain);
            Check(!encrypted.AsSpan().SequenceEqual(cipher.Encrypt(a, "brands", row, "bot_token_ciphertext", plain)));
            decoded = cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "current", encrypted);
            try { Check(decoded.AsSpan().SequenceEqual(plain)); }
            finally { CryptographicOperations.ZeroMemory(decoded); }
            Denied(() => cipher.Decrypt(b, "brands", row, "bot_token_ciphertext", "current", encrypted));
            Denied(() => cipher.Decrypt(a, "payments", row, "bot_token_ciphertext", "current", encrypted));
            Denied(() => cipher.Decrypt(a, "brands", Guid.NewGuid(), "bot_token_ciphertext", "current", encrypted));
            Denied(() => cipher.Decrypt(a, "brands", row, "other_field", "current", encrypted));
            Denied(() => cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "old", encrypted));
            Denied(() => cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "missing", encrypted));
            for (int i = 0; i < encrypted.Length; i++)
            {
                var corrupt = (byte[])encrypted.Clone();
                corrupt[i] ^= 1;
                Denied(() => cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "current", corrupt));
            }
            for (int i = 0; i < encrypted.Length; i++)
            {
                var truncated = encrypted[..i];
                Denied(() => cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "current", truncated));
            }
            Denied(() => cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "current", plain));
            byte[] stock = inventory.Encrypt(row, plain);
            decoded = inventory.Decrypt(row, "current", stock);
            try { Check(decoded.AsSpan().SequenceEqual(plain)); }
            finally { CryptographicOperations.ZeroMemory(decoded); }
            Denied(() => cipher.Decrypt(a, "inventory_items", row, "credential_ciphertext", "current", stock));
            Denied(() => inventory.Decrypt(Guid.NewGuid(), "current", stock));
            files.WriteManifest(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]);
            using var bot = SecretFiles.Load(files.Root, "service-config.json", files.Grant(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]));
            Denied(() => new InventoryCipher(bot).Decrypt(row, "current", stock));
            byte[] empty = cipher.Encrypt(a, "brands", row, "bot_token_ciphertext", []);
            Check(cipher.Decrypt(a, "brands", row, "bot_token_ciphertext", "current", empty).Length == 0);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static BrandContext Context(Guid brand)
    {
        var registry = new BotBrandRegistry([new("test-bot", "a", brand, 101, SyntheticSecretFiles.Deployment, 7, true)]);
        if (!registry.TryResolveAuthenticated("test-bot", new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "a")], "test-bot")), out var context)) throw new InvalidOperationException("Synthetic context rejected.");
        return context!;
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Cipher qualification failed.");
    }

    private static void Denied(Func<byte[]> operation)
    {
        try
        {
            var plaintext = operation();
            CryptographicOperations.ZeroMemory(plaintext);
        }
        catch (Exception error) when (error is CipherRejectedException or SecretConfigurationException)
        {
            Check(error.InnerException is null);
            return;
        }
        throw new InvalidOperationException("Unsafe ciphertext accepted.");
    }
}
