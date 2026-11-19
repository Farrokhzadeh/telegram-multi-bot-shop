using System.Globalization;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Platform.Telegram;

public enum CustomerHistorySendState { Confirmed, Rejected, Unknown }
public sealed record CustomerHistorySendOutcome(CustomerHistorySendState State, long? MessageId = null)
{
    public override string ToString() => "CustomerHistorySendOutcome [redacted]";
}
public sealed class CustomerHistoryTelegramSender(HttpClient http)
{
    public Task<CustomerHistorySendOutcome> SendAsync(string token, VerifiedTelegramUpdate update, CustomerHistoryMessage message, CancellationToken ct = default)
    {
        int separator = token?.IndexOf(':') ?? -1;
        if (token is null || update is null || update.IsCallback || update.UpdateId <= 0 || update.MessageId <= 0 || update.ActorId <= 0 ||
            !CustomerHistoryCommandRouter.TryParsePage(update.Value, out _) || separator <= 0 ||
            !long.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long bot) ||
            bot != update.Context.BotId || bot.ToString(CultureInfo.InvariantCulture) != token[..separator])
            throw new TelegramIngressRejectedException();
        return SendAsync(token, update.ActorId, message, ct);
    }
    public async Task<CustomerHistorySendOutcome> SendAsync(string token, long privateChat, CustomerHistoryMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 38 or > 128 || token.Count(c => c == ':') != 1 ||
            token[0] is < '0' or > '9' || token.AsSpan(0, token.IndexOf(':')).IndexOfAnyExcept("0123456789".AsSpan()) >= 0 ||
            token.AsSpan(token.IndexOf(':') + 1).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) >= 0 ||
            privateChat <= 0 || message is null || message.ParseMode is not null || message.HasActionButtons ||
            string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > 3500)
            throw new ArgumentException("Historical Telegram send scope rejected.");
        try
        {
            var result = await TelegramTransport.ReadRawResultAsync(token, http,
                (client, cancel) => client.SendMessage(privateChat, message.Text, disableNotification: true,
                    protectContent: true, cancellationToken: cancel), ct);
            if (result.TryGetProperty("message_id", out var messageId) && messageId.TryGetInt64(out long id) && id > 0 &&
                result.TryGetProperty("chat", out var chat) &&
                chat.TryGetProperty("id", out var recipient) && recipient.TryGetInt64(out long recipientId) && recipientId == privateChat &&
                chat.TryGetProperty("type", out var type) && type.GetString() == "private")
                return new(CustomerHistorySendState.Confirmed, id);
            return new(CustomerHistorySendState.Unknown);
        }
        catch (RequestException exception) when (exception.InnerException is not (JsonException or ApiRequestException) &&
            TelegramTransport.StatusCode(exception) is >= 400 and < 500 and not 429)
        { return new(CustomerHistorySendState.Rejected); }
        catch (Exception exception) when (exception is RequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        { return new(CustomerHistorySendState.Unknown); }
    }
}
