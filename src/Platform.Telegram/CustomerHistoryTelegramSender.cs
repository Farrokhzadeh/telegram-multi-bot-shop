using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Platform.Telegram;

public enum CustomerHistorySendState { Confirmed, Rejected, Unknown }
public sealed record CustomerHistorySendOutcome(CustomerHistorySendState State, long? MessageId = null)
{
    public override string ToString() => "CustomerHistorySendOutcome [redacted]";
}
public sealed class CustomerHistoryTelegramSender(HttpClient http)
{
    public async Task<CustomerHistorySendOutcome> SendAsync(string token, long privateChat, CustomerHistoryMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 38 or > 128 || token.Count(c => c == ':') != 1 ||
            token.AsSpan(0, token.IndexOf(':')).IndexOfAnyExcept("0123456789".AsSpan()) >= 0 ||
            token.AsSpan(token.IndexOf(':') + 1).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) >= 0 ||
            privateChat <= 0 || message is null || message.ParseMode is not null || message.HasActionButtons ||
            string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > 3500)
            throw new ArgumentException("Historical Telegram send scope rejected.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.telegram.org/bot" + token + "/sendMessage"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = privateChat.ToString(CultureInfo.InvariantCulture),
            ["text"] = message.Text,
            ["disable_notification"] = "true",
            ["protect_content"] = "true"
        });
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                return new(CustomerHistorySendState.Unknown);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 12 }, ct);
            var root = json.RootElement;
            if (response.IsSuccessStatusCode &&
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var messageId) && messageId.TryGetInt64(out long id) && id > 0 &&
                result.TryGetProperty("chat", out var chat) &&
                chat.TryGetProperty("id", out var recipient) && recipient.TryGetInt64(out long recipientId) && recipientId == privateChat &&
                chat.TryGetProperty("type", out var type) && type.GetString() == "private")
                return new(CustomerHistorySendState.Confirmed, id);
            return new(response.IsSuccessStatusCode ? CustomerHistorySendState.Unknown : CustomerHistorySendState.Rejected);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        { return new(CustomerHistorySendState.Unknown); }
    }
}
