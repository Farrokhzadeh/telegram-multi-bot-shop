using System.Text;
using Platform.Core;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class PaymentCallbackTests
{
    private static readonly InvoiceExpectation Expected = new("123456789", "public-synthetic-origin", 10.25m, "usdttrc20", 10.4m);

    [Fact]
    public void IndependentNodeVectorCoversDeepSortingArraysUnicodeControlsAndIntegerPropertyOrder()
    {
        const string body = """
            {"payment_id":123456789,"order_id":"public-synthetic-origin","payment_status":"finished","price_currency":"usd","price_amount":10.25,"pay_currency":"usdttrc20","pay_amount":10.4,"actually_paid":10.4,"fee":{"withdrawalFee":0,"depositFee":0.15,"currency":"usdt"},"extra":["سلام",{"10":"ten","2":"two","note":"<tag>/\u001f"}]}
            """;
        const string signature = "a951f9d8f2be3a7933c2035836c271474bbe2b67a53e7919a9a64955fb6da09b71e41475f2dad1193fb38135a75ec4c50d2b66a2301ba8433931b03b961fb94d";
        using var verifier = PaymentNoticeFixture.Verifier();
        using var notice = verifier.Verify([signature.ToUpperInvariant()], Encoding.UTF8.GetBytes(body));
        Assert.Equal(PaymentEvidenceDecision.Ready, PaymentEvidencePolicy.Evaluate(Expected, notice));
        Assert.DoesNotContain("سلام", notice.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", notice.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidMissingDuplicatedAndTamperedSignaturesNeverProduceProof()
    {
        using var verifier = PaymentNoticeFixture.Verifier();
        byte[] body = Encoding.UTF8.GetBytes(PaymentNoticeFixture.Body());
        string signature = PaymentNoticeFixture.Sign(PaymentNoticeFixture.Body());
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([], body));
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([signature, signature], body));
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([new string('0', 128)], body));
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([signature + " "], body));
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([signature], Encoding.UTF8.GetBytes(PaymentNoticeFixture.Body(actual: 999))));
    }

    [Theory]
    [InlineData("{\"payment_id\":1,\"payment_id\":2}")]
    [InlineData("{\"payment_id\":1,\"payment_\\u0069d\":2}")]
    [InlineData("{\"__proto__\":{}}")]
    [InlineData("{\"fee\":{\"x\":1,\"x\":2}}")]
    [InlineData("[]")]
    [InlineData("{\"x\":NaN}")]
    [InlineData("{\"x\":1e100}")]
    [InlineData("{\"x\":0.123456789012345678901234567891}")]
    public void AmbiguousOrUnsupportedPayloadsFailClosed(string body)
    {
        using var verifier = PaymentNoticeFixture.Verifier();
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([PaymentNoticeFixture.Sign(body)], Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void BodyAndDepthAreBoundedAndDisposedVerifiersCannotAuthenticate()
    {
        using var verifier = PaymentNoticeFixture.Verifier();
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([new string('0', 128)], new byte[65537]));
        string deep = new string('[', 20) + "0" + new string(']', 20);
        Assert.Throws<PaymentNoticeRejectedException>(() => verifier.Verify([PaymentNoticeFixture.Sign(deep)], Encoding.UTF8.GetBytes(deep)));
        verifier.Dispose();
        Assert.Throws<ObjectDisposedException>(() => verifier.Verify([new string('0', 128)], ReadOnlyMemory<byte>.Empty));
    }

    [Theory]
    [InlineData("waiting", "0", PaymentEvidenceDecision.Pending)]
    [InlineData("confirming", "10.4", PaymentEvidenceDecision.Pending)]
    [InlineData("confirmed", "10.4", PaymentEvidenceDecision.Pending)]
    [InlineData("sending", "10.4", PaymentEvidenceDecision.Pending)]
    [InlineData("finished", "10.4", PaymentEvidenceDecision.Ready)]
    [InlineData("finished", "10", PaymentEvidenceDecision.Review)]
    [InlineData("finished", "11", PaymentEvidenceDecision.Review)]
    [InlineData("partially_paid", "1", PaymentEvidenceDecision.Review)]
    [InlineData("refunded", "10.4", PaymentEvidenceDecision.Review)]
    [InlineData("provider_new_status", "10.4", PaymentEvidenceDecision.Review)]
    public void OnlyFullFinishedEvidenceIsReadyAndUnknownOrAnomalousFundsStayReviewed(string status, string actual, PaymentEvidenceDecision decision)
    {
        using var notice = PaymentNoticeFixture.Notice(status: status, actual: ExactDecimal.Parse(actual));
        Assert.Equal(decision, PaymentEvidencePolicy.Evaluate(Expected, notice));
    }

    [Theory]
    [InlineData("usdtbsc", "10.25", "123456789", "public-synthetic-origin", "10.4")]
    [InlineData("trx", "10.25", "123456789", "public-synthetic-origin", "10.4")]
    [InlineData("usdttrc20", "99", "123456789", "public-synthetic-origin", "10.4")]
    [InlineData("usdttrc20", "10.25", "987654321", "public-synthetic-origin", "10.4")]
    [InlineData("usdttrc20", "10.25", "123456789", "foreign-origin", "10.4")]
    [InlineData("usdttrc20", "10.25", "123456789", "public-synthetic-origin", "1")]
    public void AValidSignatureCannotOverrideFrozenInvoiceIdentityQuoteOrNetwork(string asset, string quote, string payment, string origin, string tokens)
    {
        using var notice = PaymentNoticeFixture.Notice(origin, asset: asset, quote: ExactDecimal.Parse(quote), provider: payment, expected: ExactDecimal.Parse(tokens));
        Assert.Equal(PaymentEvidenceDecision.Review, PaymentEvidencePolicy.Evaluate(Expected, notice));
    }

    [Fact]
    public void QuoteUsdAndActualUsdtRemainDistinctAndShortageCreditIgnoresProviderFees()
    {
        using var notice = PaymentNoticeFixture.Notice();
        Assert.Equal(10.25m, notice.QuoteAmount);
        Assert.Equal(10.4m, notice.ReceivedTokens);
        Assert.Equal(PaymentEvidenceDecision.Ready, PaymentEvidencePolicy.Evaluate(Expected, notice));
        var route = ExternalPaymentPolicy.Route(PaymentEvidenceDecision.Ready, false, "confirmation_pending", CheckoutKind.ProductPurchase, "local", false, false);
        Assert.Equal(ExternalSettlementRoute.LocalShortageCredit, route);
        Assert.Equal(new Money(5, Currency.Usd), ExternalPaymentPolicy.FrozenCredit(PricingPolicy.DiscountTotal(10, 1, 50, Currency.Usd).Amount));
    }

    [Theory]
    [InlineData(CheckoutKind.ProductPurchase, "supplier", false, ExternalSettlementRoute.Supplier)]
    [InlineData(CheckoutKind.ProductPurchase, "local", true, ExternalSettlementRoute.LocalPreorder)]
    [InlineData(CheckoutKind.PrepaidUnits, "supplier", false, ExternalSettlementRoute.Prepaid)]
    [InlineData(CheckoutKind.ProductPurchase, "manual", false, ExternalSettlementRoute.LocalGoods)]
    public void SupplierPreorderAndPrepaidRoutesNeverEnterAutomaticLocalShortageCredit(CheckoutKind kind, string fulfillment, bool preorder, ExternalSettlementRoute route)
    {
        Assert.Equal(route, ExternalPaymentPolicy.Route(PaymentEvidenceDecision.Ready, false, "awaiting_payment", kind, fulfillment, preorder, false));
        Assert.Equal(ExternalSettlementRoute.LateCredit, ExternalPaymentPolicy.Route(PaymentEvidenceDecision.Ready, false, "cancelled", kind, fulfillment, preorder, false));
        Assert.Equal(ExternalSettlementRoute.Review, ExternalPaymentPolicy.Route(PaymentEvidenceDecision.Pending, false, "awaiting_payment", kind, fulfillment, preorder, false));
    }
}
