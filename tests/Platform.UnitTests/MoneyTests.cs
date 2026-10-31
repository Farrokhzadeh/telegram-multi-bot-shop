using Platform.Core;

namespace Platform.UnitTests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1.25")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("123456789012.123456789012")]
    [InlineData("1.2300000000000000000000000000000000")]
    public void ExactDecimalTextRoundtripsWithoutRounding(string text)
    {
        decimal value = ExactDecimal.Parse(text);
        Assert.Equal(value, ExactDecimal.Parse(ExactDecimal.Format(value)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e2")]
    [InlineData("1,000")]
    [InlineData(" 1")]
    [InlineData("+1")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("۱")]
    public void NonCanonicalOrNonFiniteTextIsRejected(string text)
    {
        Assert.Throws<FormatException>(() => ExactDecimal.Parse(text));
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    public void ValuesOutsideExactDecimalCapacityFailInsteadOfRounding(string text)
    {
        Assert.Throws<OverflowException>(() => ExactDecimal.Parse(text));
    }

    [Fact]
    public void ArithmeticRejectsPrecisionLossAndKeepsCurrencyExplicit()
    {
        Money usd = Money.Parse("0.1", Currency.Usd);
        Assert.Equal(Money.Parse("0.3", Currency.Usd), usd.Add(Money.Parse("0.2", Currency.Usd)));
        Assert.Equal(Money.Parse("0.2", Currency.Usd), usd.MultiplyQuantity(2));
        Assert.Equal(Money.Parse("-0.1", Currency.Usd), usd.Subtract(Money.Parse("0.2", Currency.Usd)));
        Assert.Throws<ArgumentException>(() => usd.Add(new Money(1, Currency.Usdt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => usd.MultiplyQuantity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-1, Currency.Usd).RequireNonNegative());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(0, Currency.Usd).RequirePositive());
        Assert.Throws<OverflowException>(() => ExactDecimal.Add(decimal.MaxValue, 0.1m));
        Assert.Throws<OverflowException>(() => ExactDecimal.Multiply(ExactDecimal.Parse("0.0000000000000000000000000001"), 0.1m));
        Assert.Throws<OverflowException>(() => ExactDecimal.Multiply(decimal.MaxValue, 2));
        Assert.Equal(0.125m, ExactDecimal.Divide(1, 8));
        Assert.Throws<ArithmeticException>(() => ExactDecimal.Divide(1, 3));
        Assert.Throws<DivideByZeroException>(() => ExactDecimal.Divide(1, 0));
    }

    [Theory]
    [InlineData("USD", Currency.Usd)]
    [InlineData("USDT", Currency.Usdt)]
    [InlineData("TOMAN", Currency.Toman)]
    public void CurrencyCodesKeepWalletQuoteAndBankDenominationsDistinct(string code, Currency currency)
    {
        Assert.Equal(currency, CurrencyCodes.Parse(code));
        Assert.Equal(code, currency.ToCode());
    }

    [Fact]
    public void UnknownCurrencyAndDefaultMoneyCannotReachABoundary()
    {
        Assert.Throws<FormatException>(() => CurrencyCodes.Parse("usd"));
        Assert.Throws<FormatException>(() => CurrencyCodes.Parse("USDTTRC20"));
        Assert.Throws<FormatException>(() => CurrencyCodes.Parse("RLS"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(1, (Currency)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(Money).ToString());
    }
}
