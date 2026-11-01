using System.Security.Cryptography;
using System.Text;
using Platform.Core;

namespace Platform.Testing;

public static class PaymentNoticeFixture
{
    public const string Secret = "public-synthetic-ipn-secret-00001";
    public static NowPaymentsIpnVerifier Verifier() => new("synthetic-v1", Encoding.UTF8.GetBytes(Secret));
    public static string Body(string origin = "public-synthetic-origin", string status = "finished", decimal actual = 10.4m, string asset = "usdttrc20",
        decimal quote = 10.25m, string provider = "123456789", decimal expected = 10.4m) =>
        "{\"actually_paid\":" + ExactDecimal.Format(actual) + ",\"order_id\":\"" + origin + "\",\"pay_amount\":" + ExactDecimal.Format(expected) + ",\"pay_currency\":\"" + asset +
        "\",\"payment_id\":" + provider + ",\"payment_status\":\"" + status + "\",\"price_amount\":" + ExactDecimal.Format(quote) + ",\"price_currency\":\"usd\"}";
    public static string Sign(string canonical) => Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(canonical)));
    public static VerifiedPaymentNotice Notice(string origin = "public-synthetic-origin", string status = "finished", decimal actual = 10.4m, string asset = "usdttrc20",
        decimal quote = 10.25m, string provider = "123456789", decimal expected = 10.4m)
    {
        string body = Body(origin, status, actual, asset, quote, provider, expected);
        using var verifier = Verifier();
        return verifier.Verify([Sign(body)], Encoding.UTF8.GetBytes(body));
    }
}
