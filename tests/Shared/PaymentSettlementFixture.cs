using System.Net;
using System.Text;
using Platform.Core;
using Platform.Payments;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public sealed class PaymentSettlementFixture(CheckoutFixture fixture)
{
    public CheckoutFixture Shop { get; } = fixture;
    public CatalogStore Catalog => new(Shop.Sessions, Shop.Secrets, [new("synthetic", "public account")],
        (_, _) => Task.FromResult(new SupplierCatalogResult(SupplierCatalogStatus.Available, 10000, 1, "USD", "public quote")));
    public PaymentSettlementStore Store => new(Shop.Sessions, Shop.Secrets, Catalog);
    public async Task EnableAsync(CancellationToken ct)
    {
        await using var edit = await Shop.Sessions.BeginAsync(ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET allow_crypto=true,allow_wallet_topup=true,zero_balance_topup=true,policy_version=policy_version+1"), ct);
        await edit.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET allow_crypto=true,revision=revision+1"), ct);
        await edit.CommitAsync(ct);
    }
    public async Task<(Guid Payment, Guid Order, string Origin, decimal Due)> OrderAsync(int quantity = 1, CheckoutKind kind = CheckoutKind.ProductPurchase, int user = 0, CancellationToken ct = default)
    {
        var request = new CheckoutRequest(OrderIds.New(), Shop.Product, quantity, CheckoutPayment.Crypto, kind);
        var result = await new WalletCheckoutService(Shop.Sessions, Shop.Store, Catalog).PurchaseAsync(Shop.Context(user), CheckoutFixture.Actor(user), request,
            (_, _, _) => Task.FromResult(ChannelMembership.Member), ct);
        if (result.Outcome != CheckoutOutcome.AwaitingPayment) throw new InvalidOperationException("Synthetic checkout failed.");
        await using var bind = await Shop.Sessions.BeginAsync(ct);
        var payment = await bind.QuerySingleAsync(new DbStatement("SELECT id,idempotency_ref,amount_due::text FROM shop.payments WHERE order_id=$1", DbValue.Uuid(request.OrderId)),
            r => (Id: r.GetGuid(0), Origin: r.GetString(1), Due: ExactDecimal.Parse(r.GetString(2))), ct);
        await new PaymentEvidenceStore(Shop.Sessions, Shop.Secrets).BindInvoiceAsync(bind, Shop.Context(user), CheckoutFixture.Actor(user), payment.Id, Provider(payment.Id), 10.4m, ct);
        await bind.CommitAsync(ct);
        return (payment.Id, request.OrderId, payment.Origin, payment.Due);
    }
    public static string Provider(Guid id) => (10000000000UL + BitConverter.ToUInt32(id.ToByteArray(), 12)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public async Task CaptureAsync(Guid payment, string origin, decimal due, int user = 0, CancellationToken ct = default)
    {
        using var notice = PaymentNoticeFixture.Notice(origin, quote: due, provider: Provider(payment));
        await new PaymentEvidenceStore(Shop.Sessions, Shop.Secrets).CaptureAsync(Shop.Context(user), notice,
            (e, _) => Task.FromResult("protected:synthetic/" + e.PaymentId.ToString("N") + "/" + e.Digest), ct);
    }
    public static NowPaymentsClient Client(Func<string, string>? change = null) => new("public-synthetic-api-key", new Transport(change));
    private sealed class Transport(Func<string, string>? change) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "api.nowpayments.io" || request.Headers.GetValues("x-api-key").Single() != "public-synthetic-api-key") throw new InvalidOperationException("Synthetic merchant request failed.");
            string body = change?.Invoke(request.RequestUri.AbsolutePath) ?? throw new InvalidOperationException("Synthetic response is required.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    public static Task<ProviderPaymentSnapshot> Lookup(InvoiceExpectation invoice, CancellationToken ct)
    {
        return ReadAsync();
        async Task<ProviderPaymentSnapshot> ReadAsync()
        {
            using var client = Client(_ => PaymentNoticeFixture.Body(invoice.Origin, quote: invoice.UsdDue, provider: invoice.ProviderPayment, asset: invoice.Asset, expected: invoice.ExpectedTokens, actual: invoice.ExpectedTokens));
            return await client.LookupAsync(invoice, ct);
        }
    }
    public static async Task RunNativeAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 1, ct: ct);
        var fixture = new PaymentSettlementFixture(f);
        await fixture.EnableAsync(ct);
        var order = await fixture.OrderAsync(ct: ct);
        await fixture.CaptureAsync(order.Payment, order.Origin, order.Due, ct: ct);
        var settled = await fixture.Store.SettleAsync(f.Context(0), order.Payment, Lookup, ct);
        var replay = await fixture.Store.SettleAsync(f.Context(0), order.Payment, (_, _) => throw new InvalidOperationException(), ct);
        if (settled.Route != ExternalSettlementRoute.LocalGoods || replay.Outcome != PaymentSettlementOutcome.Replayed ||
            await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", ct) != 1 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) != 0)
            throw new InvalidOperationException("Native settlement failed.");
    }
}
