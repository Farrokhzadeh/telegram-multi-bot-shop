using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Security;
using Platform.Telegram;

namespace Platform.BotHost;

internal static class IngressQualification
{
    internal static async Task<int> RunAsync()
    {
        var registry = new BotBrandRegistry([new("qualification-bot", "public", Guid.NewGuid(), 1001, Guid.NewGuid(), 1, true)]);
        if (!registry.TryResolveAuthenticated("qualification-bot", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "public")], "qualification-bot")), out var context)) return 1;
        byte[] secret = SHA256.HashData("public synthetic native webhook fixture"u8);
        using var verifier = new TelegramWebhookVerifier(context!, secret);
        string header = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        const string body = """{"update_id":1,"message":{"message_id":77,"from":{"id":7001,"is_bot":false},"chat":{"id":7001,"type":"private"},"text":"/start"}}""";
        using var stagingPreview = JsonDocument.Parse(body);
        using var foreignPreview = JsonDocument.Parse(body.Replace("7001", "7002", StringComparison.Ordinal));
        using var groupPreview = JsonDocument.Parse(body.Replace("\"private\"", "\"group\"", StringComparison.Ordinal));
        using var checkoutPreview = JsonDocument.Parse(body.Replace("/start", "/checkout", StringComparison.Ordinal));
        if (!StagingPreviewCommand.Accepts(stagingPreview.RootElement, 7001) ||
            StagingPreviewCommand.Accepts(foreignPreview.RootElement, 7001) ||
            StagingPreviewCommand.Accepts(groupPreview.RootElement, 7001) ||
            StagingPreviewCommand.Accepts(checkoutPreview.RootElement, 7001)) return 1;
        var update = verifier.Verify([header], System.Text.Encoding.UTF8.GetBytes(body));
        if (update.ActorId != 7001 || update.Context.BotId != 1001 || update.IsCallback) return 1;
        bool rejected = false;
        try { verifier.Verify([header], System.Text.Encoding.UTF8.GetBytes(body.Replace("\"private\"", "\"supergroup\"", StringComparison.Ordinal))); }
        catch (TelegramIngressRejectedException) { rejected = true; }
        if (!rejected) return 1;
        var historical = new CustomerHistoricalPage([new("ORD-ABC123ABC123", new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc), 2, 600000m, "IRR", null, null, true)], null);
        var outgoing = CustomerHistoryMessageRenderer.Render(new UpdateRouteResult(UpdateRouteCode.History, 0, History: historical));
        if (outgoing.Count != 1 || outgoing[0].ParseMode is not null || outgoing[0].HasActionButtons ||
            !outgoing[0].Text.Contains("600000 IRR", StringComparison.Ordinal) ||
            !outgoing[0].Text.Contains("Payment status: Unknown", StringComparison.Ordinal) ||
            !outgoing[0].Text.Contains("Delivery status: Unknown", StringComparison.Ordinal)) return 1;
        var originals = Enumerable.Range(0, 55).Select(i => new CustomerHistoricalOrder(
            "STAGE-SYNTH-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(i), 2, 600000m, "IRR", null, null, true)).ToArray();
        var verifiedTyped = originals.Take(10).Select(o => o with { SavedTitle = "Verified synthetic product" }).ToArray();
        var first = CustomerHistoricalOrderDeduplicator.Page(originals, verifiedTyped, 0);
        var middle = CustomerHistoricalOrderDeduplicator.Page(originals, verifiedTyped, 20);
        var last = CustomerHistoricalOrderDeduplicator.Page(originals, verifiedTyped, 40);
        var unique = first.Orders.Concat(middle.Orders).Concat(last.Orders).Select(o => o.OriginalReference).ToArray();
        if (first.Orders.Count != 20 || first.NextOffset != 20 ||
            middle.Orders.Count != 20 || middle.NextOffset != 40 ||
            last.Orders.Count != 15 || last.NextOffset is not null ||
            unique.Distinct(StringComparer.Ordinal).Count() != 55) return 1;
        if (!await QualifyTelegramTransportAsync()) return 1;
        Console.WriteLine("PASS: native authenticated private Telegram ingress and offline read-only history rendering; no network.");
        return 0;
    }

    private static async Task<bool> QualifyTelegramTransportAsync()
    {
        using var fake = new QualificationTelegramTransport();
        using var http = new HttpClient(fake);
        const string token = "12345:" + "public-synthetic-qualification-only";
        using var request = new StringReader(token + "\n" + """
            {"method":"getUpdates","parameters":{"offset":41,"timeout":0,"allowed_updates":["message"]}}
            {"method":"sendMessage","parameters":{"chat_id":7001,"text":"Synthetic acknowledgement","protect_content":true,"reply_markup":{"inline_keyboard":[[{"text":"Orders","callback_data":"orders"}]]}}}
            """);
        using var response = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        if (await StagingTelegramPipe.RunAsync(request, response, http) != 0) return false;
        using var lines = new StringReader(response.ToString());
        using var updates = JsonDocument.Parse(lines.ReadLine()!);
        using var sent = JsonDocument.Parse(lines.ReadLine()!);
        return updates.RootElement.GetProperty("result")[0].GetProperty("future_field").GetBoolean() &&
            sent.RootElement.GetProperty("result").GetProperty("message_id").GetInt32() == 99 &&
            fake.Polls == 1 && fake.Sends == 1;
    }

    private sealed class QualificationTelegramTransport : HttpMessageHandler
    {
        public int Polls { get; private set; }
        public int Sends { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var data = payload.RootElement;
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/getUpdates", StringComparison.Ordinal) &&
                data.GetProperty("offset").GetInt32() == 41)
            {
                Polls++;
                body = """{"ok":true,"result":[{"update_id":41,"future_field":true}]}""";
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal) &&
                data.GetProperty("chat_id").GetInt64() == 7001 && data.GetProperty("protect_content").GetBoolean() &&
                data.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString() == "orders")
            {
                Sends++;
                body = """{"ok":true,"result":{"message_id":99,"chat":{"id":7001,"type":"private"}}}""";
            }
            else throw new InvalidOperationException();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
