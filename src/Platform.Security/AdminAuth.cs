using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;

namespace Platform.Security;

public sealed class AdminAuthenticationException : Exception, ISafeApiError
{
    public ErrorKind Kind => ErrorKind.Unauthenticated;
    public AdminAuthenticationException() : base("Administrator authentication rejected.") { }
}

/// <summary>Explicit transport export only; no public secret property or raw value in ToString.</summary>
public sealed class AdminToken : IDisposable
{
    private readonly byte[] bytes;
    private readonly object gate = new();
    private bool disposed;
    private AdminToken(byte[] bytes) => this.bytes = bytes;
    public static AdminToken Create() => new(RandomNumberGenerator.GetBytes(32));

    public static AdminToken Parse(string value)
    {
        if (value is null || value.Length != 43 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new AdminAuthenticationException();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { throw new AdminAuthenticationException(); }
        var token = new AdminToken(bytes);
        if (bytes.Length != 32 || token.ExportForTransport() != value)
        {
            token.Dispose();
            throw new AdminAuthenticationException();
        }
        return token;
    }

    public string ExportForTransport()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public byte[] Hash()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return SHA256.HashData(bytes);
        }
    }

    public void Dispose()
    {
        lock (gate) { CryptographicOperations.ZeroMemory(bytes); disposed = true; }
    }
    public override string ToString() => "Admin secret (redacted)";
}

public sealed class AuthenticatedAdmin
{
    internal AuthenticatedAdmin(Guid adminId, Guid sessionId, long version, byte[] digest)
    { AdminId = adminId; SessionId = sessionId; AuthVersion = version; TokenHash = digest.ToArray(); }
    public Guid AdminId { get; }
    public Guid SessionId { get; }
    public long AuthVersion { get; }
    internal byte[] TokenHash { get; }
    public override string ToString() => "Authenticated administrator";
}

/// <summary>Server adapter assertion after verified Telegram ingress; never bind this from a customer DTO.</summary>
public sealed class PrivateTelegramProof
{
    private PrivateTelegramProof(long userId, byte[] nonceHash) { UserId = userId; NonceHash = nonceHash; }
    public long UserId { get; }
    internal byte[] NonceHash { get; }
    public static PrivateTelegramProof FromVerifiedIngress(BrandContext bot, long senderId, long privateChatId, bool senderIsBot, AdminToken messageNonce)
    {
        ArgumentNullException.ThrowIfNull(bot);
        if (senderId <= 0 || privateChatId != senderId || senderIsBot) throw new AdminAuthenticationException();
        return new PrivateTelegramProof(senderId, messageNonce.Hash());
    }
}

public static class AdminAuth
{
    private const int Iterations = 600000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string CreatePasswordVerifier(ReadOnlySpan<char> password)
    {
        if (!ValidPassword(password)) throw new ArgumentException("Invalid administrator password policy.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = new byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, salt, hash, Iterations, HashAlgorithmName.SHA256);
            return "pbkdf2-sha256:1:600000:" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
        }
        finally { CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(hash); }
    }

    public static bool VerifyPassword(ReadOnlySpan<char> password, string? verifier)
    {
        byte[] salt = new byte[16], expected = new byte[32], actual = new byte[32];
        bool recognized = false;
        int iterations = Iterations;
        try
        {
            if (verifier is not null && verifier.Length <= 160)
            {
                var parts = verifier.Split(':');
                if (parts.Length == 5 && parts[0] == "pbkdf2-sha256" && parts[1] == "1" &&
                    int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int work) && work is >= Iterations and <= 1200000)
                {
                    try
                    {
                        byte[] parsedSalt = Convert.FromBase64String(parts[3]), parsedHash = Convert.FromBase64String(parts[4]);
                        try
                        {
                            if (parsedSalt.Length == 16 && parsedHash.Length == 32 &&
                                Convert.ToBase64String(parsedSalt) == parts[3] && Convert.ToBase64String(parsedHash) == parts[4])
                            { parsedSalt.CopyTo(salt, 0); parsedHash.CopyTo(expected, 0); iterations = work; recognized = true; }
                        }
                        finally { CryptographicOperations.ZeroMemory(parsedSalt); CryptographicOperations.ZeroMemory(parsedHash); }
                    }
                    catch (FormatException) { }
                }
            }
            bool valid = ValidPassword(password);
            Rfc2898DeriveBytes.Pbkdf2(valid ? password : ReadOnlySpan<char>.Empty, salt, actual, iterations, HashAlgorithmName.SHA256);
            return recognized && valid && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    public static byte[] BindingHash(AdminToken nonce, Guid adminId, long authVersion, long telegramId, bool botConfirmed)
    {
        if (adminId == Guid.Empty || authVersion < 0 || telegramId <= 0) throw new AdminAuthenticationException();
        byte[] hash = nonce.Hash();
        byte[] material = new byte[16 + 16 + 8 + 8 + 1 + 32];
        try
        {
            "admin-binding-v1"u8.CopyTo(material);
            adminId.TryWriteBytes(material.AsSpan(16, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteInt64BigEndian(material.AsSpan(32, 8), authVersion);
            BinaryPrimitives.WriteInt64BigEndian(material.AsSpan(40, 8), telegramId);
            material[48] = botConfirmed ? (byte)2 : (byte)1;
            hash.CopyTo(material, 49);
            return SHA256.HashData(material);
        }
        finally { CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(material); }
    }

    private static bool ValidPassword(ReadOnlySpan<char> password)
    {
        if (password.Length is < 15 or > 256) return false;
        try
        {
            _ = StrictUtf8.GetByteCount(password);
            int characters = 0;
            foreach (var rune in password.EnumerateRunes()) characters++;
            return characters is >= 15 and <= 128;
        }
        catch (EncoderFallbackException) { return false; }
    }
}
