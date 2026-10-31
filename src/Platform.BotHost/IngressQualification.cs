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
        Console.WriteLine("PASS: native authenticated private Telegram ingress; group proof rejected; no network.");
        return 0;
    }
}
