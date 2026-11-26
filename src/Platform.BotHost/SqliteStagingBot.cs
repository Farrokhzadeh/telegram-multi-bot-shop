using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;

namespace Platform.BotHost;

internal static class SqliteStagingBot
{
    internal static async Task<int> RunAsync(CancellationToken ct = default)
    {
        string? format = Environment.GetEnvironmentVariable("SHOP_SQLITE_STAGING");
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("INSTANCE_ID") != "staging" ||
            format != "synthetic-acc-abad-v2")
            return HostBootstrap.ConfigurationRequiredExitCode;
        string token;
        string key;
        long botId, tester;
        try
        {
            botId = Number("/stage/tester/bot-id");
            tester = Number("/stage/tester/tester-id");
            token = Private("/stage/tester/bot-token", 160).Trim();
            key = Private("/stage/tester/api-key", 128).Trim();
            if (token.Split(':')[0] != botId.ToString(CultureInfo.InvariantCulture) || key.Length != 64 ||
                !key.All(char.IsAsciiHexDigit) || token.Length is < 35 or > 128)
                throw new UnauthorizedAccessException();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
            or OverflowException or ArgumentException)
        {
            Console.Error.WriteLine("Private SQLite staging Telegram identity is unavailable.");
            return HostBootstrap.ConfigurationRequiredExitCode;
        }
        using var telegram = TelegramTransport.CreateHttpClient(TimeSpan.FromSeconds(45));
        using var api = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:18081"),
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 32768
        };
        api.DefaultRequestHeaders.Add("X-Staging-Key", key);
        try
        {
            using (var health = await api.GetAsync("/health/sqlite", ct))
                if (!health.IsSuccessStatusCode || !health.Headers.TryGetValues("X-Sqlite-Format", out var values) ||
                    !values.SequenceEqual(["2"], StringComparer.Ordinal)) throw new UnauthorizedAccessException();
            var identity = await TelegramTransport.ReadRawResultAsync(token, telegram,
                (client, cancel) => client.GetMe(cancel), ct);
            var webhook = await TelegramTransport.ReadRawResultAsync(token, telegram,
                (client, cancel) => client.GetWebhookInfo(cancel), ct);
            if (identity.GetProperty("id").GetInt64() != botId ||
                identity.GetProperty("username").GetString() != "testadbadbot" ||
                !identity.GetProperty("is_bot").GetBoolean() ||
                !string.IsNullOrEmpty(webhook.GetProperty("url").GetString()))
                throw new UnauthorizedAccessException("Staging bot identity or exclusive polling was not verified.");
            var cursor = await api.GetStringAsync("/staging/cursor", ct);
            if (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) || offset < 0)
                throw new UnauthorizedAccessException();
            Console.WriteLine("The managed SQLite staging bot is polling Telegram.");
            while (!ct.IsCancellationRequested)
            {
                JsonElement updates;
                try
                {
                    updates = await TelegramTransport.GetUpdatesAsync(token, telegram, offset, 10, 9,
                        [UpdateType.Message, UpdateType.CallbackQuery], ct);
                }
                catch (Exception error) when (error is RequestException or HttpRequestException or TaskCanceledException)
                {
                    Console.Error.WriteLine("A staging Telegram poll was inconclusive.");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                foreach (var update in updates.EnumerateArray())
                {
                    int id = update.GetProperty("update_id").GetInt32();
                    if (id < offset) continue;
                    if (TryRead(update, tester, botId, out var kind, out var value, out var anchor,
                        out var prior, out var callbackId))
                    {
                        try
                        {
                            if (callbackId is not null) await AnswerCallbackAsync(token, telegram, callbackId, ct);
                            using var payload = WriteUpdate(id, tester, kind, value, anchor, prior);
                            using var response = await api.PostAsync("/staging/update",
                                new ByteArrayContent(payload.ToArray())
                                {
                                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                                }, ct);
                            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
                            {
                                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
                                var reply = json.RootElement;
                                if (reply.GetProperty("accepted").GetBoolean())
                                {
                                    string answer = reply.GetProperty("text").GetString() ?? "";
                                    if (answer.Length is > 0 and <= 4096)
                                    {
                                        var keyboard = Keyboard(reply);
                                        int messageId;
                                        if (reply.GetProperty("edit").GetBoolean() && anchor is int old)
                                        {
                                            try
                                            {
                                                _ = await TelegramTransport.ReadRawResultAsync(token, telegram,
                                                    (client, cancel) => client.EditMessageText(tester, old, answer,
                                                        replyMarkup: keyboard, cancellationToken: cancel), ct);
                                            }
                                            catch (ApiRequestException error) when (error.ErrorCode == 400 &&
                                                error.Message.Contains("message is not modified", StringComparison.Ordinal))
                                            { }
                                            messageId = old;
                                        }
                                        else
                                        {
                                            var sent = await TelegramTransport.ReadRawResultAsync(token, telegram,
                                                (client, cancel) => client.SendMessage(tester, answer,
                                                    replyMarkup: keyboard, protectContent: true, cancellationToken: cancel), ct);
                                            messageId = sent.GetProperty("message_id").GetInt32();
                                        }
                                        if (reply.TryGetProperty("ack", out var ack) && ack.ValueKind == JsonValueKind.String &&
                                            ack.GetString() == "catalog" &&
                                            reply.GetProperty("product").TryGetGuid(out var product) &&
                                            reply.GetProperty("revision").TryGetInt64(out var revision))
                                        {
                                            using var confirm = WriteAck(id, tester, messageId, product, revision);
                                            using var ackResponse = await api.PostAsync("/staging/ack",
                                                new ByteArrayContent(confirm.ToArray())
                                                {
                                                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                                                }, ct);
                                            if (!ackResponse.IsSuccessStatusCode)
                                                Console.Error.WriteLine("Staging catalog anchor was not saved.");
                                        }
                                    }
                                }
                            }
                            else Console.Error.WriteLine("The SQLite staging API rejected an update.");
                        }
                        catch (Exception error) when (error is RequestException or HttpRequestException or TaskCanceledException
                            or JsonException or InvalidOperationException or ArgumentException)
                        { Console.Error.WriteLine("A staging Telegram response was inconclusive."); }
                    }
                    offset = checked(id + 1);
                }
            }
            return 0;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or RequestException or HttpRequestException
            or InvalidOperationException or JsonException or ArgumentException or IOException)
        {
            Console.Error.WriteLine("The staging Telegram transport could not be initialized.");
            return HostBootstrap.ConfigurationRequiredExitCode;
        }
    }

    private static bool TryRead(JsonElement update, long tester, long bot, out string kind,
        out string value, out int? anchor, out string? prior, out string? callbackId)
    {
        kind = ""; value = ""; anchor = null; prior = null; callbackId = null;
        if (update.TryGetProperty("message", out var message))
        {
            if (!PrivateChat(message, tester, tester) || !message.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String) return false;
            string? original = text.GetString();
            if (original is null || original.Length is < 1 or > 4096) return false;
            kind = "message"; value = original;
            return true;
        }
        if (update.TryGetProperty("callback_query", out var callback))
        {
            if (!callback.TryGetProperty("from", out var actor) ||
                !actor.TryGetProperty("id", out var sender) || sender.GetInt64() != tester ||
                !callback.TryGetProperty("message", out var msg) || !PrivateChat(msg, tester, bot) ||
                !msg.TryGetProperty("from", out var origin) || !origin.TryGetProperty("id", out var from) ||
                from.GetInt64() != bot || !callback.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.String || !callback.TryGetProperty("id", out var query))
                return false;
            string? command = data.GetString();
            if (command is null || command.Length is < 4 or > 64 || !command.StartsWith("ak:", StringComparison.Ordinal))
                return false;
            kind = "callback"; value = command; anchor = msg.GetProperty("message_id").GetInt32();
            prior = msg.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() : null;
            callbackId = query.GetString();
            return callbackId is { Length: > 0 and < 130 };
        }
        return false;
    }

    private static bool PrivateChat(JsonElement message, long tester, long senderExpected)
    {
        if (!message.TryGetProperty("chat", out var chat) ||
            !chat.TryGetProperty("type", out var type) || type.GetString() != "private" ||
            !chat.TryGetProperty("id", out var chatId) || chatId.GetInt64() != tester ||
            !message.TryGetProperty("from", out var sender) ||
            !sender.TryGetProperty("id", out var from) || from.GetInt64() != senderExpected ||
            (!sender.TryGetProperty("is_bot", out var isBot) || isBot.GetBoolean() != (senderExpected != tester)))
            return false;
        return true;
    }

    private static InlineKeyboardMarkup? Keyboard(JsonElement reply)
    {
        if (!reply.TryGetProperty("buttons", out var buttons) || buttons.ValueKind == JsonValueKind.Null)
            return null;
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var button in buttons.EnumerateArray())
        {
            string title = button.GetProperty("text").GetString() ?? "";
            string value = button.GetProperty("data").GetString() ?? "";
            if (title.Length is < 1 or > 80 || value.Length is < 1 or > 56)
                throw new ArgumentException("Invalid staging callback data.");
            rows.Add([InlineKeyboardButton.WithCallbackData(title, "ak:" + value)]);
        }
        return rows.Count == 0 ? null : new InlineKeyboardMarkup(rows);
    }

    private static async Task AnswerCallbackAsync(string token, HttpClient http, string id, CancellationToken ct)
    {
        _ = await TelegramTransport.ReadRawResultAsync(token, http,
            (client, cancel) => client.SendRequest(new AnswerCallbackQueryRequest { CallbackQueryId = id }, cancel), ct);
    }

    private static MemoryStream WriteUpdate(int update, long tester, string kind, string value, int? anchor, string? prior)
    {
        var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = false }))
        {
            json.WriteStartObject();
            json.WriteNumber("updateId", update);
            json.WriteNumber("user", tester);
            json.WriteString("kind", kind);
            json.WriteString("value", value);
            if (anchor is int id) json.WriteNumber("anchor", id);
            if (prior is not null) json.WriteString("messageText", prior);
            json.WriteEndObject();
        }
        return output;
    }

    private static MemoryStream WriteAck(int update, long tester, int anchor, Guid product, long revision)
    {
        var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("updateId", update);
            json.WriteNumber("user", tester);
            json.WriteNumber("anchor", anchor);
            json.WriteString("product", product);
            json.WriteNumber("revision", revision);
            json.WriteEndObject();
        }
        return output;
    }

    private static long Number(string path) =>
        long.TryParse(Private(path, 32).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result : throw new UnauthorizedAccessException();

    private static string Private(string path, int maximum)
    {
        if (!OperatingSystem.IsLinux()) throw new UnauthorizedAccessException();
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length is < 1 or > 160 ||
            file.Length > maximum || (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0)
            throw new UnauthorizedAccessException();
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
    }
}
