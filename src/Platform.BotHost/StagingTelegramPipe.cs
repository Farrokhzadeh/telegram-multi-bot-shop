using System.Text;
using System.Text.Json;
using Platform.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Platform.BotHost;

internal static class StagingTelegramPipe
{
    internal static async Task<int> RunAsync(TextReader input, TextWriter output, HttpClient http, CancellationToken ct = default)
    {
        string? token = await input.ReadLineAsync(ct);
        if (token is null or { Length: < 24 or > 128 } ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or ':'))) return 78;

        while (await input.ReadLineAsync(ct) is string line)
        {
            string? method = null;
            try
            {
                if (line.Length > 16384) throw new ArgumentException();
                using var request = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 10 });
                var parameters = request.RootElement.GetProperty("parameters");
                method = request.RootElement.GetProperty("method").GetString();
                JsonElement result = await InvokeAsync(token, http, method, parameters, ct);
                using var response = new MemoryStream();
                using (var writer = new Utf8JsonWriter(response))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("ok", true);
                    writer.WritePropertyName("result");
                    result.WriteTo(writer);
                    writer.WriteEndObject();
                }
                await output.WriteLineAsync(Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length)));
            }
            catch (ApiRequestException error) when (method == "editMessageText" && error.ErrorCode == 400 &&
                error.Message.StartsWith("Bad Request: message is not modified", StringComparison.Ordinal))
            {
                await output.WriteLineAsync("""{"ok":true,"result":true}""");
            }
            catch (RequestException error)
            {
                int? status = TelegramTransport.StatusCode(error);
                await output.WriteLineAsync(status is int value ? FormattableString.Invariant($$"""{"ok":false,"status":{{value}}}""") : """{"ok":false}""");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or FormatException or OverflowException)
            {
                await output.WriteLineAsync("""{"ok":false,"status":400}""");
            }
            await output.FlushAsync(ct);
        }
        return 0;
    }

    private static Task<JsonElement> InvokeAsync(string token, HttpClient http, string? method, JsonElement args,
        CancellationToken ct) => method switch
        {
            "getMe" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) => client.GetMe(cancel), ct),
            "getWebhookInfo" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) => client.GetWebhookInfo(cancel), ct),
            "getUpdates" => TelegramTransport.GetUpdatesAsync(token, http, Number(args, "offset", 0), Number(args, "limit", 100),
                Number(args, "timeout", 0), args.GetProperty("allowed_updates").EnumerateArray().Select(UpdateKind).ToArray(), ct),
            "sendMessage" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) =>
                client.SendMessage(PrivateChat(args), Text(args, "text"), parseMode: Formatting(args), replyMarkup: Keyboard(args),
                    protectContent: Protected(args), cancellationToken: cancel), ct),
            "sendRichMessage" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) =>
                client.SendRichMessage(PrivateChat(args), RichMessage(args) ?? throw new ArgumentException(),
                    replyMarkup: Keyboard(args), protectContent: Protected(args), cancellationToken: cancel), ct),
            "editMessageText" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) =>
                client.EditMessageText(PrivateChat(args), args.GetProperty("message_id").GetInt32(), OptionalText(args, "text"),
                    parseMode: Formatting(args), replyMarkup: Keyboard(args), richMessage: RichMessage(args), cancellationToken: cancel), ct),
            "answerCallbackQuery" => TelegramTransport.ReadRawResultAsync(token, http, (client, cancel) =>
                client.SendRequest(new AnswerCallbackQueryRequest { CallbackQueryId = Text(args, "callback_query_id") }, cancel), ct),
            _ => throw new ArgumentException()
        };

    private static UpdateType UpdateKind(JsonElement kind) => kind.GetString() switch
    {
        "message" => UpdateType.Message,
        "callback_query" => UpdateType.CallbackQuery,
        _ => throw new ArgumentException()
    };

    private static int Number(JsonElement args, string name, int defaultValue) =>
        args.TryGetProperty(name, out var value) ? value.GetInt32() : defaultValue;

    private static long PrivateChat(JsonElement args)
    {
        long chat = args.GetProperty("chat_id").GetInt64();
        return chat > 0 ? chat : throw new ArgumentException();
    }

    private static string Text(JsonElement args, string name) =>
        args.GetProperty(name).GetString() ?? throw new ArgumentException();

    private static string? OptionalText(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static bool Protected(JsonElement args) =>
        args.TryGetProperty("protect_content", out var value) && value.GetBoolean();

    private static ParseMode Formatting(JsonElement args) => OptionalText(args, "parse_mode") switch
    {
        null => ParseMode.None,
        "HTML" => ParseMode.Html,
        _ => throw new ArgumentException()
    };

    private static InputRichMessage? RichMessage(JsonElement args) =>
        args.TryGetProperty("rich_message", out var markup)
            ? new InputRichMessage { Html = Text(markup, "html"), IsRtl = markup.TryGetProperty("is_rtl", out var rtl) && rtl.GetBoolean() }
            : null;

    private static InlineKeyboardMarkup? Keyboard(JsonElement args) =>
        args.TryGetProperty("reply_markup", out var markup)
            ? new InlineKeyboardMarkup(markup.GetProperty("inline_keyboard").EnumerateArray().Select(row =>
                row.EnumerateArray().Select(Button).ToArray()).ToArray())
            : null;

    private static InlineKeyboardButton Button(JsonElement args)
    {
        var button = InlineKeyboardButton.WithCallbackData(Text(args, "text"), Text(args, "callback_data"));
        button.IconCustomEmojiId = OptionalText(args, "icon_custom_emoji_id");
        button.Style = OptionalText(args, "style") switch
        {
            null => null,
            "primary" => KeyboardButtonStyle.Primary,
            "success" => KeyboardButtonStyle.Success,
            "danger" => KeyboardButtonStyle.Danger,
            _ => throw new ArgumentException()
        };
        return button;
    }
}
