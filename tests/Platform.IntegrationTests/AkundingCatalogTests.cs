using System.Net;
using System.Text;
using Platform.Fulfillment.Providers;

namespace Platform.IntegrationTests;

public sealed class AkundingCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Product = """{"id":7,"name":"Public test account","unit_label":"account","your_price":1.25,"stock":2,"available":true}""";
    [Fact]
    public async Task CatalogSearchPagesByStableIdentityAndRetainsSoldOutChoices()
    {
        string soldOut = Product.Replace("\"id\":7", "\"id\":9", StringComparison.Ordinal).Replace("\"stock\":2", "\"stock\":0", StringComparison.Ordinal).Replace("\"available\":true", "\"available\":false", StringComparison.Ordinal);
        using var handler = new Handler(request =>
        {
            Assert.Equal("https://akunding.shop/api/v1/products?include_out_of_stock=true", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            return Reply("[" + soldOut + "," + Product + "]");
        });
        using var http = new HttpClient(handler);
        var catalog = new AkundingCatalogClient(http);
        var first = await catalog.ListProductsAsync("PUBLIC", null, 1, Ct);
        Assert.Equal(7, Assert.Single(first.Items).Id); Assert.Equal(7L, first.Next);
        var second = await catalog.ListProductsAsync("PUBLIC", first.Next, 1, Ct);
        Assert.Equal(9, Assert.Single(second.Items).Id); Assert.False(second.Items[0].Available); Assert.Null(second.Next);
        Assert.Equal(9, Assert.Single((await catalog.ListProductsAsync("9", null, 100, Ct)).Items).Id);
        Assert.Empty((await catalog.ListProductsAsync("absent", null, 100, Ct)).Items);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    public async Task InvalidListNeverBecomesAnEmptySuccessfulCatalog(string body)
    {
        using var handler = new Handler(_ => Reply(body));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AkundingCatalogClient(http).ListProductsAsync("", null, 100, Ct));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIdentitiesAndOverlargeCatalogsAreRejectedBeforeFiltering(bool oversized)
    {
        string body = "[" + string.Join(",", Enumerable.Repeat(Product, oversized ? 1001 : 2)) + "]";
        using var handler = new Handler(_ => Reply(body));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AkundingCatalogClient(http).ListProductsAsync("absent", null, 100, Ct));
    }
    [Fact]
    public async Task InvalidQueryAndCallerCancellationNeverProduceCatalogSuccess()
    {
        using var handler = new Handler(_ => Reply("[]"));
        using var http = new HttpClient(handler);
        var catalog = new AkundingCatalogClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListProductsAsync("", 0, 10, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListProductsAsync("", null, 101, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListProductsAsync(new string('x', 81), null, 10, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListProductsAsync("\n", null, 10, Ct));
        Assert.Equal(0, handler.Calls);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ListProductsAsync("", null, 10, cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task ReadsOnlyExactSelectedProductAndPreservesSupplierBillingCurrency()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://akunding.shop/api/v1/products/7", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            return Reply(Product);
        });
        using var http = new HttpClient(handler);
        var product = await new AkundingCatalogClient(http).GetProductAsync(7, Ct);
        Assert.Equal(1.25m, product.UnitPrice);
        Assert.Equal("USDT", product.Currency);
        Assert.Equal(2, product.Stock);
        Assert.True(product.Available);
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("id", "8")]
    [InlineData("your_price", "0")]
    [InlineData("your_price", "1.00000000000000000000000000001")]
    [InlineData("stock", "-1")]
    [InlineData("available", "false")]
    public async Task RejectsChangedIdentityAndInvalidPriceStockOrAvailability(string field, string replacement)
    {
        string original = field switch { "id" => "7", "your_price" => "1.25", "stock" => "2", _ => "true" };
        using var handler = new Handler(_ => Reply(Product.Replace("\"" + field + "\":" + original, "\"" + field + "\":" + replacement, StringComparison.Ordinal)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AkundingCatalogClient(http).GetProductAsync(7, Ct));
        Assert.Equal("Supplier catalog is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task HttpFailuresNeverRetryOrReturnStaleData(int status)
    {
        using var handler = new Handler(_ => Reply("public failure", (HttpStatusCode)status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AkundingCatalogClient(http).GetProductAsync(7, Ct));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"id":7,"id":7}""")]
    public async Task MissingMalformedOrAmbiguousDataFailsClosed(string json)
    {
        using var handler = new Handler(_ => Reply(json));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AkundingCatalogClient(http).GetProductAsync(7, Ct));
    }
    [Fact]
    public async Task SoldOutProductIsMetadataRatherThanAProductSelectionOrPurchase()
    {
        using var handler = new Handler(_ => Reply(Product.Replace("\"stock\":2", "\"stock\":0", StringComparison.Ordinal).Replace("\"available\":true", "\"available\":false", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);
        var product = await new AkundingCatalogClient(http).GetProductAsync(7, Ct);
        Assert.False(product.Available);
        Assert.Equal(0, product.Stock);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task OversizedResponseAndNonpositiveIdentityAreRejected()
    {
        using var handler = new Handler(_ => Reply(new string('x', 1048577)));
        using var http = new HttpClient(handler);
        var client = new AkundingCatalogClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetProductAsync(0, Ct));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetProductAsync(7, Ct));
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(request)); }
    }
}
