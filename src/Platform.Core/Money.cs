using System.Globalization;
using System.Numerics;

namespace Platform.Core;

public enum Currency { Usd = 1, Usdt = 2, Toman = 3 }

public static class CurrencyCodes
{
    public static Currency Parse(string code) => code switch
    {
        "USD" => Currency.Usd,
        "USDT" => Currency.Usdt,
        "TOMAN" => Currency.Toman,
        _ => throw new FormatException("Unsupported commerce currency code.")
    };

    public static string ToCode(this Currency currency) => currency switch
    {
        Currency.Usd => "USD",
        Currency.Usdt => "USDT",
        Currency.Toman => "TOMAN",
        _ => throw new ArgumentOutOfRangeException(nameof(currency))
    };
}

public readonly record struct Money
{
    private readonly decimal amount;
    private readonly Currency currency;

    public Money(decimal amount, Currency currency)
    {
        _ = currency.ToCode();
        this.amount = amount;
        this.currency = currency;
    }

    public decimal Amount { get { _ = currency.ToCode(); return amount; } }
    public Currency Currency { get { _ = currency.ToCode(); return currency; } }
    public static Money Parse(string text, Currency currency) => new(ExactDecimal.Parse(text), currency);

    public Money Add(Money other) { RequireSameCurrency(other); return new(ExactDecimal.Add(Amount, other.Amount), Currency); }
    public Money Subtract(Money other) { RequireSameCurrency(other); return new(ExactDecimal.Subtract(Amount, other.Amount), Currency); }

    public Money MultiplyQuantity(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        return new(ExactDecimal.Multiply(Amount, quantity), Currency);
    }

    public Money RequireNonNegative()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Amount);
        return this;
    }

    public Money RequirePositive()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Amount);
        return this;
    }

    public override string ToString() => ExactDecimal.Format(Amount) + " " + Currency.ToCode();

    private void RequireSameCurrency(Money other)
    {
        if (Currency != other.Currency) throw new ArgumentException("Money operands require the same currency.");
    }
}

// Decimal's normal parse/arithmetic can round. Construct only after exact integer arithmetic
// proves the result fits the 96-bit coefficient and maximum scale of System.Decimal.
public static class ExactDecimal
{
    private static readonly BigInteger MaxCoefficient = (BigInteger.One << 96) - 1;

    public static decimal Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > 128) throw new FormatException("Invalid exact decimal text.");
        int start = text[0] == '-' ? 1 : 0;
        int dot = -1;
        if (start == text.Length) throw new FormatException("Invalid exact decimal text.");
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '.' && dot < 0 && i > start && i < text.Length - 1) dot = i;
            else if (!char.IsAsciiDigit(text[i])) throw new FormatException("Invalid exact decimal text.");
        }
        string digits = text[start..].Replace(".", "", StringComparison.Ordinal);
        BigInteger coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (start == 1) coefficient = -coefficient;
        return FromParts(coefficient, dot < 0 ? 0 : text.Length - dot - 1);
    }

    public static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static decimal Add(decimal left, decimal right)
    {
        var (a, sa) = Parts(left); var (b, sb) = Parts(right);
        int scale = Math.Max(sa, sb);
        return FromParts(a * BigInteger.Pow(10, scale - sa) + b * BigInteger.Pow(10, scale - sb), scale);
    }

    public static decimal Subtract(decimal left, decimal right) => Add(left, -right);

    public static decimal Multiply(decimal left, decimal right)
    {
        var (a, sa) = Parts(left); var (b, sb) = Parts(right);
        return FromParts(a * b, sa + sb);
    }

    public static decimal Divide(decimal left, decimal right)
    {
        if (right == 0) throw new DivideByZeroException("Exact decimal divisor is zero.");
        var (a, sa) = Parts(left); var (b, sb) = Parts(right);
        BigInteger numerator = a * BigInteger.Pow(10, sb), denominator = b * BigInteger.Pow(10, sa);
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        BigInteger gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= gcd; denominator /= gcd;
        int twos = 0, fives = 0;
        while (denominator % 2 == 0) { denominator /= 2; twos++; }
        while (denominator % 5 == 0) { denominator /= 5; fives++; }
        if (denominator != 1) throw new ArithmeticException("Decimal division would require rounding.");
        int scale = Math.Max(twos, fives);
        return FromParts(numerator * BigInteger.Pow(2, scale - twos) * BigInteger.Pow(5, scale - fives), scale);
    }

    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return (bits[3] < 0 ? -coefficient : coefficient, (bits[3] >> 16) & 255);
    }

    private static decimal FromParts(BigInteger coefficient, int scale)
    {
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        BigInteger magnitude = BigInteger.Abs(coefficient);
        if (scale > 28 || magnitude > MaxCoefficient) throw new OverflowException("Value cannot be represented as an exact decimal.");
        uint low = (uint)(magnitude & uint.MaxValue), middle = (uint)((magnitude >> 32) & uint.MaxValue), high = (uint)(magnitude >> 64);
        return new decimal(unchecked((int)low), unchecked((int)middle), unchecked((int)high), coefficient.Sign < 0, (byte)scale);
    }
}
