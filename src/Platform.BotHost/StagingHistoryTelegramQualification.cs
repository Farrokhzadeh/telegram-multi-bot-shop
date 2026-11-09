using System.Globalization;
using System.Text.Json;
using Platform.Security;
using Platform.Telegram;

namespace Platform.BotHost;

internal static class StagingHistoryTelegramQualification
{
    private static string ReadPrivate(string path, int max)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException();
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 or > 256 ||
            (File.GetUnixFileMode(path) & (UnixFileMode)63) != 0) throw new UnauthorizedAccessException();
        var content = File.ReadAllText(path).Trim();
        if (content.Length is < 1 or > 256 || content.Length > max || content.Any(char.IsControl)) throw new UnauthorizedAccessException();
        return content;
    }
    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var reply = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!reply.IsSuccessStatusCode) throw new UnauthorizedAccessException();
        await using var body = await reply.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 16 }, ct);
    }
    internal static async Task<int> RunAsync(string tokenPath, string recipientPath, string attemptPath, CancellationToken ct = default)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_STAGING_HISTORY_TELEGRAM_SEND") != "1")
                throw new UnauthorizedAccessException();
            string token = ReadPrivate(tokenPath, 128);
            string recipient = ReadPrivate(recipientPath, 20);
            if (!long.TryParse(recipient, NumberStyles.None, CultureInfo.InvariantCulture, out long chatId) || chatId <= 0 || 
                !Path.IsPathFullyQualified(attemptPath) || Path.GetFileName(attemptPath) != "native-history-preview.attempt")
                throw new UnauthorizedAccessException();
            string parent = Path.GetDirectoryName(attemptPath) ?? throw new UnauthorizedAccessException();
            var dir = new DirectoryInfo(parent);
            if (!dir.Exists || dir.LinkTarget is not null || !dir.Name.StartsWith("staging-telegram-history-", StringComparison.Ordinal) ||
                (File.GetUnixFileMode(parent) & (UnixFileMode)63) != 0 || File.Exists(attemptPath))
                throw new UnauthorizedAccessException();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            string api = "https://api.telegram.org/bot" + token + "/";
            using (var identity = await GetAsync(http, api + "getMe", ct))
            {
                var bot = identity.RootElement.GetProperty("result");
                if (!identity.RootElement.GetProperty("ok").GetBoolean() || !bot.GetProperty("is_bot").GetBoolean() ||
                    !string.Equals(bot.GetProperty("username").GetString(), "testadbadbot", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException();
            }
            using (var identity = await GetAsync(http, api + "getChat?chat_id=" + recipient, ct))
            {
                var chat = identity.RootElement.GetProperty("result");
                if (!identity.RootElement.GetProperty("ok").GetBoolean() || chat.GetProperty("type").GetString() != "private" ||
                    chat.GetProperty("id").GetInt64() != chatId)
                    throw new UnauthorizedAccessException();
            }
            var order = new CustomerHistoricalOrder("STAGE-SYNTHETIC-002", new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc), 2, 600000m, "IRR", null, null, true);
            var draft = CustomerHistoryMessageRenderer.Render(new CustomerHistoricalPage([order], null));
            if (draft.Count != 1 || draft[0].ParseMode is not null || draft[0].HasActionButtons) throw new UnauthorizedAccessException();
            var preview = new CustomerHistoryMessage("[STAGING TEST — SYNTHETIC DATA ONLY]\n" + draft[0].Text + "\nNo charges or deliveries were performed.");
            await using (var marker = new FileStream(attemptPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                byte[] tag = "Attempt consumed. Never automatically retry an unknown Telegram outcome."u8.ToArray();
                await marker.WriteAsync(tag, ct);
                await marker.FlushAsync(ct);
                marker.Flush(true);
            }
            var result = await new CustomerHistoryTelegramSender(http).SendAsync(token, chatId, preview, ct);
            if (result.State != CustomerHistorySendState.Confirmed || result.MessageId is not > 0) throw new UnauthorizedAccessException();
            Console.WriteLine("PASS: exact native staging Telegram history preview sent once to verified private staging chat; synthetic data only.");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Staging Telegram qualification rejected or remote outcome uncertain; no automatic retry.");
            return 78;
        }
    }
}
