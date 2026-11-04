using System.Net;
using System.Text;
using Platform.Core;
using Platform.Fulfillment.Providers;

namespace Platform.ContractTests;

public sealed class SupplierContractTests
{
	private static SupplierBinding Bound(int quantity = 5, string provider = "synthetic") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, provider, "public account", "public product", "public variant", "public intent", quantity, 10, "USD", null);
	[Theory]
	[InlineData("10", true)]
	[InlineData("11", true)]
	[InlineData("11.01", false)]
	[InlineData("9", true)]
	public void OriginalCostCeilingIsInclusiveNonrollingAndRequiresEnforcedReadyExactCurrency(string cost, bool allowed)
	{
		var binding = Bound();
		var quote = new SupplierQuote(binding.Provider, binding.Account, binding.Product, binding.Variant, "USD", decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), true, true, DateTime.UtcNow, "protected:synthetic/quote");
		Assert.Equal(allowed, SupplierRules.Preflight(binding, quote, DateTime.UtcNow) is null);
		Assert.Equal(11, SupplierRules.UnitCeiling(binding));
		Assert.Equal("cost_unverified", SupplierRules.Preflight(binding, quote with { EnforcesMaximum = false }, DateTime.UtcNow));
		Assert.Equal("cost_unverified", SupplierRules.Preflight(binding, quote with { Currency = "TOMAN" }, DateTime.UtcNow));
		Assert.Equal("source_ineligible", SupplierRules.Preflight(binding, quote with { Ready = false }, DateTime.UtcNow));
		Assert.Equal("identity_mismatch", SupplierRules.Preflight(binding, quote with { Account = "another account" }, DateTime.UtcNow));
		Assert.Equal("cost_unverified", SupplierRules.Preflight(binding, quote with { ObservedAt = DateTime.UtcNow.AddMinutes(-1) }, DateTime.UtcNow));
	}
	[Fact]
	public void BonusCustomerIdentityIsExplicitDisjointAndOrderIndependent()
	{
		var binding = Bound();
		using var result = Outcome(5, 1);
		Assert.Null(SupplierRules.Validate(binding, result));
		using var reordered = new SupplierOutcome(SupplierOutcomeKind.Completed, "public purchase", "USD", 50, result.Items.Reverse().Select(i => new SupplierItem(i.Reference, i.Disposition, i.ContentKind, i.Content.Span)).ToArray(), "public wire"u8);
		Assert.Null(SupplierRules.Validate(binding, reordered));
		Assert.Equal(SupplierRules.ItemKey(binding, result.Items[0].Reference), SupplierRules.ItemKey(binding, reordered.Items[^1].Reference));
		using var noBonus = Outcome(5, 0);
		Assert.Null(SupplierRules.Validate(binding, noBonus));
		using var duplicate = new SupplierOutcome(SupplierOutcomeKind.Completed, "public purchase", "USD", 50, [new("same", SupplierItemDisposition.Customer, "code", "first"u8), new("same", SupplierItemDisposition.Bonus, "code", "second"u8)], "public wire"u8);
		Assert.Equal("identity_mismatch", SupplierRules.Validate(binding, duplicate));
		using var ambiguous = new SupplierOutcome(SupplierOutcomeKind.Completed, "public purchase", "USD", 50, [new("unclear", SupplierItemDisposition.Unverified, "code", "public credential"u8)], "public wire"u8);
		Assert.Equal("identity_mismatch", SupplierRules.Validate(binding, ambiguous));
		Assert.DoesNotContain("public credential", ambiguous.ToString());
		Assert.DoesNotContain("public credential", ambiguous.Items[0].ToString());
	}
	[Fact]
	public void TimeoutAndPartialResultsNeverImplyNoEffectOrCompleteDelivery()
	{
		var binding = Bound();
		using var partial = Outcome(3, 0);
		Assert.Equal("partial_acquisition", SupplierRules.Validate(binding, partial));
		using var unknown = new SupplierOutcome(SupplierOutcomeKind.Unknown, null, null, null, [], "public timeout"u8);
		Assert.Equal("provider_unknown", SupplierRules.Validate(binding, unknown));
		using var failed = new SupplierOutcome(SupplierOutcomeKind.ConfirmedNoEffect, null, null, null, [], "public rejection"u8);
		Assert.Equal("provider_failed", SupplierRules.Validate(binding, failed));
		using var falseFailure = new SupplierOutcome(SupplierOutcomeKind.ConfirmedNoEffect, null, null, null, [new("item", SupplierItemDisposition.Customer, "code", "public content"u8)], "public rejection"u8);
		Assert.Equal("identity_mismatch", SupplierRules.Validate(binding, falseFailure));
	}
	[Fact]
	public void QuantityFeesCurrencyAndInvalidBaselineCannotBypassTheOriginalCeiling()
	{
		var binding = Bound(2);
		using var fees = new SupplierOutcome(SupplierOutcomeKind.Completed, "public purchase", "USD", 22.01m, [new("first", SupplierItemDisposition.Customer, "code", "public one"u8), new("second", SupplierItemDisposition.Customer, "code", "public two"u8)], "public wire"u8);
		Assert.Equal("cost_limit", SupplierRules.Validate(binding, fees));
		using var currency = new SupplierOutcome(SupplierOutcomeKind.Completed, "public purchase", "UNKNOWN", 20, [], "public wire"u8);
		Assert.Equal("cost_unverified", SupplierRules.Validate(binding, currency));
		Assert.Throws<ArgumentException>(() => SupplierRules.UnitCeiling(binding with { UnitBaseline = 0 }));
		Assert.Throws<ArgumentException>(() => SupplierRules.UnitCeiling(binding with { Quantity = 0 }));
		Assert.Throws<ArgumentException>(() => new SupplierOutcome((SupplierOutcomeKind)99, null, null, null, [], []));
		Assert.Equal(11, SupplierRules.UnitCeiling(binding));
		Assert.Equal(12, SupplierRules.UnitCeiling(binding with { ApprovedUnitCeiling = 12 }));
		Assert.Equal(10, binding.UnitBaseline);
	}
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
	private static SupplierOutcome Outcome(int customers, int bonus) => new(SupplierOutcomeKind.Completed, "public purchase", "USD", 50,
		Enumerable.Range(0, customers + bonus).Select(i => new SupplierItem("public item " + i, i < customers ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus, "code", "public credential"u8)).ToArray(), "public wire"u8);
	private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
	}
}
