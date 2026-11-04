using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Fulfillment.Providers;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class ProdSellerSupplierAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ProductId = "000000000000000000000007";
    private const string Product = """{"id":"000000000000000000000007","name":"Public generated key product","price":1,"publicPrice":2,"stock":10,"delivery":{"type":"instant"},"requiresEmailActivation":false}""";
    private const string Delivered = """{"orderId":"publicOrderOne","status":"delivered","product":{"id":"000000000000000000000007","name":"Public generated key product"},"quantity":2,"amount":2,"deliveredKeys":["public key A","public key B"]}""";
    [Fact]
    public async Task PurchaseCommitsExactKeyAndAcquisitionBeforeOneCompleteManifest()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        int posts = 0;
        HttpRequestMessage? posted = null;
        using var adapter = new ProdSellerSupplierAdapter(f.Secrets, credentials, "public account", [new(ProductId, "key")], Verify, new Handler(async (request, ct) =>
        {
            Assert.Equal(32, Assert.Single(request.Headers.GetValues("X-API-Key")).Length);
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("https://prodseller.com/v1/products/" + ProductId, request.RequestUri!.AbsoluteUri);
                return Reply(Product);
            }
            posts++; posted = request;
            Assert.Equal("https://prodseller.com/v1/orders", request.RequestUri!.AbsoluteUri);
            string key = Assert.Single(request.Headers.GetValues("Idempotency-Key"));
            Assert.True(key.Length <= 100); Assert.StartsWith("purchase:", key);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_request_attempts WHERE operation='submit' AND outcome='in_flight' AND purchase_idempotency_key='" + key + "'", ct));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal(ProductId, body.RootElement.GetProperty("productId").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("quantity").GetInt32());
            return Reply(Delivered);
        }));
        await Configure(f);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, credentials, adapter.InspectCatalogAsync);
        var accepted = await new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 2),
            (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.Purchased, accepted.Outcome);
        var archive = new SyntheticSupplier();
        await new SupplierFulfillmentWorker(new(f.Sessions, f.Secrets), adapter, archive).RunAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct), Ct);
        Assert.Equal(1, posts); Assert.Equal(1, archive.Archives);
        Assert.False(posted!.Headers.Contains("X-API-Key"));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE provider_code='prodseller' AND cost_currency='USDT' AND cost_baseline_amount=1 AND status='succeeded' AND remote_ref='publicOrderOne'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired' AND content_kind='key'", Ct));
        await new DeliveryStore(f.Sessions, f.Secrets).PrepareAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.PrepareManifest, Ct), Ct);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", Ct));
    }
    [Theory]
    [InlineData("activation")]
    [InlineData("custom")]
    [InlineData("missing flag")]
    [InlineData("unknown delivery")]
    public async Task CustomerInputAndUnknownProductsNeverReachPurchaseOrPayableCheckout(string kind)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        int posts = 0;
        string body = kind switch
        {
            "activation" => Product.Replace("\"requiresEmailActivation\":false", "\"requiresEmailActivation\":true", StringComparison.Ordinal),
            "custom" => Product.Replace("\"type\":\"instant\"", "\"type\":\"custom\"", StringComparison.Ordinal).Replace("\"stock\":10", "\"stock\":null", StringComparison.Ordinal),
            "missing flag" => Product.Replace(",\"requiresEmailActivation\":false", "", StringComparison.Ordinal),
            _ => Product.Replace("\"type\":\"instant\"", "\"type\":\"unknown\"", StringComparison.Ordinal)
        };
        using var adapter = new ProdSellerSupplierAdapter(f.Secrets, credentials, "public account", [new(ProductId, "key")], Verify, new Handler((request, _) =>
        { if (request.Method == HttpMethod.Post) posts++; return Task.FromResult(Reply(body)); }));
        var binding = Binding();
        var quote = await adapter.InspectAsync(binding, Ct);
        Assert.False(quote.Ready); Assert.False(quote.EnforcesMaximum);
        using var blocked = await adapter.SubmitAttemptAsync(binding, "purchase:" + Guid.NewGuid().ToString("D"), SupplierRules.UnitCeiling(binding), Ct);
        Assert.Equal(SupplierOutcomeKind.ConfirmedNoEffect, blocked.Kind);
        await Configure(f);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, credentials, adapter.InspectCatalogAsync);
        await Assert.ThrowsAsync<PricingRejectedException>(() => new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 2), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct));
        Assert.Equal(0, posts);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleCredentialAliasesAreOneUnitAndNeverDoubleCounted(bool both)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        string body = Delivered.Replace("\"quantity\":2", "\"quantity\":1", StringComparison.Ordinal).Replace("\"amount\":2", "\"amount\":1", StringComparison.Ordinal)
            .Replace("\"deliveredKeys\":[\"public key A\",\"public key B\"]", both ? "\"deliveredKey\":\"public key A\",\"deliveredKeys\":[\"public key A\"]" : "\"deliveredKey\":\"public key A\"", StringComparison.Ordinal);
        using var adapter = Adapter(f, credentials, new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https://prodseller.com/v1/orders/publicOrderOne", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Reply(body));
        }));
        using var result = await adapter.LookupAsync(Binding() with { Quantity = 1 }, "publicOrderOne", Ct);
        Assert.Equal(SupplierOutcomeKind.Completed, result.Kind); Assert.Single(result.Items);
        Assert.Equal("public key A", Encoding.UTF8.GetString(result.Items[0].Content.Span));
    }
    [Theory]
    [InlineData("pending", SupplierOutcomeKind.Processing)]
    [InlineData("paid", SupplierOutcomeKind.Processing)]
    [InlineData("failed", SupplierOutcomeKind.Unknown)]
    public async Task RecoveryDoesNotInventRefundOrFailureNoEffect(string state, SupplierOutcomeKind expected)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        string body = Delivered.Replace("\"status\":\"delivered\"", "\"status\":\"" + state + "\"", StringComparison.Ordinal).Replace("[\"public key A\",\"public key B\"]", "[]", StringComparison.Ordinal);
        using var adapter = Adapter(f, credentials, new Handler((request, _) => { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Reply(body)); }));
        using var result = await adapter.LookupAsync(Binding(), "publicOrderOne", Ct);
        Assert.Equal(expected, result.Kind); Assert.Equal("publicOrderOne", result.PurchaseReference); Assert.Empty(result.Items);
    }
    [Theory]
    [InlineData("order")]
    [InlineData("product")]
    [InlineData("quantity")]
    [InlineData("duplicate")]
    [InlineData("activation")]
    [InlineData("instructions")]
    [InlineData("aliases")]
    [InlineData("inexact")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task UncertainOrExcludedDeliveryPreservesEvidenceWithoutFabricatedItems(string fault)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        int calls = 0;
        using var adapter = Adapter(f, credentials, new Handler((_, _) =>
        {
            calls++;
            if (fault == "timeout") throw new IOException("public provider transport canary");
            string body = fault switch
            {
                "order" => Delivered.Replace("publicOrderOne", "publicOrderOther", StringComparison.Ordinal),
                "product" => Delivered.Replace(ProductId, "000000000000000000000008", StringComparison.Ordinal),
                "quantity" => Delivered.Replace("\"quantity\":2", "\"quantity\":3", StringComparison.Ordinal),
                "duplicate" => Delivered[..^1] + ",\"orderId\":\"publicOrderOne\"}",
                "activation" => Delivered[..^1] + ",\"activation\":{\"emails\":[\"public@example.invalid\"]}}",
                "instructions" => Delivered[..^1] + ",\"activationInstructions\":[\"public manual instruction\"]}",
                "aliases" => Delivered[..^1] + ",\"deliveredKey\":\"public key A\"}",
                "inexact" => Delivered.Replace("\"amount\":2", "\"amount\":2.00000000000000000000000000001", StringComparison.Ordinal),
                _ => Delivered
            };
            return Task.FromResult(Reply(body, fault == "http" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        }));
        using var result = await adapter.LookupAsync(Binding(), "publicOrderOne", Ct);
        Assert.Equal(SupplierOutcomeKind.Unknown, result.Kind); Assert.Empty(result.Items); Assert.NotEmpty(result.Evidence.ToArray());
        Assert.Equal(1, calls); Assert.DoesNotContain("public provider transport canary", result.ToString());
    }
    [Fact]
    public async Task MissingReferenceOrAttemptKeyNeverCreatesAnotherPurchase()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files, provider: "prodseller");
        int calls = 0;
        using var adapter = Adapter(f, credentials, new Handler((_, _) => { calls++; return Task.FromResult(Reply(Product)); }));
        using var absent = await adapter.LookupAsync(Binding(), null, Ct);
        Assert.Equal(SupplierOutcomeKind.Unknown, absent.Kind); Assert.Equal(0, calls);
        var binding = Binding();
        await adapter.InspectAsync(binding, Ct);
        using var blocked = await adapter.SubmitAsync(binding, SupplierRules.UnitCeiling(binding), Ct);
        Assert.Equal(SupplierOutcomeKind.ConfirmedNoEffect, blocked.Kind); Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.InspectAsync(binding with { Currency = "USD" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetProductAsync("../orders", Ct));
        Assert.Equal(1, calls);
    }
    private static SupplierBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "prodseller", "public account", ProductId, null, "public-local-intent", 2, 1, "USDT", null);
    private static ProdSellerSupplierAdapter Adapter(CheckoutFixture f, Platform.Security.SupplierCredentialSnapshot credentials, HttpMessageHandler handler) =>
        new(f.Secrets, credentials, "public account", [new(ProductId, "key")], Verify, handler);
    private static SupplierItem[] Verify(SupplierBinding binding, ProdSellerOrderReceipt receipt) => receipt.Items.Select(raw => new SupplierItem(Encoding.UTF8.GetString(raw.Span) switch
    { "public key A" => "qualified synthetic ProdSeller unit A", "public key B" => "qualified synthetic ProdSeller unit B", _ => throw new InvalidOperationException("Unqualified synthetic identity.") }, SupplierItemDisposition.Customer, "key", raw.Span)).ToArray();
    private static async Task Configure(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        Guid source = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'prodseller',$3,true)", DbValue.Uuid(source), DbValue.Uuid(f.Product), DbValue.Text(ProductId)), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await session.CommitAsync(Ct);
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
