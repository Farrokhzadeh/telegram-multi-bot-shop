using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Security;

public sealed class BotToken : IDisposable
{
    private readonly byte[] bytes;
    private bool disposed;
    public long BotId { get; }
    private BotToken(byte[] bytes, long botId) { this.bytes = bytes; BotId = botId; }
    public static BotToken Parse(ReadOnlySpan<char> value)
    {
        int colon = value.IndexOf(':');
        if (colon is < 1 or > 19 || value.Length - colon - 1 is < 30 or > 128 || value[0] == '0' ||
            !long.TryParse(value[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0) throw new ArgumentException("Invalid bot credential.");
        foreach (char c in value[(colon + 1)..]) if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) throw new ArgumentException("Invalid bot credential.");
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value)];
        Encoding.UTF8.GetBytes(value, bytes);
        return new(bytes, id);
    }
    public ReadOnlyMemory<byte> ForTrustedTransport()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return bytes;
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(bytes); disposed = true; }
    public override string ToString() => "BotToken [redacted]";
}

public sealed record ProtectedBotToken(byte[] Ciphertext, string KeyId)
{
    public override string ToString() => "ProtectedBotToken [redacted]";
}
public sealed record VerifiedBotProfile(long Id, string Username);
public sealed class BotTokenVault(SecretSnapshot secrets)
{
    public ProtectedBotToken Protect(BrandContext context, BotToken token)
    {
        if (token.BotId != context.BotId) throw new UnauthorizedAccessException("Bot identity mismatch.");
        return new(new BrandCipher(secrets).Encrypt(context, "brands", context.BrandId, "bot_token_ciphertext", token.ForTrustedTransport().Span), secrets.CurrentBrandKeyId(context));
    }
    public BotToken OpenForTrustedTransport(BrandContext context, string keyId, ReadOnlySpan<byte> ciphertext)
    {
        byte[] plain = new BrandCipher(secrets).Decrypt(context, "brands", context.BrandId, "bot_token_ciphertext", keyId, ciphertext);
        try
        {
            char[] chars = Encoding.UTF8.GetChars(plain);
            BotToken token;
            try { token = BotToken.Parse(chars); }
            finally { Array.Clear(chars); }
            if (token.BotId == context.BotId) return token;
            token.Dispose();
            throw new UnauthorizedAccessException("Bot identity mismatch.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
