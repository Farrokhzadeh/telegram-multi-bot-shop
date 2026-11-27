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

public sealed record VerifiedBotProfile(long Id, string Username);
