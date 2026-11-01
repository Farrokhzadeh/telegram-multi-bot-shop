using System.Numerics;
using System.Text;
using Platform.Core;

namespace Platform.UnitTests;

public sealed class PricingPolicyTests
{
    [Theory]
    [InlineData("1234567", "123456.7")]
    [InlineData("10000", "1000")]
    [InlineData("1000000000", "100000000")]
    [InlineData("1e6", "100000")]
    [InlineData("+1234567.0", "123456.7")]
    public void NobitexRialsAreConvertedToExactToman(string latest, string expected) =>
        Assert.Equal(ExactDecimal.Parse(expected), PricingPolicy.ParseNobitex(Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":\"" + latest + "\"}}}")));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":true,\"latest\":10000}}}")]
    [InlineData("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":true}}}")]
    [InlineData("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":9999}}}")]
    [InlineData("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":\"NaN\"}}}")]
    [InlineData("{\"status\":\"ok\",\"stats\":{\"usdt-rls\":{\"isClosed\":false,\"latest\":10000,\"latest\":20000}}}")]
    public void InvalidMarketEvidenceFailsWithoutRawResponseDetails(string body)
    {
        var error = Assert.Throws<PricingRejectedException>(() => PricingPolicy.ParseNobitex(Encoding.UTF8.GetBytes(body)));
        Assert.Null(error.InnerException);
        Assert.Equal("Pricing is unavailable or invalid.", error.Message);
    }

    [Theory]
    [InlineData("10", 2, "10", "18")]
    [InlineData("0.05", 1, "10", "0.04")]
    [InlineData("0.15", 1, "10", "0.14")]
    [InlineData("0.01", 1, "50", "0")]
    [InlineData("10", 10, "100", "0")]
    public void SourceDiscountCentTiesUseHalfEven(string unit, int quantity, string percent, string expected) =>
        Assert.Equal(ExactDecimal.Parse(expected), PricingPolicy.DiscountTotal(ExactDecimal.Parse(unit), quantity, ExactDecimal.Parse(percent), Currency.Usdt).Amount);

    [Fact]
    public void AutomaticTomanRoundsTheDiscountedWholeTotalAndManualOptOutUsesItsOwnPrice()
    {
        var rate = new FxRate(123456.7m, PricingPolicy.FxSource, DateTime.UtcNow);
        Assert.Equal(1525000m, PricingPolicy.TomanTotal(12.34m, null, 1, 0, true, rate).Amount);
        Assert.Equal(1375000m, PricingPolicy.TomanTotal(12.34m, null, 1, 10, true, rate).Amount);
        Assert.Equal(11110.50m, PricingPolicy.TomanTotal(12.34m, 12345m, 1, 10, false, null).Amount);
        Assert.Equal(0m, PricingPolicy.TomanTotal(12.34m, null, 1, 100, true, rate).Amount);
    }

    [Fact]
    public void FxEqualityIsFreshAndFutureMissingOrExpiredRatesAreRejected()
    {
        DateTime now = DateTime.UtcNow;
        var rate = new FxRate(123456.7m, PricingPolicy.FxSource, now.AddHours(-1));
        PricingPolicy.RequireFresh(rate, now, TimeSpan.FromHours(1));
        Assert.Throws<PricingRejectedException>(() => PricingPolicy.RequireFresh(rate with { FetchedAt = rate.FetchedAt.AddTicks(-1) }, now, TimeSpan.FromHours(1)));
        Assert.Throws<PricingRejectedException>(() => PricingPolicy.RequireFresh(rate with { FetchedAt = now.AddTicks(1) }, now, TimeSpan.FromHours(1)));
        Assert.Throws<PricingRejectedException>(() => PricingPolicy.RequireFresh(null, now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void CapacityUsesSignedOwnerAdjustmentAndHoldsWithoutOverflowOrHistoryRewriting()
    {
        Assert.Equal<BigInteger?>(new BigInteger(3), PricingPolicy.Remaining(4, 2, -5, 1));
        Assert.Equal<BigInteger?>(BigInteger.Zero, PricingPolicy.Remaining(1, 4, 3, 2));
        Assert.Equal<BigInteger?>(BigInteger.Zero, PricingPolicy.Remaining(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue));
        Assert.Equal<BigInteger?>(new BigInteger(long.MaxValue - 1), PricingPolicy.Remaining(long.MaxValue, 0, long.MinValue, 1));
    }
}
