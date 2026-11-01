using System.Numerics;

namespace Platform.Core;

public enum CheckoutKind { ProductPurchase, PrepaidUnits }
public enum CryptoAsset { UsdtTrc20, UsdtBsc }
public enum CheckoutPayment { Wallet, Crypto, Bank, NoPaymentRequired }
public sealed class DiscountQuantityException(int maximum) : Exception("quantity_exceeds_discount_limit")
{
    public int Maximum { get; } = maximum;
}
public sealed record ProductPresentation(string Brand, string Title, string? Description = null, string? Icon = null, string? Guidance = null);
public sealed record QuotePrices(decimal UnitUsdt, decimal? UnitToman, bool AutoToman, bool FxEnabled, FxRate? Rate, TimeSpan MaximumFxAge);
public sealed record QuoteCoupon(Guid Id, Guid RevisionId, string Code, decimal Percent, BigInteger? Remaining)
{
    public override string ToString() => "QuoteCoupon [redacted]";
}
public sealed record QuoteTerms(int Quantity, decimal UnitUsdt, decimal? UnitToman, bool AutoToman, FxRate? Rate, decimal RoundingStep,
    string Version, CheckoutPayment Payment, Currency Currency, ProductPresentation Product, Guid? DiscountId, Guid? DiscountRevisionId, decimal? DiscountPercent, int? DiscountUnits);

public sealed class CheckoutQuote
{
    private CheckoutQuote(QuoteTerms terms, CheckoutPayment requested) { Terms = terms; RequestedPayment = requested; }
    public QuoteTerms Terms { get; }
    public CheckoutPayment RequestedPayment { get; }
    public Money TotalUsdt => PricingPolicy.DiscountTotal(Terms.UnitUsdt, Terms.Quantity, Terms.DiscountPercent ?? 0, Currency.Usdt);
    public Money TotalUsd => new(TotalUsdt.Amount, Currency.Usd);
    public Money? TotalToman => Terms.AutoToman ? Terms.Rate is null ? null : PricingPolicy.TomanTotal(Terms.UnitUsdt, Terms.UnitToman, Terms.Quantity, Terms.DiscountPercent ?? 0, true, Terms.Rate) :
        Terms.UnitToman is null ? null : PricingPolicy.TomanTotal(Terms.UnitUsdt, Terms.UnitToman, Terms.Quantity, Terms.DiscountPercent ?? 0, false, null);
    public Money Payable => Terms.Currency == Currency.Toman ? TotalToman ?? throw new PricingRejectedException() : Terms.Payment == CheckoutPayment.Wallet ? TotalUsd : TotalUsdt;
    public override string ToString() => "CheckoutQuote [redacted]";

    public static CheckoutQuote Create(QuotePrices prices, ProductPresentation product, int quantity, CheckoutPayment requested, IReadOnlyList<QuoteCoupon> coupons, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(coupons);
        if (quantity <= 0 || prices.UnitUsdt <= 0 || prices.UnitToman <= 0 || now.Kind != DateTimeKind.Utc || requested is not (CheckoutPayment.Wallet or CheckoutPayment.Crypto or CheckoutPayment.Bank) ||
            string.IsNullOrWhiteSpace(product.Brand) || string.IsNullOrWhiteSpace(product.Title)) throw new PricingRejectedException();
        bool automatic = prices.AutoToman && prices.FxEnabled;
        FxRate? rate = null;
        if (automatic)
        {
            try { PricingPolicy.RequireFresh(prices.Rate, now, prices.MaximumFxAge); rate = prices.Rate; }
            catch (PricingRejectedException) when (requested != CheckoutPayment.Bank) { }
        }
        decimal? unitToman = automatic ? rate is null ? null : PricingPolicy.CeilingToman(ExactDecimal.Multiply(prices.UnitUsdt, rate.Toman)) : prices.UnitToman;
        if (requested == CheckoutPayment.Bank && unitToman is null) throw new PricingRejectedException();
        Money usd = PricingPolicy.DiscountTotal(prices.UnitUsdt, quantity, 0, Currency.Usdt);
        Money? toman = unitToman is null ? null : PricingPolicy.TomanTotal(prices.UnitUsdt, unitToman, quantity, 0, automatic, rate);
        QuoteCoupon? chosen = null;
        foreach (QuoteCoupon coupon in coupons.OrderBy(c => c.Code, StringComparer.Ordinal).ThenBy(c => c.Id))
        {
            if (coupon.Id == Guid.Empty || coupon.RevisionId == Guid.Empty || coupon.Remaining < 0 || string.IsNullOrEmpty(coupon.Code)) throw new PricingRejectedException();
            Money candidateUsd = PricingPolicy.DiscountTotal(prices.UnitUsdt, quantity, coupon.Percent, Currency.Usdt);
            Money? candidateToman = unitToman is null ? null : PricingPolicy.TomanTotal(prices.UnitUsdt, unitToman, quantity, coupon.Percent, automatic, rate);
            if (candidateUsd.Amount < usd.Amount || (candidateUsd.Amount == usd.Amount && (candidateToman?.Amount ?? 0) < (toman?.Amount ?? 0)))
            { chosen = coupon; usd = candidateUsd; toman = candidateToman; }
        }
        if (chosen?.Remaining is BigInteger capacity && capacity < quantity) throw new DiscountQuantityException((int)BigInteger.Min(int.MaxValue, capacity));
        CheckoutPayment method = chosen?.Percent == 100 ? CheckoutPayment.NoPaymentRequired : requested;
        return new(new(quantity, prices.UnitUsdt, unitToman, automatic, rate, PricingPolicy.TomanStep, PricingPolicy.Version, method,
            method == CheckoutPayment.Bank ? Currency.Toman : Currency.Usdt, product, chosen?.Id, chosen?.RevisionId, chosen?.Percent, chosen is null ? null : quantity), requested);
    }
}
