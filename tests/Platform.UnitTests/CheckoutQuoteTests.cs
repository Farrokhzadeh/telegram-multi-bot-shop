using System.Numerics;
using Platform.Core;

namespace Platform.UnitTests;

public sealed class CheckoutQuoteTests
{
    private static readonly ProductPresentation Product = new("public brand", "public product", "public description");
    private static readonly QuotePrices Prices = new(10, 1000000, false, false, null, TimeSpan.FromHours(1));
    private static QuoteCoupon Coupon(decimal percent, BigInteger? remaining, string code = "PUBLIC") => new(Guid.NewGuid(), Guid.NewGuid(), code, percent, remaining);

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void ExcessQuantityIsRejectedBeforeAQuoteExistsForEveryPercentage(int percent)
    {
        foreach (CheckoutPayment payment in new[] { CheckoutPayment.Wallet, CheckoutPayment.Crypto, CheckoutPayment.Bank })
        {
            var error = Assert.Throws<DiscountQuantityException>(() => CheckoutQuote.Create(Prices, Product, 11, payment, [Coupon(percent, 10)], DateTime.UtcNow));
            Assert.Equal(10, error.Maximum);
            Assert.Equal("quantity_exceeds_discount_limit", error.Message);
        }
    }

    [Fact]
    public void MandatoryBestCodeCannotBeSkippedForAWeakerCodeOrFullPrice()
    {
        var best = Coupon(100, 10);
        var weaker = Coupon(10, 100);
        Assert.Equal(10, Assert.Throws<DiscountQuantityException>(() => CheckoutQuote.Create(Prices, Product, 11, CheckoutPayment.Wallet, [weaker, best], DateTime.UtcNow)).Maximum);
        var quote = CheckoutQuote.Create(Prices, Product, 10, CheckoutPayment.Bank, [weaker, best], DateTime.UtcNow);
        Assert.Equal(best.Id, quote.Terms.DiscountId);
        Assert.Equal(CheckoutPayment.NoPaymentRequired, quote.Terms.Payment);
        Assert.Equal(0m, quote.TotalUsd.Amount);
        Assert.Equal(0m, quote.TotalToman!.Value.Amount);
    }

    [Fact]
    public void EqualTotalsUseOrdinalCodeOrderAndNeverExposeTheCodeInTheSnapshot()
    {
        var a = Coupon(50, 10, "AAA");
        var b = Coupon(50, 10, "BBB");
        var quote = CheckoutQuote.Create(Prices, Product, 2, CheckoutPayment.Crypto, [b, a], DateTime.UtcNow);
        Assert.Equal(a.Id, quote.Terms.DiscountId);
        Assert.Equal(10m, quote.TotalUsd.Amount);
        Assert.Equal(new Money(10m, Currency.Usdt), quote.Payable);
        Assert.DoesNotContain("AAA", quote.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AAA", quote.Terms.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HeldAndOwnerOvercommittedCapacityCanReturnMaximumZero()
    {
        var remaining = PricingPolicy.Remaining(1, 4, 3, 2);
        Assert.Equal(0, Assert.Throws<DiscountQuantityException>(() => CheckoutQuote.Create(Prices, Product, 1, CheckoutPayment.Wallet, [Coupon(50, remaining)], DateTime.UtcNow)).Maximum);
    }

    [Fact]
    public void AutomaticTomanUsesDiscountedWholeUsdAndFrozenInputsRemainReadableAfterFxExpires()
    {
        DateTime now = DateTime.UtcNow;
        var prices = new QuotePrices(12.34m, 999, true, true, new(123456.7m, PricingPolicy.FxSource, now), TimeSpan.FromHours(1));
        var quote = CheckoutQuote.Create(prices, Product, 1, CheckoutPayment.Bank, [Coupon(10, 10)], now);
        Assert.Equal(11.11m, quote.TotalUsd.Amount);
        Assert.Equal(1375000m, quote.TotalToman!.Value.Amount);
        Assert.Equal(1525000m, quote.Terms.UnitToman);
        Assert.Equal(1375000m, quote.TotalToman!.Value.Amount);
        Assert.Throws<PricingRejectedException>(() => CheckoutQuote.Create(prices, Product, 1, CheckoutPayment.Bank, [], now.AddHours(2)));
    }

    [Fact]
    public void StaleAutomaticTomanDoesNotDisableIndependentUsdQuotesAndManualOptOutIsIndependent()
    {
        DateTime now = DateTime.UtcNow;
        var stale = Prices with { AutoToman = true, FxEnabled = true, Rate = new(123456.7m, PricingPolicy.FxSource, now.AddHours(-2)) };
        var usd = CheckoutQuote.Create(stale, Product, 2, CheckoutPayment.Wallet, [], now);
        Assert.Null(usd.TotalToman);
        Assert.Equal(20m, usd.TotalUsd.Amount);
        Assert.Equal(new Money(20m, Currency.Usd), usd.Payable);
        var manual = CheckoutQuote.Create(stale with { AutoToman = false, UnitToman = 12345 }, Product, 2, CheckoutPayment.Bank, [Coupon(50, 2)], now);
        Assert.Equal(12345m, manual.TotalToman!.Value.Amount);
        Assert.Equal(new Money(12345m, Currency.Toman), manual.Payable);
    }
}
