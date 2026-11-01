using System.Security.Cryptography;
using Platform.Core;

namespace Platform.Payments;

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
    public async Task<ProviderPaymentSnapshot> LookupAsync(InvoiceExpectation invoice, CancellationToken ct = default)
    {
        if (invoice.ProviderPayment.Length is < 1 or > 64 || !invoice.ProviderPayment.All(char.IsAsciiDigit)) throw new PaymentNoticeRejectedException();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nowpayments.io/v1/payment/" + invoice.ProviderPayment);
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
                return NowPaymentsIpnVerifier.ReadLookup(bytes.AsMemory(0, length));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { throw new PaymentNoticeRejectedException(); }
    }
    public void Dispose() => client.Dispose();
    public override string ToString() => "NowPaymentsClient [redacted]";
}
