using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Platform.Core;

public sealed class PaymentNoticeRejectedException : Exception
{
    public PaymentNoticeRejectedException() : base("Payment evidence rejected.") { }
}
public sealed class VerifiedPaymentNotice : IDisposable
{
    internal VerifiedPaymentNotice(string key, string payment, string origin, string status, string currency, decimal quote, string asset,
        decimal expected, decimal received, long? confirmations, byte[] body, byte[] rawHash, byte[] canonicalHash, byte[] signature)
    {
        KeyId = key; ProviderPayment = payment; Origin = origin; Status = status; QuoteCurrency = currency; QuoteAmount = quote; Asset = asset;
        ExpectedTokens = expected; ReceivedTokens = received; Confirmations = confirmations; this.body = body; RawHash = rawHash; CanonicalHash = canonicalHash; Signature = signature;
    }
    private readonly byte[] body;
    private bool disposed;
    public string KeyId { get; }
    public string ProviderPayment { get; }
    public string Origin { get; }
    public string Status { get; }
    public string QuoteCurrency { get; }
    public decimal QuoteAmount { get; }
    public string Asset { get; }
    public decimal ExpectedTokens { get; }
    public decimal ReceivedTokens { get; }
    public long? Confirmations { get; }
    internal ReadOnlyMemory<byte> Body { get { ObjectDisposedException.ThrowIf(disposed, this); return body; } }
    internal byte[] RawHash { get; }
    internal byte[] CanonicalHash { get; }
    internal byte[] Signature { get; }
    public void Dispose() { CryptographicOperations.ZeroMemory(body); disposed = true; }
    public override string ToString() => "VerifiedPaymentNotice [redacted]";
}

public sealed class NowPaymentsIpnVerifier : IDisposable
{
    private readonly string keyId;
    private readonly byte[] key;
    private bool disposed;
    public NowPaymentsIpnVerifier(string keyId, ReadOnlySpan<byte> secret)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > 160 || !keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            secret.Length is < 32 or > 256 || secret[0] <= 32 || secret[^1] <= 32) throw new ArgumentException("Invalid protected IPN configuration.");
        this.keyId = keyId; key = secret.ToArray();
    }
    public VerifiedPaymentNotice Verify(IReadOnlyList<string> signatures, ReadOnlyMemory<byte> body)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (signatures.Count != 1 || signatures[0].Length != 128 || !signatures[0].All(char.IsAsciiHexDigit) || body.Length is < 2 or > 65536) throw new PaymentNoticeRejectedException();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new PaymentNoticeRejectedException();
            var text = new StringBuilder();
            Canonical(document.RootElement, text);
            byte[] canonical = Encoding.UTF8.GetBytes(text.ToString()), supplied = Convert.FromHexString(signatures[0]);
            try
            {
                byte[] expected = HMACSHA512.HashData(key, canonical);
                try { if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) throw new PaymentNoticeRejectedException(); }
                finally { CryptographicOperations.ZeroMemory(expected); }
                var root = document.RootElement;
                string payment = Identifier(root.GetProperty("payment_id"));
                string origin = Label(root.GetProperty("order_id"), 160);
                string status = Label(root.GetProperty("payment_status"), 64);
                string currency = Label(root.GetProperty("price_currency"), 16);
                string asset = Label(root.GetProperty("pay_currency"), 32);
                decimal quote = Amount(root.GetProperty("price_amount")), tokens = Amount(root.GetProperty("pay_amount")), received = Amount(root.GetProperty("actually_paid"));
                if (quote <= 0 || tokens <= 0 || received < 0) throw new PaymentNoticeRejectedException();
                long? confirmations = null;
                if (root.TryGetProperty("confirmations", out var count) && count.ValueKind != JsonValueKind.Null)
                {
                    if (!count.TryGetInt64(out long n) || n < 0) throw new PaymentNoticeRejectedException();
                    confirmations = n;
                }
                return new(keyId, payment, origin, status, currency, quote, asset, tokens, received, confirmations,
                    body.ToArray(), SHA256.HashData(body.Span), SHA256.HashData(canonical), supplied.ToArray());
            }
            finally { CryptographicOperations.ZeroMemory(canonical); CryptographicOperations.ZeroMemory(supplied); }
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new PaymentNoticeRejectedException(); }
    }
    private static string Identifier(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string id = Label(value, 64);
            if (id.All(char.IsAsciiDigit)) return id;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long id) && id is > 0 and <= 9007199254740991) return id.ToString(CultureInfo.InvariantCulture);
        throw new PaymentNoticeRejectedException();
    }
    private static string Label(JsonElement value, int maximum)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw new PaymentNoticeRejectedException();
        return text;
    }
    private static decimal Amount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Number(value.GetRawText()),
        JsonValueKind.String => ExactDecimal.Parse(value.GetString()!),
        _ => throw new PaymentNoticeRejectedException()
    };
    private static decimal Number(string text)
    {
        if (text.Length > 128) throw new PaymentNoticeRejectedException();
        int e = text.IndexOfAny(['e', 'E']);
        if (e < 0) return ExactDecimal.Parse(text);
        if (!int.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int exponent) || exponent is < -100 or > 100) throw new PaymentNoticeRejectedException();
        string mantissa = text[..e];
        bool negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        int dot = mantissa.IndexOf('.');
        int scale = (dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        string expanded = scale <= 0 ? digits + new string('0', -scale) : scale >= digits.Length ? "0." + new string('0', scale - digits.Length) + digits : digits.Insert(digits.Length - scale, ".");
        return ExactDecimal.Parse((negative ? "-" : "") + expanded);
    }
    private static string JsonNumber(decimal number)
    {
        if (number == 0) return "0";
        string sign = number < 0 ? "-" : "", plain = ExactDecimal.Format(decimal.Abs(number));
        if (decimal.Abs(number) is >= 0.000001m and < 1000000000000000000000m) return sign + plain;
        int dot = plain.IndexOf('.');
        int exponent = number is > -1 and < 1 ? -1 - plain[2..].TakeWhile(c => c == '0').Count() : (dot < 0 ? plain.Length : dot) - 1;
        string digits = plain.Replace(".", "", StringComparison.Ordinal).TrimStart('0').TrimEnd('0');
        return sign + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
    private static uint? Index(string text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) && n != uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == text ? n : null;
    private static void Canonical(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Length > 256 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                    properties.Any(p => p.Name.Length > 160 || p.Name is "__proto__" or "constructor" or "prototype")) throw new PaymentNoticeRejectedException();
                text.Append('{');
                bool comma = false;
                foreach (var property in properties.OrderBy(p => Index(p.Name) is null ? 1 : 0).ThenBy(p => Index(p.Name) ?? 0).ThenBy(p => p.Name, StringComparer.Ordinal))
                { if (comma) text.Append(','); comma = true; String(text, property.Name); text.Append(':'); Canonical(property.Value, text); }
                text.Append('}'); break;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > 256) throw new PaymentNoticeRejectedException();
                text.Append('['); bool separator = false;
                foreach (var item in value.EnumerateArray()) { if (separator) text.Append(','); separator = true; Canonical(item, text); }
                text.Append(']'); break;
            case JsonValueKind.String: String(text, value.GetString()!); break;
            case JsonValueKind.Number: text.Append(JsonNumber(Number(value.GetRawText()))); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Null: text.Append("null"); break;
            default: throw new PaymentNoticeRejectedException();
        }
    }
    private static void String(StringBuilder text, string value)
    {
        if (value.Length > 16384) throw new PaymentNoticeRejectedException();
        text.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c))
                    { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new PaymentNoticeRejectedException(); text.Append(c).Append(value[i]); }
                    else if (char.IsLowSurrogate(c)) throw new PaymentNoticeRejectedException();
                    else text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(key); disposed = true; }
}

