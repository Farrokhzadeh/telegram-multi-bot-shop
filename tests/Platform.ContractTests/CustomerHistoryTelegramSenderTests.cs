using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Platform.Security;
using System.Text;
using System.Text.Json;
using Telegram.Bot.Types.Enums;
using Platform.Telegram;

namespace Platform.ContractTests;

public sealed class CustomerHistoryTelegramSenderTests
{
    private const string StageOnlySyntheticToken = "12345:" + "x0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private sealed class Stub(string body, HttpStatusCode status = HttpStatusCode.OK, bool loseResult = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Posted { get; private set; }
        public Uri? Target { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Target = request.RequestUri;
            Posted = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (loseResult) throw new HttpRequestException("Synthetic lost confirmation.");
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private static VerifiedTelegramUpdate PrivateHistory(long bot, string command)
    {
        var registry = new BotBrandRegistry([new("synthetic-history", "bound", Guid.NewGuid(), bot, Guid.NewGuid(), 1, true)]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bound")], "synthetic-history"));
        if (!registry.TryResolveAuthenticated("synthetic-history", principal, out var context)) throw new InvalidOperationException();
        byte[] secret = SHA256.HashData("synthetic verified history sender"u8);
        using var verifier = new TelegramWebhookVerifier(context!, secret);
        string header = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string payload = "{\"update_id\":34,\"message\":{\"message_id\":77,\"from\":{\"id\":7001,\"is_bot\":false},\"chat\":{\"id\":7001,\"type\":\"private\"},\"text\":\"" + command + "\"}}";
        return verifier.Verify([header], Encoding.UTF8.GetBytes(payload));
    }
    [Fact]
    public async Task VerifiedHistorySenderBindsRecipientAndBotToAuthenticatedUpdate()
    {
        var stub = new Stub("""{"ok":true,"result":{"message_id":99,"chat":{"id":7001,"type":"private"}}}""");
        using var http = new HttpClient(stub);
        var sender = new CustomerHistoryTelegramSender(http);
        var verified = PrivateHistory(12345, "/orders");
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => sender.SendAsync(StageOnlySyntheticToken, PrivateHistory(12346, "/orders"), new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => sender.SendAsync(StageOnlySyntheticToken, PrivateHistory(12345, "/start"), new("Preview"), TestContext.Current.CancellationToken));
        Assert.Equal(0, stub.Calls);
        var result = await sender.SendAsync(StageOnlySyntheticToken, verified, new("Historical orders (read-only)"), TestContext.Current.CancellationToken);
        Assert.Equal(CustomerHistorySendState.Confirmed, result.State);
        Assert.Equal(1, stub.Calls);
        using var request = JsonDocument.Parse(stub.Posted!);
        Assert.Equal(7001L, request.RootElement.GetProperty("chat_id").GetInt64());
    }
    [Fact]
    public async Task SendsOneScopedPlaintextMessageAndRequiresExactPrivateChatAcknowledgement()
    {
        var fake = new Stub("""{"ok":true,"result":{"message_id":99,"chat":{"id":7001,"type":"private"}}}""");
        using var http = new HttpClient(fake);
        var message = new CustomerHistoryMessage("Historical orders (read-only)\nOriginal total: 600000 IRR\nPayment status: Unknown");
        var result = await new CustomerHistoryTelegramSender(http).SendAsync(StageOnlySyntheticToken, 7001, message, TestContext.Current.CancellationToken);
        Assert.Equal(CustomerHistorySendState.Confirmed, result.State);
        Assert.Equal(99L, result.MessageId);
        Assert.Equal(1, fake.Calls);
        Assert.EndsWith("/sendMessage", fake.Target!.AbsolutePath, StringComparison.Ordinal);
        using var request = JsonDocument.Parse(fake.Posted!);
        var posted = request.RootElement;
        Assert.Equal(7001L, posted.GetProperty("chat_id").GetInt64());
        Assert.Contains("600000 IRR", posted.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.True(posted.GetProperty("disable_notification").GetBoolean());
        Assert.True(posted.GetProperty("protect_content").GetBoolean());
        Assert.False(posted.TryGetProperty("parse_mode", out _));
        Assert.False(posted.TryGetProperty("reply_markup", out _));
        Assert.Equal("CustomerHistorySendOutcome [redacted]", result.ToString());
    }
    [Theory]
    [InlineData("""{"ok":true,"result":{"message_id":1,"chat":{"id":7002,"type":"private"}}}""")]
    [InlineData("""{"ok":true,"result":{"message_id":1,"chat":{"id":7001,"type":"group"}}}""")]
    [InlineData("""{"ok":true,"result":{"message_id":1}}""")]
    [InlineData("""{"ok":false}""")]
    public async Task ContradictoryOrIncompleteRemoteAcknowledgementIsUnknown(string body)
    {
        var fake = new Stub(body);
        using var http = new HttpClient(fake);
        var result = await new CustomerHistoryTelegramSender(http).SendAsync(StageOnlySyntheticToken, 7001, new("Preview"), TestContext.Current.CancellationToken);
        Assert.Equal(CustomerHistorySendState.Unknown, result.State);
        Assert.Null(result.MessageId);
        Assert.Equal(1, fake.Calls);
    }
    [Theory]
    [InlineData(429, """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":1}}""")]
    [InlineData(502, "")]
    [InlineData(400, """{"ok":false}""")]
    public async Task RemoteRejectionNeverAutoretries(int status, string body)
    {
        var fake = new Stub(body, (HttpStatusCode)status);
        using var http = new HttpClient(fake);
        var outcome = await new CustomerHistoryTelegramSender(http).SendAsync(StageOnlySyntheticToken, 7001, new("Preview"), TestContext.Current.CancellationToken);
        Assert.Equal(status >= 500 || status == 429 ? CustomerHistorySendState.Unknown : CustomerHistorySendState.Rejected, outcome.State);
        Assert.Equal(1, fake.Calls);
    }
    [Fact]
    public async Task LostRemoteResponseIsUnknownWithoutBlindResend()
    {
        var fake = new Stub("", loseResult: true);
        using var http = new HttpClient(fake);
        var result = await new CustomerHistoryTelegramSender(http).SendAsync(StageOnlySyntheticToken, 7001, new("Preview"), TestContext.Current.CancellationToken);
        Assert.Equal(CustomerHistorySendState.Unknown, result.State);
        Assert.Equal(1, fake.Calls);
    }
    [Fact]
    public async Task PollingRetainsUnmodeledPropertiesAndUsesTheExplicitOffset()
    {
        var fake = new Stub("""{"ok":true,"result":[{"update_id":35,"future_field":{"value":"original"}}]}""");
        using var http = new HttpClient(fake);
        var result = await TelegramTransport.GetUpdatesAsync(StageOnlySyntheticToken, http, 34, 12, 25,
            [UpdateType.Message, UpdateType.CallbackQuery], TestContext.Current.CancellationToken);
        Assert.Equal("original", result[0].GetProperty("future_field").GetProperty("value").GetString());
        Assert.Equal(1, fake.Calls);
        using var request = JsonDocument.Parse(fake.Posted!);
        Assert.Equal(34, request.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(25, request.RootElement.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task InvalidRecipientTokenOrActionBearingMessageFailsBeforeNetwork()
    {
        var fake = new Stub("");
        using var http = new HttpClient(fake);
        var sender = new CustomerHistoryTelegramSender(http);
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(StageOnlySyntheticToken, -1001, new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("not-a-bot-token", 7001, new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(":" + new string('x', 40), 7001, new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(StageOnlySyntheticToken, 7001, new(new string('X', 3501)), TestContext.Current.CancellationToken));
        Assert.Equal(0, fake.Calls);
    }
}
