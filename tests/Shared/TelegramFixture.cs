using System.Text;
using System.Text.Json;

namespace Platform.Testing;

public static class TelegramFixture
{
    public static string Header(ReadOnlySpan<byte> secret) => Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Body(string value, bool callback = false, long updateId = 1, long actorId = 7001, long chatId = 7001,
        long botId = 1001, long messageId = 77, string chatType = "private", bool senderBot = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("update_id", updateId);
            writer.WritePropertyName(callback ? "callback_query" : "message"); writer.WriteStartObject();
            writer.WritePropertyName("from"); writer.WriteStartObject(); writer.WriteNumber("id", actorId); writer.WriteBoolean("is_bot", senderBot); writer.WriteEndObject();
            if (callback) { writer.WriteString("data", value); writer.WritePropertyName("message"); writer.WriteStartObject(); writer.WritePropertyName("from"); writer.WriteStartObject(); writer.WriteNumber("id", botId); writer.WriteBoolean("is_bot", true); writer.WriteEndObject(); }
            writer.WriteNumber("message_id", messageId); writer.WritePropertyName("chat"); writer.WriteStartObject(); writer.WriteNumber("id", chatId); writer.WriteString("type", chatType); writer.WriteEndObject();
            if (!callback) writer.WriteString("text", value);
            writer.WriteEndObject(); if (callback) writer.WriteEndObject(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    public static byte[] Replace(byte[] body, string before, string after) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace(before, after, StringComparison.Ordinal));
}
