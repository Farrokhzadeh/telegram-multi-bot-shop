using Platform.Core;

namespace Platform.ContractTests;

public sealed class SupplierContractTests
{
    private static SupplierBinding Bound(int quantity = 5) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "synthetic", "public account", "public product", "public variant", "public intent", quantity, 10, "USD", null);
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
    private static SupplierOutcome Outcome(int customers, int bonus) => new(SupplierOutcomeKind.Completed, "public purchase", "USD", 50,
        Enumerable.Range(0, customers + bonus).Select(i => new SupplierItem("public item " + i, i < customers ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus, "code", "public credential"u8)).ToArray(), "public wire"u8);
}
