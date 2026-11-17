using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

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

    [Fact]
    public async Task SharedMerchantBindsCheckoutAndCallbackToTheOriginalBrand()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var wallet = new WalletCheckoutService(f.Sessions, f.Store, fixture.Catalog);
        Guid first = OrderIds.New(), second = OrderIds.New(), third = OrderIds.New();
        Task<ChannelMembership> Member(long _, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await wallet.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(first, f.Product, 1, CheckoutPayment.Crypto), Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await wallet.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), new(second, f.Product, 1, CheckoutPayment.Crypto), Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.AwaitingPayment, (await wallet.PurchaseAsync(f.Context(2), CheckoutFixture.Actor(2), new(third, f.Product, 1, CheckoutPayment.Crypto), Member, Ct)).Outcome);
        string origin = "order:" + first.ToString("D");
        int requests = 0;
        using var client = new NowPaymentsClient("synthetic-api-key-only", new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var payload = body.RootElement;
            int invoice = ++requests + 1233;
            return new(HttpStatusCode.Created) { Content = new StringContent($$"""
                {"id":{{invoice}},"order_id":{{payload.GetProperty("order_id").GetRawText()}},"price_amount":{{payload.GetProperty("price_amount").GetRawText()}},"price_currency":"usd","pay_currency":{{payload.GetProperty("pay_currency").GetRawText()}},"invoice_url":"https://nowpayments.io/payment/?iid={{invoice}}"}
                """, Encoding.UTF8, "application/json") };
        }));
        var evidence = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        var hosted = new HostedPaymentService(evidence, client, Callback);
        var a = await hosted.IssueForOrderAsync(f.Context(0), CheckoutFixture.Actor(0), first, ct: Ct);
        var b = await hosted.IssueForOrderAsync(f.Context(1), CheckoutFixture.Actor(1), second, ct: Ct);
        var c = await hosted.IssueForOrderAsync(f.Context(2), CheckoutFixture.Actor(2), third, ct: Ct);
        Assert.Equal(3, new[] { a.ProviderInvoice, b.ProviderInvoice, c.ProviderInvoice }.Distinct().Count());
        Assert.Equal(a, await hosted.IssueForOrderAsync(f.Context(0), CheckoutFixture.Actor(0), first, ct: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hosted.IssueForOrderAsync(f.Context(1), CheckoutFixture.Actor(1), first, ct: Ct));
        Assert.Equal(3, requests);
        using var read = PaymentNoticeFixture.Verifier();
        VerifiedPaymentNotice MakeNotice(string invoice)
        {
            string body = PaymentNoticeFixture.Body(origin, quote: 10m).Replace("\"order_id\":", "\"invoice_id\":" + invoice + ",\"order_id\":", StringComparison.Ordinal);
            return read.Verify([PaymentNoticeFixture.Sign(body)], Encoding.UTF8.GetBytes(body));
        }
        Task<string> Archive(ProtectedPaymentEvidence item, CancellationToken _) => Task.FromResult("protected:synthetic/" + item.Digest);
        using (var forged = MakeNotice(b.ProviderInvoice))
            await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => evidence.CaptureAsync(f.Context(0), forged, Archive, Ct));
        using var notice = MakeNotice(a.ProviderInvoice);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => evidence.CaptureAsync(f.Context(1), notice, Archive, Ct));
        var capture = await evidence.CaptureAsync(f.Context(0), notice, Archive, Ct);
        Assert.Equal(PaymentCaptureOutcome.Ready, capture.Outcome);
        Assert.Equal(PaymentCaptureOutcome.Replayed, (await evidence.CaptureAsync(f.Context(0), notice, Archive, Ct)).Outcome);
        var settled = await fixture.Store.SettleAsync(f.Context(0), capture.PaymentId, PaymentSettlementFixture.Lookup, Ct);
        Assert.Equal(ExternalSettlementRoute.LocalGoods, settled.Route);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.payments WHERE hosted_invoice_ref IS NOT NULL AND provider_payment_ref IS NULL", Ct));
    }

    [Fact]
    public async Task UncertainProviderResponseCannotCreateAnotherInvoice()
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: Ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(Ct);
        var store = new PaymentEvidenceStore(f.Sessions, f.Secrets);
        Guid payment = await store.CreateTopupAsync(f.Context(0), CheckoutFixture.Actor(0), Guid.NewGuid(), new(10m, Currency.Usd), CryptoAsset.UsdtTrc20, Ct);
        int requests = 0;
        using var client = new NowPaymentsClient("synthetic-api-key-only", new Handler((_, _) =>
        {
            requests++;
            throw new HttpRequestException();
        }));
        var service = new HostedPaymentService(store, client, Callback);
        await Assert.ThrowsAsync<PaymentNoticeRejectedException>(() => service.IssueForTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, ct: Ct));
        var restarted = new HostedPaymentService(new PaymentEvidenceStore(new(f.Database.Data), f.Secrets), client, Callback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.IssueForTopupAsync(f.Context(0), CheckoutFixture.Actor(0), payment, ct: Ct));
        Assert.Equal(1, requests);
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
