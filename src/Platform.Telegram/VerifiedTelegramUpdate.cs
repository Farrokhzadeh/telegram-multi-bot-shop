using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Security;

namespace Platform.Telegram;

public sealed class TelegramIngressRejectedException : Exception
{
    public TelegramIngressRejectedException() : base("Telegram update was rejected.") { }
}
public sealed class VerifiedTelegramUpdate
{
    internal VerifiedTelegramUpdate(BrandContext context, long updateId, long actorId, long messageId, string value, bool callback)
    { Context = context; UpdateId = updateId; ActorId = actorId; MessageId = messageId; Value = value; IsCallback = callback; }
    public BrandContext Context { get; }
    public long UpdateId { get; }
    public long ActorId { get; }
    public long MessageId { get; }
    public bool IsCallback { get; }
    internal string Value { get; }
    public override string ToString() => "VerifiedTelegramUpdate [redacted]";
}

public sealed class TelegramWebhookVerifier : IDisposable
{
    private readonly BrandContext context;
    private readonly byte[] secretHash;
    private bool disposed;
    public TelegramWebhookVerifier(BrandContext context, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (secret.Length != 32) throw new ArgumentException("Webhook secrets require 32 random bytes.");
        this.context = context;
        byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        try { secretHash = SHA256.HashData(encoded); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public VerifiedTelegramUpdate Verify(IReadOnlyList<string> secretHeaders, ReadOnlyMemory<byte> body)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (secretHeaders.Count != 1 || secretHeaders[0].Length != 43 || body.Length is < 2 or > 1048576) throw new TelegramIngressRejectedException();
        byte[] supplied = Encoding.UTF8.GetBytes(secretHeaders[0]);
        byte[] hash = SHA256.HashData(supplied);
        bool authenticated;
        try { authenticated = CryptographicOperations.FixedTimeEquals(secretHash, hash); }
        finally { CryptographicOperations.ZeroMemory(supplied); CryptographicOperations.ZeroMemory(hash); }
        if (!authenticated) throw new TelegramIngressRejectedException();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Validate(root);
            if (root.ValueKind != JsonValueKind.Object) throw new TelegramIngressRejectedException();
            long updateId = Number(root, "update_id", false);
            bool callback = root.TryGetProperty("callback_query", out JsonElement envelope);
            if (callback == root.TryGetProperty("message", out JsonElement message)) throw new TelegramIngressRejectedException();
            JsonElement sender;
            string value;
            if (callback)
            {
                sender = envelope.GetProperty("from");
                message = envelope.GetProperty("message");
                JsonElement author = message.GetProperty("from");
                if (!author.GetProperty("is_bot").GetBoolean() || Number(author, "id", true) != context.BotId) throw new TelegramIngressRejectedException();
                value = envelope.GetProperty("data").GetString() ?? throw new TelegramIngressRejectedException();
                if (Encoding.UTF8.GetByteCount(value) is < 1 or > 64 || envelope.TryGetProperty("inline_message_id", out _)) throw new TelegramIngressRejectedException();
            }
            else
            {
                sender = message.GetProperty("from");
                value = message.GetProperty("text").GetString() ?? throw new TelegramIngressRejectedException();
                if (value is not ("/start" or "/menu" or "/support" or "/discounts") && !(value.Length == 82 && value.StartsWith("/bind ", StringComparison.Ordinal))) throw new TelegramIngressRejectedException();
            }
            long actorId = Number(sender, "id", true);
            if (sender.GetProperty("is_bot").GetBoolean()) throw new TelegramIngressRejectedException();
            JsonElement chat = message.GetProperty("chat");
            if (chat.GetProperty("type").GetString() != "private" || Number(chat, "id", true) != actorId) throw new TelegramIngressRejectedException();
            return new(context, updateId, actorId, Number(message, "message_id", true), value, callback);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new TelegramIngressRejectedException(); }
    }

    private static long Number(JsonElement value, string name, bool positive)
    {
        if (!value.GetProperty(name).TryGetInt64(out long number) || (positive ? number <= 0 : number < 0)) throw new TelegramIngressRejectedException();
        return number;
    }
    private static void Validate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || BrandSelector.IsReservedName(property.Name)) throw new TelegramIngressRejectedException();
                Validate(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) Validate(child);
    }
    public void Dispose() { if (!disposed) { disposed = true; CryptographicOperations.ZeroMemory(secretHash); } }
    public override string ToString() => "TelegramWebhookVerifier [redacted]";
}
