using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Payments;

namespace Platform.IntegrationTests;

public sealed class NowPaymentsHostedInvoiceTests
{
    private const string Origin = "order:aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa";
    private static readonly Uri Callback = new("https://shop.example/webhooks/nowpayments/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(CryptoAsset.UsdtTrc20, "usdttrc20")]
    [InlineData(CryptoAsset.UsdtBsc, "usdtbsc")]
    public async Task UsesFrozenQuoteAndOnlyTheSelectedUsdtNetwork(CryptoAsset network, string asset)
    {
        int requests = 0;
        using var client = new NowPaymentsClient("synthetic-api-key-only", new Handler(async (request, ct) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.nowpayments.io/v1/invoice", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-api-key-only", Assert.Single(request.Headers.GetValues("x-api-key")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var payload = body.RootElement;
            Assert.Equal(5, payload.EnumerateObject().Count());
            Assert.Equal(12.35m, payload.GetProperty("price_amount").GetDecimal());
            Assert.Equal("usd", payload.GetProperty("price_currency").GetString());
            Assert.Equal(asset, payload.GetProperty("pay_currency").GetString());
            Assert.Equal(Origin, payload.GetProperty("order_id").GetString());
            Assert.Equal(Callback.AbsoluteUri, payload.GetProperty("ipn_callback_url").GetString());
            return Response(asset, Origin, "12.35", "https://nowpayments.io/payment/?iid=1234");
        }));
        var result = await client.CreateHostedInvoiceAsync(Origin, 12.35m, network, Callback, Ct);
        Assert.Equal("1234", result.ProviderInvoice);
        Assert.Equal("https://nowpayments.io/payment/?iid=1234", result.CheckoutUrl.AbsoluteUri);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("trx", Origin, "12.35", "https://nowpayments.io/payment/?iid=1234")]
    [InlineData("usdtbsc", "another-order", "12.35", "https://nowpayments.io/payment/?iid=1234")]
    [InlineData("usdtbsc", Origin, "12.34", "https://nowpayments.io/payment/?iid=1234")]
    [InlineData("usdtbsc", Origin, "12.350000000000000000000000000001", "https://nowpayments.io/payment/?iid=1234")]
    [InlineData("usdtbsc", Origin, "12.35", "https://unrelated.example/payment/?iid=1234")]
    [InlineData("usdtbsc", Origin, "12.35", "https://nowpayments.io/payment/?iid=5678")]
    public async Task RefusesMismatchedOrUntrustedInvoiceResponse(string asset, string origin, string due, string url)
    {
        using var client = new NowPaymentsClient("synthetic-api-key-only", new Handler((_, _) => Task.FromResult(Response(asset, origin, due, url))));
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.CreateHostedInvoiceAsync(Origin, 12.35m, CryptoAsset.UsdtBsc, Callback, Ct));
    }

    [Fact]
    public async Task RejectsUnsupportedNetworkBeforeContactingTransport()
    {
        using var client = new NowPaymentsClient("synthetic-api-key-only", new Handler((_, _) => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => client.CreateHostedInvoiceAsync(Origin, 12.35m, (CryptoAsset)99, Callback, Ct));
    }

    private static HttpResponseMessage Response(string asset, string origin, string due, string url) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent($$"""
                {"id":1234,"order_id":"{{origin}}","price_amount":"{{due}}","price_currency":"usd","pay_currency":"{{asset}}","invoice_url":"{{url}}"}
                """, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
