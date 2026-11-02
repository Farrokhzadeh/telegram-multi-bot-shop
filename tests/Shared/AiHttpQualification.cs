using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Testing;

public static class AiHttpQualification
{
    private sealed class TicketFeature : IAuthenticateResultFeature { public AuthenticateResult? AuthenticateResult { get; set; } }
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        var order = f.Request();
        AiBoundaryQualification.Require((await f.Store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), order, ct)).Outcome == CheckoutOutcome.Purchased);
        var registry = new AiScopeRegistry([
            new("public-own", f.Context(0), CheckoutFixture.Actor(0), order.OrderId, DateTime.UtcNow.AddMinutes(5)),
            new("public-foreign", f.Context(1), CheckoutFixture.Actor(1), order.OrderId, DateTime.UtcNow.AddMinutes(5)),
            new("public-expired", f.Context(0), CheckoutFixture.Actor(0), null, DateTime.UtcNow.AddMinutes(-1))]);
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting(); app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
        app.Use(async (http, next) =>
        {
            string subject = http.Request.Headers["X-Synthetic-Service"].ToString();
            http.User = AiBoundaryQualification.Principal("public-own");
            if (subject.Length > 0)
            {
                string scheme = subject == "public-bot" ? "bot-service" : AiScopeRegistry.Scheme;
                var principal = AiBoundaryQualification.Principal(subject == "public-bot" ? "public-own" : subject, scheme);
                if (subject == "public-duplicate") principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "public-own"), new Claim(ClaimTypes.NameIdentifier, "public-own")], scheme));
                http.Features.Set<IAuthenticateResultFeature>(new TicketFeature { AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(principal, scheme)) });
            }
            await next(http);
        });
        app.UseMiddleware<AiReadGuardMiddleware>(registry);
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)((_, _) => Task.FromResult<AuthenticatedAdmin?>(null)));
        app.MapAiContext(new(f.Database.Data));
        app.MapOrderAdministration(null!, null!, null!, null!, _ => null);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string before = await AiBoundaryQualification.Snapshot(f, ct);
        foreach (string? subject in new string?[] { null, "public-unknown", "public-expired", "public-bot", "public-duplicate" })
            await Check(HttpMethod.Get, "/v1/ai/context", subject, HttpStatusCode.Unauthorized);
        using (var response = await Send(HttpMethod.Get, "/v1/ai/context", "public-own"))
        {
            AiBoundaryQualification.Require(response.StatusCode == HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            AiBoundaryQualification.Require(root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "version", "brandId", "brandName", "language", "humanSupportAvailable", "publicSupportContact", "order" }.Order()));
            AiBoundaryQualification.Require(root.GetProperty("brandId").GetGuid() == f.Brands[0]);
            var item = root.GetProperty("order");
            AiBoundaryQualification.Require(item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "orderId", "productTitle", "paymentStatus", "fulfillmentStatus" }.Order()));
            AiBoundaryQualification.Require(item.GetProperty("orderId").GetGuid() == order.OrderId);
        }
        await Check(HttpMethod.Get, "/v1/ai/context", "public-foreign", HttpStatusCode.NotFound);
        await Check(HttpMethod.Get, "/v1/ai/context?orderId=" + order.OrderId, "public-own", HttpStatusCode.BadRequest);
        foreach (string header in new[] { "X-Brand-Id", "X-User-Id", "X-Telegram-Id" })
        {
            using var request = Request(HttpMethod.Get, "/v1/ai/context", "public-own"); request.Headers.Add(header, "public-spoof");
            using var response = await client.SendAsync(request, ct); AiBoundaryQualification.Require(response.StatusCode == HttpStatusCode.BadRequest);
        }
        using (var request = Request(HttpMethod.Get, "/v1/ai/context", "public-own"))
        {
            request.Content = new StringContent("public selector"); using var response = await client.SendAsync(request, ct);
            AiBoundaryQualification.Require(response.StatusCode == HttpStatusCode.BadRequest);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete }) await Check(method, "/v1/ai/context", "public-own", HttpStatusCode.MethodNotAllowed);
        await Check(HttpMethod.Post, "/v1/admin/wallet-adjustments", "public-own", HttpStatusCode.Unauthorized);
        AiBoundaryQualification.Require(registry.Revoke("public-own"));
        await Check(HttpMethod.Get, "/v1/ai/context", "public-own", HttpStatusCode.Unauthorized);
        AiBoundaryQualification.Require(before == await AiBoundaryQualification.Snapshot(f, ct));
        await app.StopAsync(ct);
        HttpRequestMessage Request(HttpMethod method, string path, string? subject)
        {
            var request = new HttpRequestMessage(method, path); request.Headers.Add("Cookie", "public-spoof-cookie=public-own");
            if (subject is not null) request.Headers.Add("X-Synthetic-Service", subject);
            return request;
        }
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? subject)
        { using var request = Request(method, path, subject); return await client.SendAsync(request, ct); }
        async Task Check(HttpMethod method, string path, string? subject, HttpStatusCode expected)
        {
            using var response = await Send(method, path, subject);
            if (response.StatusCode != expected) throw new InvalidOperationException($"AI HTTP qualification expected {(int)expected}, received {(int)response.StatusCode}.");
        }
    }
}
