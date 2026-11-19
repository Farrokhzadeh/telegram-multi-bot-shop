using System.Net;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;

namespace Platform.Telegram;

public static class TelegramTransport
{
    public static TelegramBotClient Create(string token, HttpClient http) =>
        new(new TelegramBotClientOptions(token) { RetryThreshold = 0 }, http);

    public static HttpClient CreateHttpClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = 1048576
        };

    public static int? StatusCode(RequestException error) =>
        error.HttpStatusCode is HttpStatusCode status ? (int)status :
        error is ApiRequestException api ? api.ErrorCode : null;

    public static Task<JsonElement> GetUpdatesAsync(string token, HttpClient http, int offset, int limit,
        int timeout, IEnumerable<UpdateType> allowed, CancellationToken ct) =>
        ReadRawResultAsync(token, http, (client, cancel) => client.GetUpdates(offset, limit, timeout, allowed, cancel), ct);

    // Capture the authenticated response before the SDK discards fields it does not model.
    // Each call has its own SDK client, so response listeners cannot receive a different request.
    public static async Task<JsonElement> ReadRawResultAsync<T>(string token, HttpClient http,
        Func<ITelegramBotClient, CancellationToken, Task<T>> request, CancellationToken ct)
    {
        var client = Create(token, http);
        JsonElement result = default;
        HttpStatusCode? observedStatus = null;
        client.OnApiResponseReceived += async (_, args, cancel) =>
        {
            observedStatus = args.ResponseMessage.StatusCode;
            if (!args.ResponseMessage.IsSuccessStatusCode) return;
            byte[] response = await args.ResponseMessage.Content.ReadAsByteArrayAsync(cancel);
            using var body = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            if (body.RootElement.TryGetProperty("result", out var value)) result = value.Clone();
        };
        try { _ = await request(client, ct); }
        catch (ApiRequestException error) when (observedStatus is HttpStatusCode status && (int)status != error.ErrorCode)
        { throw new RequestException("Telegram reported contradictory error status.", observedStatus.GetValueOrDefault(), error); }
        if (result.ValueKind == JsonValueKind.Undefined)
            throw new RequestException("Telegram response result is unavailable.");
        return result;
    }
}