public enum PaymentEvidenceDecision { Pending, Ready, Review }
public sealed record InvoiceExpectation(string ProviderPayment, string Origin, decimal UsdDue, string Asset, decimal ExpectedTokens);
public static class PaymentEvidencePolicy
{
    public static PaymentEvidenceDecision Evaluate(InvoiceExpectation expected, VerifiedPaymentNotice notice)
    {
        if (notice.ProviderPayment != expected.ProviderPayment || notice.Origin != expected.Origin || notice.QuoteCurrency != "usd" || notice.QuoteAmount != expected.UsdDue ||
            notice.Asset != expected.Asset || notice.ExpectedTokens != expected.ExpectedTokens || notice.ReceivedTokens > expected.ExpectedTokens ||
            notice.Status is not ("waiting" or "confirming" or "confirmed" or "sending" or "finished" or "partially_paid")) return PaymentEvidenceDecision.Review;
        if (notice.Status == "partially_paid" || (notice.Status == "finished" && notice.ReceivedTokens < expected.ExpectedTokens)) return PaymentEvidenceDecision.Review;
        return notice.Status == "finished" && notice.ReceivedTokens == expected.ExpectedTokens ? PaymentEvidenceDecision.Ready : PaymentEvidenceDecision.Pending;
    }
}
public enum ExternalSettlementRoute { Topup, LateCredit, LocalGoods, LocalShortageCredit, LocalPreorder, Supplier, Prepaid, AlreadyAccepted, Review }
public static class ExternalPaymentPolicy
{
    public static ExternalSettlementRoute Route(PaymentEvidenceDecision evidence, bool topup, string orderState, CheckoutKind kind, string fulfillment, bool preorder, bool completeLocalStock)
    {
        if (evidence != PaymentEvidenceDecision.Ready || !Enum.IsDefined(kind)) return ExternalSettlementRoute.Review;
        if (topup) return ExternalSettlementRoute.Topup;
        if (orderState is "paid" or "not_required") return ExternalSettlementRoute.AlreadyAccepted;
        if (orderState is "expired" or "cancelled") return ExternalSettlementRoute.LateCredit;
        if (orderState is not ("awaiting_payment" or "confirmation_pending" or "under_review")) return ExternalSettlementRoute.Review;
        if (kind == CheckoutKind.PrepaidUnits) return ExternalSettlementRoute.Prepaid;
        if (preorder) return fulfillment == "local" ? ExternalSettlementRoute.LocalPreorder : ExternalSettlementRoute.Review;
        return fulfillment switch
        {
            "supplier" => ExternalSettlementRoute.Supplier,
            "manual" => ExternalSettlementRoute.LocalGoods,
            "local" => completeLocalStock ? ExternalSettlementRoute.LocalGoods : ExternalSettlementRoute.LocalShortageCredit,
            _ => ExternalSettlementRoute.Review
        };
    }
    public static Money FrozenCredit(decimal netQuote) => netQuote > 0 ? new(netQuote, Currency.Usd) : throw new PaymentNoticeRejectedException();
}
