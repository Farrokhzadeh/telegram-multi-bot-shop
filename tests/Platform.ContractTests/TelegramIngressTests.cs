using System.Security.Claims;
using System.Security.Cryptography;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class TelegramIngressTests
{
    [Theory]
    [InlineData("/start")]
    [InlineData("/menu")]
    [InlineData("/shop")]
    [InlineData("/support")]
    [InlineData("/discounts")]
    [InlineData("/orders")]
    [InlineData("/orders 20")]
    public void OnlyAuthenticatedPrivateSenderCanSupplyCommandProof(string command)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        using var verifier = new TelegramWebhookVerifier(Context(), secret);
        string header = TelegramFixture.Header(secret);
        var update = verifier.Verify([header], TelegramFixture.Body(command));
        Assert.Equal(7001, update.ActorId);
        Assert.False(update.IsCallback);
        Assert.DoesNotContain(header, update.ToString(), StringComparison.Ordinal);
        Assert.Throws<TelegramIngressRejectedException>(() => verifier.Verify([], TelegramFixture.Body(command)));
        Assert.Throws<TelegramIngressRejectedException>(() => verifier.Verify([header, header], TelegramFixture.Body(command)));
        Assert.Throws<TelegramIngressRejectedException>(() => verifier.Verify([new string('x', 43)], TelegramFixture.Body(command)));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("other_actor")]
    [InlineData("bot_sender")]
    [InlineData("wrong_bot")]
    [InlineData("duplicate")]
    [InlineData("selector")]
    [InlineData("inline")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public void InvalidOrForgedCallbackEnvelopeHasNoPrivateAuthority(string mutation)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        using var verifier = new TelegramWebhookVerifier(Context(), secret);
        byte[] body = TelegramFixture.Body("v1|1|p|" + Guid.NewGuid().ToString("N"), true,
            chatId: mutation == "other_actor" ? 7002 : mutation == "group" ? -1001 : 7001,
            botId: mutation == "wrong_bot" ? 1002 : 1001, chatType: mutation == "group" ? "supergroup" : "private", senderBot: mutation == "bot_sender");
        body = mutation switch
        {
            "duplicate" => TelegramFixture.Replace(body, "\"update_id\":1", "\"update_id\":1,\"update_id\":2"),
            "selector" => TelegramFixture.Replace(body, "\"update_id\":1", "\"update_id\":1,\"brand_id\":\"foreign\""),
            "inline" => TelegramFixture.Replace(body, "\"callback_query\":{", "\"callback_query\":{\"inline_message_id\":\"public-inline\","),
            "malformed" => "{"u8.ToArray(),
            "oversized" => new byte[1048577],
            _ => body
        };
        var error = Assert.Throws<TelegramIngressRejectedException>(() => verifier.Verify([TelegramFixture.Header(secret)], body));
        Assert.Null(error.InnerException);
        Assert.Equal("Telegram update was rejected.", error.Message);
    }

    [Theory]
    [InlineData("/orders 0")]
    [InlineData("/orders 01")]
    [InlineData("/orders -20")]
    [InlineData("/orders 21")]
    [InlineData("/orders 10020")]
    [InlineData("/orders 20x")]
    public void InvalidHistoryPageIsNeverAnAuthenticatedCommand(string command)
    {
        Assert.False(CustomerHistoryCommandRouter.TryParsePage(command, out _));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using var verifier = new TelegramWebhookVerifier(Context(), key);
        Assert.Throws<TelegramIngressRejectedException>(() => verifier.Verify([TelegramFixture.Header(key)], TelegramFixture.Body(command)));
    }
    private static BrandContext Context()
    {
        var registry = new BotBrandRegistry([new("test-bot", "a", Guid.NewGuid(), 1001, Guid.NewGuid(), 1, true)]);
        Assert.True(registry.TryResolveAuthenticated("test-bot", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "a")], "test-bot")), out var context));
        return context!;
    }
}
