using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Platform.Api;
using Platform.Security;

namespace Platform.ContractTests;

public sealed class TelegramContextTests
{
    [Theory]
    [InlineData("brand_id")]
    [InlineData("botId")]
    [InlineData("tenant-id")]
    [InlineData("deployment_id")]
    public async Task ForgedCallbackEnvelopeCannotOverrideAuthenticatedBot(string selector)
    {
        var brand = Guid.NewGuid();
        var registry = new BotBrandRegistry([new("test-bot", "public-bot", brand, 1001, Guid.NewGuid(), 1, true)]);
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(CustomerBrandEndpointMetadata.Instance), "public-callback"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "public-bot")], "test-bot"));
        context.Features.Set<IAuthenticateResultFeature>(new TicketFeature { AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, "test-bot")) });
        var body = Encoding.UTF8.GetBytes("{\"callback_query\":{\"from\":{\"id\":7001},\"" + selector + "\":\"foreign\"}}");
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        bool routed = false;
        await new BrandContextMiddleware(_ => { routed = true; return Task.CompletedTask; }, registry).InvokeAsync(context);
        Assert.False(routed);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Throws<InvalidOperationException>(() => context.GetRequiredBrandContext());
    }

    private sealed class TicketFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }
}
