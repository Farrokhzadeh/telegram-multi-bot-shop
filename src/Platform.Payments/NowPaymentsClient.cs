using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Core;

namespace Platform.Payments;

public sealed record HostedPaymentInvoice(string ProviderInvoice, Uri CheckoutUrl)
{
    public override string ToString() => "HostedPaymentInvoice [redacted]";
}

public sealed class NowPaymentsClient : IDisposable
{
    private readonly HttpClient client;
    private readonly string apiKey;
    public NowPaymentsClient(string protectedApiKey, HttpMessageHandler? transport = null)
    {
        if (protectedApiKey.Length is < 16 or > 256 || protectedApiKey.Any(c => c <= 32 || c > 126)) throw new ArgumentException("Invalid protected merchant configuration.");
        apiKey = protectedApiKey;
        client = new(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public Task<ProviderPaymentSnapshot> LookupAsync(InvoiceExpectation invoice, CancellationToken ct = default)
    {
        if (invoice.ProviderPayment.Length is < 1 or > 64 || !invoice.ProviderPayment.All(char.IsAsciiDigit)) throw new PaymentNoticeRejectedException();
        return SendAsync(new(HttpMethod.Get, "https://api.nowpayments.io/v1/payment/" + invoice.ProviderPayment), NowPaymentsIpnVerifier.ReadLookup, ct);
    }

    public Task<HostedPaymentInvoice> CreateHostedInvoiceAsync(string origin, decimal usdDue, CryptoAsset network, Uri callback, CancellationToken ct = default)
    {
        if (origin.Length < 7 || !Guid.TryParseExact(origin[(origin.IndexOf(':') + 1)..], "D", out Guid id) ||
            id == Guid.Empty || !(origin.StartsWith("order:", StringComparison.Ordinal) || origin.StartsWith("topup:", StringComparison.Ordinal)) ||
            usdDue <= 0 || decimal.Round(usdDue, 2) != usdDue || !callback.IsAbsoluteUri ||
            callback.Scheme != Uri.UriSchemeHttps || callback.IsLoopback || callback.UserInfo.Length != 0 || callback.Fragment.Length != 0)
            throw new PaymentNoticeRejectedException();
        string asset = network switch
        {
            CryptoAsset.UsdtTrc20 => "usdttrc20",
            CryptoAsset.UsdtBsc => "usdtbsc",
            _ => throw new PaymentNoticeRejectedException()
        };
        using var payload = new MemoryStream();
        using (var json = new Utf8JsonWriter(payload))
        {
            json.WriteStartObject();
            json.WriteNumber("price_amount", usdDue);
            json.WriteString("price_currency", "usd");
            json.WriteString("pay_currency", asset);
            json.WriteString("order_id", origin);
            json.WriteString("ipn_callback_url", callback.AbsoluteUri);
            json.WriteEndObject();
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nowpayments.io/v1/invoice") { Content = new ByteArrayContent(payload.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return SendAsync(request, body => ReadInvoice(body, origin, usdDue, asset), ct);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, Func<ReadOnlyMemory<byte>, T> readBody, CancellationToken ct)
    {
        using var owned = request;
        request.Headers.Add("x-api-key", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536) throw new PaymentNoticeRejectedException();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[65537];
            try
            {
                int length = 0, read;
                while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token)) != 0) length += read;
                if (length > 65536) throw new PaymentNoticeRejectedException();
                return readBody(bytes.AsMemory(0, length));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { throw new PaymentNoticeRejectedException(); }
    }
    private static HostedPaymentInvoice ReadInvoice(ReadOnlyMemory<byte> bytes, string origin, decimal usdDue, string asset)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new PaymentNoticeRejectedException();
            var identity = root.GetProperty("id");
            string? id = identity.ValueKind == JsonValueKind.String ? identity.GetString() : identity.GetRawText();
            var quote = root.GetProperty("price_amount");
            string? exact = quote.ValueKind == JsonValueKind.String ? quote.GetString() : quote.GetRawText();
            if (id is null || id.Length is < 1 or > 64 || !id.All(char.IsAsciiDigit) ||
                root.GetProperty("order_id").GetString() != origin ||
                exact is null || ExactDecimal.Parse(exact) != usdDue ||
                !string.Equals(root.GetProperty("price_currency").GetString(), "usd", StringComparison.OrdinalIgnoreCase) ||
                (root.TryGetProperty("pay_currency", out var echoed) && echoed.ValueKind != JsonValueKind.Null &&
                 !string.Equals(echoed.GetString(), asset, StringComparison.OrdinalIgnoreCase)) ||
                !Uri.TryCreate(root.GetProperty("invoice_url").GetString(), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.IdnHost != "nowpayments.io" || url.UserInfo.Length != 0 ||
                url.Fragment.Length != 0 || url.AbsolutePath is not ("/payment" or "/payment/") ||
                url.Query.TrimStart('?').Split('&').Count(part => part.StartsWith("iid=", StringComparison.Ordinal)) != 1 ||
                !url.Query.TrimStart('?').Split('&').Contains("iid=" + id, StringComparer.Ordinal))
                throw new PaymentNoticeRejectedException();
            return new(id, url);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new PaymentNoticeRejectedException();
        }
    }

    public void Dispose() => client.Dispose();
    public override string ToString() => "NowPaymentsClient [redacted]";
}
