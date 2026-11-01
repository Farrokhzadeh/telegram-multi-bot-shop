using System.Net;
using System.Text;
using Platform.Core;
using Platform.Payments;

namespace Platform.IntegrationTests;

public sealed class NowPaymentsClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("{\"payment_id\":123,\"payment_id\":456}")]
    [InlineData("{\"payment_id\":9007199254740992}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{bad")]
    public async Task MalformedOrAmbiguousAuthenticatedResponsesCannotProduceSettlementProof(string body)
    {
        using var client = new NowPaymentsClient("public-synthetic-api-key", new Response(body, HttpStatusCode.OK));
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.LookupAsync(new("123", "public origin", 10, "usdttrc20", 10.4m), Ct));
    }
    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task RedirectAndProviderFailuresDoNotRetryOrProduceProof(int status)
    {
        var transport = new Response("{}", (HttpStatusCode)status);
        using var client = new NowPaymentsClient("public-synthetic-api-key", transport);
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.LookupAsync(new("123", "public origin", 10, "usdttrc20", 10.4m), Ct));
        Assert.Equal(1, transport.Calls);
    }
    [Fact]
    public async Task OversizedBodyIsRejectedAndCallerCannotSelectAnotherProviderHost()
    {
        var transport = new Response(new string(' ', 65537), HttpStatusCode.OK);
        using var client = new NowPaymentsClient("public-synthetic-api-key", transport);
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.LookupAsync(new("123", "public origin", 10, "usdttrc20", 10.4m), Ct));
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.LookupAsync(new("../other-host", "public origin", 10, "usdttrc20", 10.4m), Ct));
        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain("public-synthetic-api-key", client.ToString(), StringComparison.Ordinal);
    }
    private sealed class Response(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.nowpayments.io/v1/payment/123", request.RequestUri!.AbsoluteUri);
            Assert.Equal("public-synthetic-api-key", request.Headers.GetValues("x-api-key").Single());
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
