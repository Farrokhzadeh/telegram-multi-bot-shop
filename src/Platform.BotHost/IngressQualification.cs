using System.Security.Claims;
using System.Security.Cryptography;
using Platform.Security;
using Platform.Telegram;

namespace Platform.BotHost;

internal static class IngressQualification
{
    internal static int Run()
    {
        var registry = new BotBrandRegistry([new("qualification-bot", "public", Guid.NewGuid(), 1001, Guid.NewGuid(), 1, true)]);
        if (!registry.TryResolveAuthenticated("qualification-bot", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "public")], "qualification-bot")), out var context)) return 1;
        byte[] secret = SHA256.HashData("public synthetic native webhook fixture"u8);
        using var verifier = new TelegramWebhookVerifier(context!, secret);
        string header = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        const string body = """{"update_id":1,"message":{"message_id":77,"from":{"id":7001,"is_bot":false},"chat":{"id":7001,"type":"private"},"text":"/start"}}""";
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
        Console.WriteLine("PASS: native authenticated private Telegram ingress and offline read-only history rendering; no network.");
        return 0;
    }
}
