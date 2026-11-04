using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Fulfillment.Providers;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class AkundingSupplierAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Product = """{"id":7,"name":"Public synthetic code","unit_label":"code","description":"Generated ready-code fixture","features":"No customer input in this fixture","your_price":1,"stock":10,"available":true}""";
    private const string Delivered = """{"id":71,"status":"delivered","product":{"id":7},"quantity":2,"unit_price":1,"subtotal":2,"discount":0,"payable":2,"payment_method":"wallet","items":["public code A","public code B"]}""";
    [Fact]
    public async Task ExactWirePurchaseArchivesVerifiedItemsAndCompletesTheWholeCustomerManifest()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        int posts = 0;
        string? intent = null;
        HttpRequestMessage? posted = null;
        using var adapter = await Adapter(f, credentials, new Handler(async (request, ct) =>
        {
            Assert.Equal("akunding.shop", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get) return Reply(Product);
            posts++; posted = request;
            intent = Assert.Single(request.Headers.GetValues("X-Idempotency-Key"));
            Assert.Equal("https://akunding.shop/api/v1/orders", request.RequestUri.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal(7, body.RootElement.GetProperty("product_id").GetInt64());
            Assert.Equal(2, body.RootElement.GetProperty("quantity").GetInt32());
            return Reply(Delivered);
        }));
        await Configure(f);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, credentials, adapter.InspectCatalogAsync);
        var result = await new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), new(Guid.CreateVersion7(), f.Product, 2),
            (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.Purchased, result.Outcome);
        var store = new SupplierRequestStore(f.Sessions, f.Secrets);
        var lease = await SupplierFixture.ClaimAsync(f, JobKind.SubmitSupplier, Ct);
        var binding = await store.ReadAsync(f.Context(0), lease, Ct);
        var archive = new SyntheticSupplier();
        await new SupplierFulfillmentWorker(store, adapter, archive).RunAsync(f.Context(0), lease, Ct);
        Assert.Equal(binding.Intent, intent); Assert.Equal(1, posts); Assert.Equal(1, archive.Archives);
        Assert.Null(posted!.Headers.Authorization);
        Assert.False(adapter.ProvenIntentLookup);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests WHERE provider_code='akunding' AND cost_currency='USDT' AND cost_baseline_amount=1 AND status='succeeded' AND remote_ref='71'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", Ct));
        await new DeliveryStore(f.Sessions, f.Secrets).PrepareAsync(f.Context(0), await SupplierFixture.ClaimAsync(f, JobKind.PrepareManifest, Ct), Ct);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", Ct));
    }
    [Theory]
    [InlineData("paid", SupplierOutcomeKind.Processing)]
    [InlineData("failed", SupplierOutcomeKind.ConfirmedNoEffect)]
    [InlineData("draft", SupplierOutcomeKind.Unknown)]
    public async Task OriginalOrderReadsPreserveDocumentedStateWithoutAnotherPost(string status, SupplierOutcomeKind expected)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        using var adapter = await Adapter(f, credentials, new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://akunding.shop/api/v1/orders/71", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Reply(Delivered.Replace("\"status\":\"delivered\"", "\"status\":\"" + status + "\"", StringComparison.Ordinal).Replace("[\"public code A\",\"public code B\"]", "[]", StringComparison.Ordinal)));
        }));
        using var result = await adapter.LookupAsync(Binding(), "71", Ct);
        Assert.Equal(expected, result.Kind); Assert.Equal("71", result.PurchaseReference); Assert.Empty(result.Items);
    }
    [Theory]
    [InlineData("wrong order")]
    [InlineData("wrong product")]
    [InlineData("wrong quantity")]
    [InlineData("duplicate")]
    [InlineData("wrong total")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task BadOrAmbiguousPurchaseEvidenceIsRetainedWithoutInventingSuccess(string fault)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        int calls = 0;
        using var adapter = await Adapter(f, credentials, new Handler((_, _) =>
        {
            calls++;
            if (fault == "timeout") throw new IOException("public private transport canary");
            string body = fault switch
            {
                "wrong order" => Delivered.Replace("\"id\":71", "\"id\":72", StringComparison.Ordinal),
                "wrong product" => Delivered.Replace("\"id\":7}", "\"id\":8}", StringComparison.Ordinal),
                "wrong quantity" => Delivered.Replace("\"quantity\":2", "\"quantity\":3", StringComparison.Ordinal),
                "duplicate" => Delivered.Replace("\"id\":71", "\"id\":71,\"id\":71", StringComparison.Ordinal),
                "wrong total" => Delivered.Replace("\"payable\":2", "\"payable\":3", StringComparison.Ordinal),
                _ => Delivered
            };
            return Task.FromResult(Reply(body, fault == "http" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        }));
        using var result = await adapter.LookupAsync(Binding(), "71", Ct);
        Assert.Equal(SupplierOutcomeKind.Unknown, result.Kind); Assert.Empty(result.Items);
        Assert.NotEmpty(result.Evidence.ToArray()); Assert.DoesNotContain("public private transport canary", result.ToString());
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task MissingOriginalReferenceNeverReplaysThePurchaseAndUninspectedSubmitHasNoRemoteEffect()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        int calls = 0;
        using var adapter = await Adapter(f, credentials, new Handler((_, _) => { calls++; return Task.FromResult(Reply(Delivered)); }));
        var binding = Binding();
        using var unknown = await adapter.LookupAsync(binding, null, Ct);
        using var blocked = await adapter.SubmitAsync(binding, SupplierRules.UnitCeiling(binding), Ct);
        Assert.Equal(SupplierOutcomeKind.Unknown, unknown.Kind);
        Assert.Equal(SupplierOutcomeKind.ConfirmedNoEffect, blocked.Kind);
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreviewedProductChangesAndOverLimitPricesNeverSubmit(bool price)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        int posts = 0;
        using var adapter = await Adapter(f, credentials, new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Task.FromResult(Reply(price ? Product.Replace("\"your_price\":1", "\"your_price\":1.11", StringComparison.Ordinal) : Product.Replace("No customer input in this fixture", "Changed requirements", StringComparison.Ordinal)));
        }));
        var binding = Binding();
        var quote = await adapter.InspectAsync(binding, Ct);
        Assert.False(quote.EnforcesMaximum);
        Assert.Equal(price ? "cost_limit" : "source_ineligible", SupplierRules.Preflight(binding, quote, DateTime.UtcNow));
        using var result = await adapter.SubmitAsync(binding, SupplierRules.UnitCeiling(binding), Ct);
        Assert.Equal(SupplierOutcomeKind.ConfirmedNoEffect, result.Kind); Assert.Equal(0, posts);
    }
    [Fact]
    public async Task UnprovenUnitIdentityArchivesTheOrderWithoutManufacturingFulfillmentReferences()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        using var files = new SyntheticSecretFiles();
        using var credentials = AkundingSourceInspectionTests.Credentials(files);
        using var adapter = await Adapter(f, credentials, new Handler((_, _) => Task.FromResult(Reply(Delivered))), (_, _) => []);
        using var result = await adapter.LookupAsync(Binding(), "71", Ct);
        Assert.Equal(SupplierOutcomeKind.Completed, result.Kind);
        Assert.Empty(result.Items); Assert.NotEmpty(result.Evidence.ToArray());
        Assert.Equal("partial_acquisition", SupplierRules.Validate(Binding(), result));
    }
    private static SupplierBinding Binding() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "akunding", "public account", "7", null, Guid.NewGuid().ToString("N"), 2, 1, "USDT", null);
    private static async Task<AkundingSupplierAdapter> Adapter(CheckoutFixture f, SupplierCredentialSnapshot credentials, HttpMessageHandler handler, Func<SupplierBinding, AkundingOrderReceipt, SupplierItem[]>? verify = null)
    {
        using var preview = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(Product))));
        var chosen = await new AkundingCatalogClient(preview).GetProductAsync(7, Ct);
        return new(f.Secrets, credentials, "public account", [new(7, "code", chosen.EligibilityFingerprint!)], verify ?? VerifySyntheticUnits, handler);
    }
    private static SupplierItem[] VerifySyntheticUnits(SupplierBinding binding, AkundingOrderReceipt receipt)
    {
        Assert.Equal(2, binding.Quantity);
        return receipt.Items.Select(raw => new SupplierItem(Encoding.UTF8.GetString(raw.Span) switch { "public code A" => "qualified synthetic unit A", "public code B" => "qualified synthetic unit B", _ => throw new InvalidOperationException("Unqualified synthetic identity.") }, SupplierItemDisposition.Customer, "code", raw.Span)).ToArray();
    }
    private static async Task Configure(CheckoutFixture f)
    {
        await using var session = await f.Sessions.BeginAsync(Ct);
        Guid source = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'akunding','7',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
        await session.CommitAsync(Ct);
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
