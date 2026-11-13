using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;
namespace Platform.IntegrationTests;

public sealed class CutoverTelegramRouterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Secret = SHA256.HashData("public cutover router fixture only"u8);
    [Fact]
    public async Task VerifiedCallbackCommitsOnceAndUntrustedBindingsCannotConsumeAnUpdate()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(users: 1, ct: Ct);
        Guid run = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            _ = await BrandUserStore.TransitionAsync(seed, fixture.Context(0), 7001, fixture.Users[0], 0, CustomerFlowState.Catalog, anchorMessageId: 77, ct: Ct);
            await seed.CommitAsync(Ct);
        }
        CutoverTelegramInput Input(long revision = 1, Guid? target = null, long actor = 7001, long bot = 1001) => new(run, 501,
            Encoding.UTF8.GetString(TelegramFixture.Body("v1|" + revision + "|p|" + (target ?? fixture.Product).ToString("N"), true, 501, actor, actor, bot)), TelegramFixture.Header(Secret));
        Task<UpdateRouteResult> Route(CutoverTelegramInput input) => CutoverTelegramRouter.RouteAsync(fixture.Sessions, fixture.Context(0), fixture.Users[0], run, Secret, input, Ct);
        foreach (var invalid in new[] { Input() with { Run = Guid.NewGuid() }, Input() with { Header = new string('x', 43) }, Input() with { Update = 502 }, Input(actor: 7999), Input(bot: 1002), Input(target: Guid.NewGuid()), Input(revision: 0) })
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => Route(invalid));
        Assert.Equal(UpdateRouteCode.Product, (await Route(Input())).Code);
        Assert.Equal(UpdateRouteCode.Duplicate, (await Route(Input())).Code);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE telegram_update_id=501", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
    }
    [Fact]
    public async Task ForeignBrandAndMembershipStayRejectedWhileTheSameUpdateIsValidForItsBoundMember()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(users: 3, ct: Ct);
        Guid run = Guid.NewGuid();
        var input = new CutoverTelegramInput(run, 601, Encoding.UTF8.GetString(TelegramFixture.Body("/menu", updateId: 601)), TelegramFixture.Header(Secret));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => CutoverTelegramRouter.RouteAsync(fixture.Sessions, fixture.Context(1), fixture.Users[1], run, Secret, input, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CutoverTelegramRouter.RouteAsync(fixture.Sessions, fixture.Context(0), fixture.Users[1], run, Secret, input, Ct));
        Assert.Equal(UpdateRouteCode.Menu, (await CutoverTelegramRouter.RouteAsync(fixture.Sessions, fixture.Context(0), fixture.Users[0], run, Secret, input, Ct)).Code);
        Assert.Equal(UpdateRouteCode.Duplicate, (await CutoverTelegramRouter.RouteAsync(fixture.Sessions, fixture.Context(0), fixture.Users[0], run, Secret, input, Ct)).Code);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE user_id='" + fixture.Users[1].ToString("D") + "'::uuid", Ct));
    }
}
