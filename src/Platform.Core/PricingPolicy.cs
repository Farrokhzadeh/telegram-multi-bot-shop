using System.Numerics;
using System.Text.Json;

namespace Platform.Core;

public sealed class PricingRejectedException : Exception
{
    public PricingRejectedException() : base("Pricing is unavailable or invalid.") { }
}
public sealed record FxRate(decimal Toman, string Source, DateTime FetchedAt);

public static class PricingPolicy
{
    public const string Version = "legacy-cent-even-toman5000-v1";
    public const string FxSource = "nobitex-usdt-rls";
    public const decimal TomanStep = 5000;

    public static decimal ParseNobitex(ReadOnlyMemory<byte> body)
    {
        if (body.Length is < 2 or > 2097152) throw new PricingRejectedException();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            Unique(document.RootElement);
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "ok") throw new PricingRejectedException();
            var market = root.GetProperty("stats").GetProperty("usdt-rls");
            if (market.GetProperty("isClosed").ValueKind != JsonValueKind.False) throw new PricingRejectedException();
            JsonElement latest = market.GetProperty("latest");
            string text = latest.ValueKind switch { JsonValueKind.String => latest.GetString()!, JsonValueKind.Number => latest.GetRawText(), _ => throw new PricingRejectedException() };
            decimal rials = ParseRate(text);
            if (rials is < 10000 or > 1000000000) throw new PricingRejectedException();
            return ExactDecimal.Divide(rials, 10);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { throw new PricingRejectedException(); }
    }

    public static Money DiscountTotal(decimal unitPrice, int quantity, decimal percent, Currency currency)
    {
        if (unitPrice < 0 || quantity <= 0 || percent is < 0 or > 100 || decimal.Round(unitPrice, 2) != unitPrice || decimal.Round(percent, 2) != percent) throw new PricingRejectedException();
        decimal total = ExactDecimal.Multiply(unitPrice, quantity);
        decimal discounted = ExactDecimal.Divide(ExactDecimal.Multiply(total, ExactDecimal.Subtract(100, percent)), 100);
        return new Money(decimal.Round(discounted, 2, MidpointRounding.ToEven), currency);
    }

    public static decimal CeilingToman(decimal amount)
    {
        if (amount < 0) throw new PricingRejectedException();
        return ExactDecimal.Multiply(decimal.Ceiling(ExactDecimal.Divide(amount, TomanStep)), TomanStep);
    }

    public static void RequireFresh(FxRate? rate, DateTime now, TimeSpan maximumAge)
    {
        if (rate is null || rate.Toman <= 0 || rate.Source != FxSource || now.Kind != DateTimeKind.Utc || rate.FetchedAt.Kind != DateTimeKind.Utc ||
            maximumAge <= TimeSpan.Zero || rate.FetchedAt > now || now - rate.FetchedAt > maximumAge) throw new PricingRejectedException();
    }

    public static Money TomanTotal(decimal unitUsdt, decimal? manualUnitToman, int quantity, decimal percent, bool automatic, FxRate? rate)
    {
        if (automatic)
        {
            if (rate is null || rate.Toman <= 0 || rate.Source != FxSource) throw new PricingRejectedException();
            return new(CeilingToman(ExactDecimal.Multiply(DiscountTotal(unitUsdt, quantity, percent, Currency.Usdt).Amount, rate.Toman)), Currency.Toman);
        }
        if (manualUnitToman is not decimal manual) throw new PricingRejectedException();
        return DiscountTotal(manual, quantity, percent, Currency.Toman);
    }

    public static BigInteger? Remaining(long? limit, long redeemed, long adjustment, BigInteger held)
    {
        if (limit < 0 || redeemed < 0 || held < 0) throw new PricingRejectedException();
        BigInteger used = BigInteger.Max(0, (BigInteger)redeemed + adjustment);
        return limit is long cap ? BigInteger.Max(0, (BigInteger)cap - used - held) : null;
    }

    private static decimal ParseRate(string text)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 128) throw new PricingRejectedException();
        bool negative = text[0] == '-';
        if (text[0] is '+' or '-') text = text[1..];
        int exponent = 0, e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            if (!int.TryParse(text[(e + 1)..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out exponent) || exponent is < -128 or > 128) throw new PricingRejectedException();
            text = text[..e];
        }
        int dot = text.IndexOf('.');
        if (dot != text.LastIndexOf('.')) throw new PricingRejectedException();
        int scale = (dot < 0 ? 0 : text.Length - dot - 1) - exponent;
        string digits = text.Replace(".", "", StringComparison.Ordinal);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) throw new PricingRejectedException();
        string expanded = scale <= 0 ? digits + new string('0', -scale) : scale >= digits.Length ? "0." + new string('0', scale - digits.Length) + digits : digits.Insert(digits.Length - scale, ".");
        return ExactDecimal.Parse((negative ? "-" : "") + expanded);
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PricingRejectedException();
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement child in value.EnumerateArray()) Unique(child);
    }
}
