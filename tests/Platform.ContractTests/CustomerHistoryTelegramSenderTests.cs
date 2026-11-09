using System.Net;
using System.Text;
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
        Assert.Contains("chat_id=7001", fake.Posted!, StringComparison.Ordinal);
        Assert.Contains("600000+IRR", fake.Posted!, StringComparison.Ordinal);
        Assert.Contains("disable_notification=true", fake.Posted!, StringComparison.Ordinal);
        Assert.Contains("protect_content=true", fake.Posted!, StringComparison.Ordinal);
        Assert.DoesNotContain("parse_mode", fake.Posted!, StringComparison.Ordinal);
        Assert.DoesNotContain("reply_markup", fake.Posted!, StringComparison.Ordinal);
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
    [InlineData(429, "")]
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
    public async Task InvalidRecipientTokenOrActionBearingMessageFailsBeforeNetwork()
    {
        var fake = new Stub("");
        using var http = new HttpClient(fake);
        var sender = new CustomerHistoryTelegramSender(http);
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(StageOnlySyntheticToken, -1001, new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("not-a-bot-token", 7001, new("Preview"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(StageOnlySyntheticToken, 7001, new(new string('X', 3501)), TestContext.Current.CancellationToken));
        Assert.Equal(0, fake.Calls);
    }
}
