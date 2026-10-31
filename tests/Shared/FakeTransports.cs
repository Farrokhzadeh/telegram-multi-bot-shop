using System.Net;
using System.Text;

namespace Platform.Testing;

// The synthetic side effect commits before the injected lost response.
public sealed class FakeTelegramTransport : HttpMessageHandler
{
    public bool LoseNextReply { get; set; }
    public int VisibleMessages { get; private set; }
    public int Edits { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/sendMessage", StringComparison.Ordinal))
        {
            VisibleMessages++;
        }
        else if (path.EndsWith("/editMessageText", StringComparison.Ordinal) && VisibleMessages == 1)
        {
            Edits++;
        }
        else
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        if (LoseNextReply)
        {
            LoseNextReply = false;
            throw new HttpRequestException("Synthetic lost result after remote acceptance.");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}", Encoding.UTF8, "application/json")
        });
    }
}

public sealed class FakeSupplierTransport : HttpMessageHandler
{
    public bool LoseNextPurchaseReply { get; set; }
    public int Purchases { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Post)
        {
            Purchases++;
            if (LoseNextPurchaseReply)
            {
                LoseNextPurchaseReply = false;
                throw new HttpRequestException("Synthetic unknown purchase outcome.");
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Purchases > 0 ? "accepted-original" : "not-found")
        });
    }
}
