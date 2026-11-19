using System.Text.Json;

namespace Platform.Telegram;

public static class StagingPreviewCommand
{
    public static bool Accepts(JsonElement update, long tester)
    {
        return tester > 0 && update.ValueKind == JsonValueKind.Object &&
            update.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("from", out var sender) && sender.ValueKind == JsonValueKind.Object &&
            sender.TryGetProperty("id", out var senderId) && senderId.ValueKind == JsonValueKind.Number &&
            senderId.TryGetInt64(out long actor) && actor == tester &&
            sender.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.False &&
            message.TryGetProperty("chat", out var chat) && chat.ValueKind == JsonValueKind.Object &&
            chat.TryGetProperty("id", out var chatId) && chatId.ValueKind == JsonValueKind.Number &&
            chatId.TryGetInt64(out long recipient) && recipient == tester &&
            chat.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "private" &&
            message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
            text.GetString() is "/start" or "/status" or "/help";
    }
}
