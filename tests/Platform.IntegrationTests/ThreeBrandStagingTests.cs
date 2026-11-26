using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Api;
using Platform.Campaigns;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;
namespace Platform.IntegrationTests;

public sealed class ThreeBrandStagingTests
{
    [Fact]
    public async Task ThreeBrandPracticeRunRejectsForgedIngressAndRestrictsDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CheckoutFixture.CreateAsync(users: 6, balance: 0, stock: 0, ct: ct);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, cancellation) =>
        {
            await using var read = await fixture.Sessions.BeginReadOnlyAsync(cancellation);
            return await AdminSessionStore.AuthenticateAsync(read, token, cancellation);
        }));
        app.MapCatalogAdministration(fixture.Sessions, new(fixture.Sessions), brand => fixture.Contexts.SingleOrDefault(context => context.BrandId == brand));
        const string header = "X-Telegram-Bot-Api-Secret-Token";
        byte[][] keys = fixture.Contexts.Select(_ => RandomNumberGenerator.GetBytes(32)).ToArray();
        for (int i = 0; i < fixture.Contexts.Length; i++)
        {
            var context = fixture.Context(i);
            var secret = keys[i];
            var router = new UpdateRouter(fixture.Sessions.ForBrandWriter(context, 0), (_, _, _, _) => throw new TelegramIngressRejectedException());
            app.MapPost("/practice/telegram/" + i, async Task<IResult> (HttpRequest request, CancellationToken cancellation) =>
            {
                if (request.ContentLength is not (>= 1 and <= 4096)) return Results.BadRequest();
                byte[] body = new byte[(int)request.ContentLength.Value];
                await request.Body.ReadExactlyAsync(body, cancellation);
                try
                {
                    using var verifier = new TelegramWebhookVerifier(context, secret);
                    var update = verifier.Verify(request.Headers[header].Select(value => value ?? "").ToArray(), body);
                    if (update.ActorId != CheckoutFixture.Actor(0)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                    var result = await router.RouteAsync(update, cancellation);
                    return Results.Text(result.Code.ToString());
                }
                catch (TelegramIngressRejectedException) { return Results.Unauthorized(); }
            });
            await using var seed = await fixture.Sessions.BeginAsync(ct);
            await BrandUserStore.TransitionAsync(seed, context, CheckoutFixture.Actor(i), fixture.Users[i], 0, CustomerFlowState.Catalog, anchorMessageId: 77, ct: ct);
            await seed.CommitAsync(ct);
        }
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        async Task Callback(string route, int key, long bot, HttpStatusCode status, string? result = null, long? actorId = null)
        {
            long actor = actorId ?? CheckoutFixture.Actor(0);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/practice/telegram/" + route);
            request.Headers.Add(header, TelegramFixture.Header(keys[key]));
            request.Content = new ByteArrayContent(TelegramFixture.Body("v1|1|p|" + fixture.Product.ToString("N"), true, 501, actor, actor, bot));
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(status, response.StatusCode);
            if (result is not null) Assert.Equal(result, await response.Content.ReadAsStringAsync(ct));
        }
        for (int i = 0; i < fixture.Contexts.Length; i++)
        {
            int foreign = (i + 1) % fixture.Contexts.Length;
            await Callback(i.ToString(), foreign, fixture.Context(i).BotId, HttpStatusCode.Unauthorized);
            await Callback(i.ToString(), i, fixture.Context(foreign).BotId, HttpStatusCode.Unauthorized);
            await Callback(i.ToString(), i, fixture.Context(i).BotId, HttpStatusCode.Forbidden, actorId: CheckoutFixture.Actor(3));
        }
        await Callback("production", 0, fixture.Context(0).BotId, HttpStatusCode.NotFound);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE telegram_update_id=501", ct));
        for (int i = 0; i < fixture.Contexts.Length; i++)
        {
            await Callback(i.ToString(), i, fixture.Context(i).BotId, HttpStatusCode.OK, "Product");
            await Callback(i.ToString(), i, fixture.Context(i).BotId, HttpStatusCode.OK, "Duplicate");
        }
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(DISTINCT u.brand_id) FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id WHERE e.telegram_update_id=501", ct));
        const string adminPath = "/v1/admin/brands";
        using (var anonymous = await client.GetAsync(adminPath, ct)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TelegramFixture.Header(keys[0]));
        using (var botCredential = await client.GetAsync(adminPath, ct)) Assert.Equal(HttpStatusCode.Unauthorized, botCredential.StatusCode);
        IssuedAdminSession issued;
        await using (var login = await fixture.Sessions.BeginAsync(ct))
        {
            issued = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), ct);
            await login.CommitAsync(ct);
        }
        using (issued)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
            using (var approved = await client.GetAsync(adminPath, ct)) Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            await using var revoke = await fixture.Sessions.BeginAsync(ct);
            await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(issued.Id)), ct);
            await revoke.CommitAsync(ct);
            using var revoked = await client.GetAsync(adminPath, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        var actor = await fixture.AdminAsync(ct);
        var campaigns = new CampaignStore(fixture.Sessions);
        Guid batch = Guid.NewGuid();
        await campaigns.CreateAsync(actor, batch, fixture.Brands.Select(brand => new CampaignDraft(Guid.NewGuid(), brand, "en", "all", null, null, "Public practice message", null)).ToArray(), ct);
        await campaigns.TransitionAsync(actor, batch, false, Guid.NewGuid(), "Public practice approval", ct);
        for (int i = 0; i < fixture.Contexts.Length; i++)
        {
            await using var claim = await fixture.Sessions.BeginAsync(ct);
            var lease = await OutboxStore.ClaimAsync(claim, JobKind.CampaignMaterialize, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct);
            Assert.NotNull(lease);
            await claim.CommitAsync(ct);
            await new AudienceMaterializer(campaigns).RunAsync(fixture.Contexts.Single(context => context.BrandId == lease.Intent.BrandId), lease, ct);
        }
        long allowedChat = CheckoutFixture.Actor(0);
        var accepted = new HashSet<Guid>();
        var rejected = new List<(Guid Brand, long Bot, long Chat, string Recipient)>();
        var worker = new BroadcastWorker(campaigns, TimeSpan.FromMilliseconds(100), null, new Dictionary<string, string>());
        for (int i = 0; i < 6; i++)
        {
            var lease = await campaigns.ClaimNoticeAsync(Guid.NewGuid(), ct);
            Assert.NotNull(lease);
            var context = fixture.Contexts.Single(context => context.BrandId == lease.Intent.BrandId);
            await worker.RunAsync(context, lease, (dispatch, _, _) => Task.FromResult(
                dispatch.BotId == context.BotId && dispatch.Recipient == "customer_dm" && dispatch.ChatId == allowedChat && accepted.Add(context.BrandId)
                    ? new NoticeOutcome(NoticeOutcomeKind.Sent, i + 1)
                    : Reject(context.BrandId, dispatch)), ct);
            await Task.Delay(TimeSpan.FromMilliseconds(120), ct);
        }
        NoticeOutcome Reject(Guid brand, NoticeDispatch dispatch)
        {
            rejected.Add((brand, dispatch.BotId, dispatch.ChatId, dispatch.Recipient));
            return new(NoticeOutcomeKind.Failed);
        }
        Assert.True(accepted.SetEquals(fixture.Brands));
        Assert.Equal(fixture.Contexts.Select(context => (context.BrandId, context.BotId, CheckoutFixture.Actor(3), "customer_dm")).Order(), rejected.Order());
        Assert.Null(await campaigns.ClaimNoticeAsync(Guid.NewGuid(), ct));
        Assert.Equal(3L, await fixture.CountAsync("SELECT count(*) FROM shop.telegram_deliveries d JOIN shop.users u ON u.id=d.user_id WHERE d.status='succeeded' AND u.telegram_id=7001", ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.telegram_deliveries d JOIN shop.users u ON u.id=d.user_id WHERE d.status='succeeded' AND u.telegram_id<>7001", ct));
        await app.StopAsync(ct);
    }
}
