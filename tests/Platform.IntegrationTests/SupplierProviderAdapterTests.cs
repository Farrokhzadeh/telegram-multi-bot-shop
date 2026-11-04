using System.Net;
using System.Text;
using Platform.Core;
using Platform.Fulfillment.Providers;

namespace Platform.IntegrationTests;

public sealed class SupplierProviderAdapterTests
{
	private static SupplierBinding Bound(int quantity = 2, string provider = "synthetic") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, provider, "public account", "public product", "public variant", "public intent", quantity, 10, "USD", null);
	[Fact]
	public async Task AkundingAdapterParsesQuoteAndPurchasesWithIdempotencyHeader()
	{
		var binding = Bound(2, "akunding");
		var handler = new TestHandler(req =>
		{
			if (req.Method == HttpMethod.Get)
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"price": 10.0, "in_stock": true, "currency": "USD"}""", Encoding.UTF8, "application/json") };
			Assert.True(req.Headers.Contains("X-Idempotency-Key"));
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"order_id": "ak-123", "charged_amount": 20.0, "status": "completed", "items": [{"code": "key-1"}, {"code": "key-2"}]}""", Encoding.UTF8, "application/json") };
		});
		using var adapter = new AkundingAdapter(binding.Account, "test-key", handler);
		var quote = await adapter.InspectAsync(binding, default);
		Assert.True(quote.Ready);
		Assert.Equal(10.0m, quote.UnitCost);
		using var outcome = await adapter.SubmitAsync(binding, 11m, default);
		Assert.Equal(SupplierOutcomeKind.Completed, outcome.Kind);
		Assert.Equal("ak-123", outcome.PurchaseReference);
		Assert.Equal(2, outcome.Items.Count);
		Assert.Equal("key-1", Encoding.UTF8.GetString(outcome.Items[0].Content.Span));
	}
	[Fact]
	public async Task ProdSellerAdapterParsesQuoteAndDeliveredKeys()
	{
		var binding = Bound(2, "prodseller");
		var handler = new TestHandler(req =>
		{
			if (req.Method == HttpMethod.Get)
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"price_usd": 9.5, "stock": 10, "is_instant": true}""", Encoding.UTF8, "application/json") };
			Assert.True(req.Headers.Contains("Idempotency-Key"));
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id": "ps-777", "total_charge": 19.0, "delivered_keys": ["ps-k1", "ps-k2"]}""", Encoding.UTF8, "application/json") };
		});
		using var adapter = new ProdSellerAdapter(binding.Account, "test-key", handler);
		var quote = await adapter.InspectAsync(binding, default);
		Assert.True(quote.Ready);
		Assert.Equal(9.5m, quote.UnitCost);
		using var outcome = await adapter.SubmitAsync(binding, 11m, default);
		Assert.Equal(SupplierOutcomeKind.Completed, outcome.Kind);
		Assert.Equal("ps-777", outcome.PurchaseReference);
		Assert.Equal(2, outcome.Items.Count);
	}
	[Fact]
	public async Task RoboticVnAdapterFetchesCredentialsWithVariantSupport()
	{
		var binding = Bound(2, "roboticvn");
		var handler = new TestHandler(req =>
		{
			if (req.RequestUri!.AbsolutePath.EndsWith("/credentials"))
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"total_usd": 20.0, "data": [{"credential": "user:pass1"}, {"credential": "user:pass2"}]}""", Encoding.UTF8, "application/json") };
			if (req.Method == HttpMethod.Get)
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"cost": 10.0, "available_units": 5}""", Encoding.UTF8, "application/json") };
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"order_id": "rvn-888"}""", Encoding.UTF8, "application/json") };
		});
		using var adapter = new RoboticVnAdapter(binding.Account, "test-key", handler);
		var quote = await adapter.InspectAsync(binding, default);
		Assert.True(quote.Ready);
		using var outcome = await adapter.SubmitAsync(binding, 11m, default);
		Assert.Equal(SupplierOutcomeKind.Completed, outcome.Kind);
		Assert.Equal("rvn-888", outcome.PurchaseReference);
		Assert.Equal(2, outcome.Items.Count);
		Assert.Equal("account", outcome.Items[0].ContentKind);
	}
	[Fact]
	public async Task CanbosoAdapterMarksExtraQuantityAsBonus()
	{
		var binding = Bound(2, "canboso");
		var handler = new TestHandler(req =>
		{
			if (req.Method == HttpMethod.Get)
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"unit_price": 8.0, "stock_count": 20, "is_ready": true}""", Encoding.UTF8, "application/json") };
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"purchase_id": "can-999", "total_paid": 16.0, "cards": ["card-1", "card-2", "bonus-card-3"]}""", Encoding.UTF8, "application/json") };
		});
		using var adapter = new CanbosoAdapter(binding.Account, "test-key", handler);
		var quote = await adapter.InspectAsync(binding, default);
		Assert.True(quote.Ready);
		using var outcome = await adapter.SubmitAsync(binding, 11m, default);
		Assert.Equal(SupplierOutcomeKind.Completed, outcome.Kind);
		Assert.Equal(3, outcome.Items.Count);
		Assert.Equal(SupplierItemDisposition.Customer, outcome.Items[0].Disposition);
		Assert.Equal(SupplierItemDisposition.Customer, outcome.Items[1].Disposition);
		Assert.Equal(SupplierItemDisposition.Bonus, outcome.Items[2].Disposition);
	}
	[Fact]
	public async Task RapidXAdapterInspectsAndSubmitsOrders()
	{
		var binding = Bound(2, "rapidx");
		var handler = new TestHandler(req =>
		{
			if (req.Method == HttpMethod.Get)
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"price": 10.0, "stock": 5}""", Encoding.UTF8, "application/json") };
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"transaction_id": "rx-555", "total_amount": 20.0, "keys": ["rx-1", "rx-2"]}""", Encoding.UTF8, "application/json") };
		});
		using var adapter = new RapidXAdapter(binding.Account, "test-key", handler);
		var quote = await adapter.InspectAsync(binding, default);
		Assert.True(quote.Ready);
		using var outcome = await adapter.SubmitAsync(binding, 11m, default);
		Assert.Equal(SupplierOutcomeKind.Completed, outcome.Kind);
		Assert.Equal("rx-555", outcome.PurchaseReference);
		Assert.Equal(2, outcome.Items.Count);
	}
	private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
	}
}
