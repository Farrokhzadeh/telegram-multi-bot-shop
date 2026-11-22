using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Platform.JobHost;

internal static class SqliteStagingWorker
{
    internal static async Task<int> RunAsync(CancellationToken ct = default)
    {
        SqliteStagingConfiguration state;
        SecretSnapshot secrets;
        string token;
        try
        {
            state = await SqliteStagingConfiguration.LoadAsync(ct);
            secrets = state.LoadSecrets(SecretService.JobHost);
            token = state.ReadTelegramToken();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or SecretConfigurationException
            or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("The staging SQLite worker identity is unavailable.");
            return HostBootstrap.ConfigurationRequiredExitCode;
        }
        using (secrets)
        using (var http = TelegramTransport.CreateHttpClient(TimeSpan.FromSeconds(45)))
        {
            try
            {
                JsonElement bot = await TelegramTransport.ReadRawResultAsync(token, http,
                    (client, cancel) => client.GetMe(cancel), ct);
                if (bot.GetProperty("id").GetInt64() != state.TelegramBotId ||
                    bot.GetProperty("username").GetString() != "testadbadbot")
                    throw new UnauthorizedAccessException();
                var workerId = Guid.CreateVersion7();
                var jobs = new SqliteStagingJobs(state.Sessions);
                var status = new SqliteStatusDeliveryStore(state.Sessions);
                var credentials = new SqliteDeliveryStore(state.Sessions, secrets);
                var expiry = new SqlitePaymentExpiryStore(state.Sessions);
                var preorders = new SqliteLocalPreorderStore(state.Sessions, secrets);
                var coupons = new SqliteCouponDeliveryStore(state.Sessions, secrets);
                var suppliers = new SqliteSupplierRequestStore(state.Sessions, secrets);
                var supplierWorker = new SqliteSupplierFulfillmentWorker(suppliers,
                    new SqliteSyntheticSupplierAdapter(), new SqliteSyntheticSupplierEvidenceArchive());
                Task inventory = SqliteStageInventoryBridge.ServeAsync(state.Sessions, secrets,
                    state.ReadApiKey(), ct);
                Console.WriteLine("The managed SQLite staging delivery worker is running.");
                while (!ct.IsCancellationRequested)
                {
                    if (inventory.IsFaulted)
                        throw new InvalidOperationException("Staging inventory import bridge is unavailable.");
                    await status.RecoverExpiredAsync(state.Context, ct);
                    await coupons.RecoverExpiredAsync(state.Context, ct);
                    foreach (Guid expired in await jobs.ExpiredCredentialsAsync(state.Context, ct))
                        await credentials.RecoverExpiredAsync(state.Context, expired, ct);
                    JobLease? due = await expiry.ClaimAsync(state.Context, workerId, ct);
                    if (due is not null)
                    {
                        _ = await expiry.RunAsync(state.Context, due, ct);
                        continue;
                    }
                    JobLease? waiting = await preorders.ClaimAsync(workerId, ct);
                    if (waiting is not null)
                    {
                        _ = await preorders.DrainAsync(waiting, [state.Context], ct);
                        continue;
                    }
                    JobLease? supplier = await suppliers.ClaimAsync(state.Context,
                        JobKind.SubmitSupplier, workerId, ct) ?? await suppliers.ClaimAsync(state.Context,
                        JobKind.ReconcileSupplier, workerId, ct);
                    if (supplier is not null)
                    {
                        await supplierWorker.RunAsync(state.Context, supplier, ct);
                        continue;
                    }
                    JobLease? coupon = await coupons.ClaimNoticeAsync(state.Context, workerId, ct);
                    if (coupon is not null)
                    {
                        await coupons.PrepareAsync(state.Context, coupon, ct);
                        continue;
                    }
                    JobLease? prepare = await jobs.ClaimAsync(state.Context, JobKind.PrepareManifest, workerId, ct);
                    if (prepare is not null)
                    {
                        try { _ = await credentials.PrepareAsync(state.Context, prepare, ct); }
                        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
                        {
                            await jobs.RetryPrepareAsync(state.Context, prepare, ct);
                        }
                        continue;
                    }
                    JobLease? send = await jobs.ClaimAsync(state.Context, JobKind.SendTelegram, workerId, ct);
                    if (send is null)
                    {
                        await Task.Delay(700, ct);
                        continue;
                    }
                    string purpose = await jobs.PurposeAsync(state.Context, send, ct);
                    if (purpose == "order_status")
                    {
                        SqliteStatusDispatch? dispatch = await status.BeginAsync(state.Context, send, ct);
                        if (dispatch is null) continue;
                        if (dispatch.ChatId != state.SyntheticActor)
                            throw new UnauthorizedAccessException("A status destination was not the staging actor.");
                        DeliveryOutcome outcome = await SendTextAsync(token, http, state.Recipient,
                            dispatch.TargetMessageId, dispatch.Text, ct);
                        await status.RecordOutcomeAsync(state.Context, send, outcome, ct);
                        continue;
                    }
                    if (purpose == "discount_notice")
                    {
                        SqliteCouponDispatch? dispatch = await coupons.BeginAsync(state.Context, send, ct);
                        if (dispatch is null) continue;
                        if (dispatch.ChatId != state.SyntheticActor)
                            throw new UnauthorizedAccessException("A prepaid notice destination was not the staging actor.");
                        DeliveryOutcome outcome = await SendTextAsync(token, http, state.Recipient,
                            null, dispatch.Text, ct);
                        await coupons.RecordOutcomeAsync(state.Context, send, outcome, ct);
                        continue;
                    }
                    if (purpose == "order_credentials")
                    {
                        using DeliveryDispatch? dispatch = await credentials.BeginAsync(state.Context, send, ct);
                        if (dispatch is null) continue;
                        if (dispatch.ChatId != state.SyntheticActor || dispatch.BotId != state.Context.BotId)
                            throw new UnauthorizedAccessException("A credential destination was not the staging actor.");
                        DeliveryOutcome outcome = dispatch.IsFile
                            ? await SendDocumentAsync(token, http, state.Recipient, dispatch, ct)
                            : await SendTextAsync(token, http, state.Recipient, dispatch.TargetMessageId,
                                new UTF8Encoding(false, true).GetString(dispatch.Content.Span), ct);
                        await credentials.RecordOutcomeAsync(state.Context, send, outcome, ct);
                        continue;
                    }
                    throw new UnauthorizedAccessException("The staged worker does not serve this Telegram purpose.");
                }
                return 0;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or RequestException
                or HttpRequestException or InvalidOperationException or JsonException or IOException)
            {
                Console.Error.WriteLine("The SQLite staging worker stopped without retrying an unknown remote effect.");
                return HostBootstrap.ConfigurationRequiredExitCode;
            }
        }
    }

