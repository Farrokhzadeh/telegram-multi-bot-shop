using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Security;
using Platform.Testing;

namespace Platform.ContractTests;

public sealed class AdminApiSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task FrozenAdministratorSurfaceHasExactRoutesSchemasAndBearerProtection()
    {
        await ApiContractQualification.RunAsync();
        await using var app = WebApplication.CreateSlimBuilder().Build();
        ApiContractQualification.Map(app);
        var document = OpenApiContract.Generate(app);
        Assert.Equal(29, document.Paths.Sum(p => p.Value.Count));
        Assert.Equal("3.0.3", document.Openapi);
        Assert.True(document.Components.Schemas["BotTokenChange"].Properties!["Token"].WriteOnly);
        Assert.Contains("CommandId", document.Components.Schemas["BotTokenChange"].Required!);
        Assert.Contains("ExpectedRevision", document.Components.Schemas["BotTokenChange"].Required!);
        Assert.Equal(new[] { "Id", "BotId", "BotUsername", "HasBotToken", "Revision", "UpdatedAt" }, document.Components.Schemas["BrandConfiguration"].Properties!.Keys);
        Assert.Equal(new[] { "Items", "Next" }, document.Components.Schemas["AdminPageAdminOrder"].Properties!.Keys);
        Assert.Equal(new[] { "code", "requestId" }, document.Components.Schemas["ApiFailure"].Properties!.Keys);
        foreach (var route in document.Paths.SelectMany(p => p.Value.Values))
        { Assert.True(route.Responses.ContainsKey("409")); Assert.True(route.Responses.ContainsKey("400")); Assert.True(route.Responses.ContainsKey("401")); }
        string serialized = JsonSerializer.Serialize(document, ContractJsonContext.Default.ApiDocument);
        Assert.DoesNotContain("Ciphertext", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password_verifier", serialized, StringComparison.Ordinal);
    }
    [Fact]
    public async Task EveryRegisteredAdministratorRouteRejectsUnauthenticatedCallsBeforeBindingOrBusinessEffects()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)((_, _) => Task.FromResult<AuthenticatedAdmin?>(null)));
        ApiContractQualification.Map(app);
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        foreach (var route in OpenApiContract.Routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path.Replace("{brand}", Guid.Empty.ToString("D"), StringComparison.Ordinal).Replace("{id}", Guid.Empty.ToString("D"), StringComparison.Ordinal));
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        await app.StopAsync(Ct);
    }
}
