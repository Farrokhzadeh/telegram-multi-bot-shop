using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Platform.Api;
using Platform.Security;

namespace Platform.ContractTests;

public sealed class AdminGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Bot public-claim")]
    [InlineData("Bearer private-payload")]
    public async Task CustomerClaimsCookiesAndUnverifiedHeadersCannotGrantAdministratorAccess(string? authorization)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(AdministratorEndpointMetadata.Instance), "private-admin"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "bot"));
        context.Request.Headers["X-Admin"] = "true";
        context.Request.Headers.Cookie = "admin_session=private-payload";
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        bool called = false;
        await new AdminGuardMiddleware(_ => { called = true; return Task.CompletedTask; }, (_, _) => throw new InvalidOperationException("Authenticator must not run.")).InvokeAsync(context);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(called);
        Assert.Throws<InvalidOperationException>(() => context.GetRequiredAdministrator());
    }

    [Fact]
    public async Task CanonicalBearerStillRequiresActualServerAuthenticationAndDuplicatesFailClosed()
    {
        using var token = AdminToken.Create();
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(AdministratorEndpointMetadata.Instance), "private-admin"));
        string bearer = "Bearer " + token.ExportForTransport();
        context.Request.Headers.Authorization = bearer;
        int checks = 0;
        var middleware = new AdminGuardMiddleware(_ => throw new InvalidOperationException("Handler must not run."), (_, _) =>
        {
            checks++;
            return Task.FromResult<AuthenticatedAdmin?>(null);
        });
        await middleware.InvokeAsync(context);
        Assert.Equal(1, checks);
        Assert.Equal(401, context.Response.StatusCode);
        context.Request.Headers.Authorization = new StringValues([bearer, bearer]);
        await middleware.InvokeAsync(context);
        Assert.Equal(1, checks);
    }
}