    private static async Task<DeliveryOutcome> SendTextAsync(string token, HttpClient http, long recipient,
        long? target, string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return new(DeliveryOutcomeKind.Uneditable);
        try
        {
            JsonElement result = target is long message
                ? await TelegramTransport.ReadRawResultAsync(token, http,
                    (client, cancel) => client.EditMessageText(recipient, checked((int)message),
                        value, cancellationToken: cancel), ct)
                : await TelegramTransport.ReadRawResultAsync(token, http,
                    (client, cancel) => client.SendMessage(recipient, value,
                        protectContent: true, cancellationToken: cancel), ct);
            return Verify(result, recipient, target);
        }
        catch (ApiRequestException error) when (target is not null && error.ErrorCode == 400 &&
            error.Message.Contains("message is not modified", StringComparison.Ordinal))
        { return new(DeliveryOutcomeKind.Succeeded, target); }
        catch (ApiRequestException error) when (error.ErrorCode is >= 400 and < 500 and not 429)
        { return new(DeliveryOutcomeKind.Uneditable); }
        catch (Exception error) when (error is RequestException or HttpRequestException
            or JsonException or IOException or OperationCanceledException)
        { return new(DeliveryOutcomeKind.Unknown); }
    }

    private static async Task<DeliveryOutcome> SendDocumentAsync(string token, HttpClient http,
        long recipient, DeliveryDispatch dispatch, CancellationToken ct)
    {
        if (dispatch.TargetMessageId is not null || dispatch.Content.Length is < 1 or > 16777216)
            return new(DeliveryOutcomeKind.Uneditable);
        byte[] file = dispatch.Content.ToArray();
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(recipient.ToString(CultureInfo.InvariantCulture)), "chat_id");
            form.Add(new StringContent("true"), "protect_content");
            form.Add(new ByteArrayContent(file)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
            }, "document", "akanti-staging-credentials.bin");
            using var response = await http.PostAsync("https://api.telegram.org/bot" + token + "/sendDocument",
                form, ct);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.TooManyRequests
                    ? new(DeliveryOutcomeKind.Uneditable) : new(DeliveryOutcomeKind.Unknown);
            byte[] json = await response.Content.ReadAsByteArrayAsync(ct);
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (!parsed.RootElement.GetProperty("ok").GetBoolean())
                return new(DeliveryOutcomeKind.Unknown);
            return Verify(parsed.RootElement.GetProperty("result"), recipient, null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException
            or OperationCanceledException)
        { return new(DeliveryOutcomeKind.Unknown); }
        finally { CryptographicOperations.ZeroMemory(file); }
    }

    private static DeliveryOutcome Verify(JsonElement result, long recipient, long? expected)
    {
        if (!result.TryGetProperty("message_id", out var message) ||
            !message.TryGetInt64(out long id) || id <= 0 ||
            expected is long target && id != target ||
            !result.TryGetProperty("chat", out var chat) ||
            !chat.TryGetProperty("type", out var type) || type.GetString() != "private" ||
            !chat.TryGetProperty("id", out var owner) || owner.GetInt64() != recipient)
            return new(DeliveryOutcomeKind.Unknown);
        return new(DeliveryOutcomeKind.Succeeded, id);
    }
}
