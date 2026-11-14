using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BrandOutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Secret = SHA256.HashData("public synthetic output qualification"u8);

    [Fact]
    public async Task BrandOutputsNeverContainForeignIdentifiersOrAssets()
    {
        await using var fixture = await DistinctOutputs();
        var snapshots = new BrandPolicySnapshot[3];
        for (int i = 0; i < 3; i++)
            snapshots[i] = await Read(fixture, i);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("public-brand-" + i, snapshots[i].PublicName);
            Assert.Equal("logo-asset-" + i, snapshots[i].LogoAssetRef);
            for (int j = 0; j < 3; j++)
                if (i != j)
                {
                    Assert.DoesNotContain("public-brand-" + j, snapshots[i].PublicName);
                    Assert.DoesNotContain("logo-asset-" + j, snapshots[i].LogoAssetRef!);
                }
        }
        Assert.Equal("ناموجود", Localization.Text(snapshots[0], CustomerText.OutOfStock));
        Assert.Equal("Out of stock", Localization.Text(snapshots[1], CustomerText.OutOfStock));
        await using var write = await fixture.Sessions.BeginAsync(Ct);
        await write.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(fixture.Users[1])), Ct);
        await write.ExecuteAsync(new DbStatement("UPDATE shop.users SET banned_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(fixture.Users[2])), Ct);
        await write.CommitAsync(Ct);
        foreach (int i in new[] { 1, 2 })
        {
            var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(fixture, i));
            Assert.DoesNotContain("public-brand-", denied.Message);
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(4L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), row => row.GetInt64(0), Ct));
        await read.CommitAsync(Ct);
    }

    [Fact]
    public async Task NeverStartedAndBlockedCustomersAreRejectedWithoutFlowWrites()
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => true);
        Assert.Equal(UpdateRouteCode.Started, (await router.RouteAsync(Command(fixture, 0, 11, actor: 9999, value: "/start"), Ct)).Code);
        Assert.Equal(UpdateRouteCode.Menu, (await router.RouteAsync(Command(fixture, 0, 12, actor: 9999), Ct)).Code);
        await using var write = await fixture.Sessions.BeginAsync(Ct);
        Guid neverStarted;
        await using (var look = await fixture.Sessions.BeginAsync(Ct))
            neverStarted = (await BrandUserStore.RequireAsync(look, fixture.Contexts[0], 9999, null, Ct)).Id;
        await write.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(neverStarted)), Ct);
        await write.CommitAsync(Ct);
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Command(fixture, 0, 13, actor: 9999), Ct));
        Assert.Equal(UpdateRouteCode.Started, (await router.RouteAsync(Command(fixture, 0, 14, actor: 9999, value: "/start"), Ct)).Code);
        await using var seed = await fixture.Sessions.BeginAsync(Ct);
        await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET banned_at=CURRENT_TIMESTAMP WHERE telegram_id=7001 AND brand_id IN ($1,$2)",
            DbValue.Uuid(fixture.Brands[1]), DbValue.Uuid(fixture.Brands[2])), Ct);
        await seed.CommitAsync(Ct);
        foreach (int i in new[] { 1, 2 })
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Command(fixture, i, 20 + i), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(5L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), row => row.GetInt64(0), Ct));
        Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments"), row => row.GetInt64(0), Ct));
        await read.CommitAsync(Ct);
    }

    [Fact]
    public async Task VisibilityPublicationSeparationKeepsForeignAndHiddenListingOutputBrandScoped()
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => true);
        var snapshots = new BrandPolicySnapshot[3];
        for (int i = 0; i < 3; i++)
            snapshots[i] = await Read(fixture, i);
        Assert.Equal("Out of stock", Localization.Text(snapshots[1], CustomerText.OutOfStock));
        Assert.Equal("ناموجود", Localization.Text(snapshots[0], CustomerText.OutOfStock));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 'p', fixture.PrivateProduct), Ct));
        await using var hide = await fixture.Sessions.BeginAsync(Ct);
        await hide.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=false WHERE product_id=$1", DbValue.Uuid(fixture.Product)), Ct);
        await hide.CommitAsync(Ct);
        foreach (int i in new[] { 0, 1, 2 })
            await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 'p', fixture.Product, brand: i, updateId: 11 + i), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), row => row.GetInt64(0), Ct));
        await read.CommitAsync(Ct);
    }

    [Fact]
    public async Task RestockConsentScopeNeverCrossesBrandsOrMovesMoney()
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => true);
        var consent = (await router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct)).EntityId!.Value;
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions"), row => row.GetInt64(0), Ct));
        Assert.Equal((fixture.Users[0], fixture.Product), await read.QuerySingleAsync(new DbStatement("SELECT user_id,product_id FROM shop.product_stock_subscriptions WHERE id=$1", DbValue.Uuid(consent)), row => (row.GetGuid(0), row.GetGuid(1)), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions WHERE user_id IN ($1,$2)", DbValue.Uuid(fixture.Users[1]), DbValue.Uuid(fixture.Users[2])), row => row.GetInt64(0), Ct));
        Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.discount_uses"), row => row.GetInt64(0), Ct));
        await read.CommitAsync(Ct);
    }

    private static async Task<BrandFixture> DistinctOutputs()
    {
        var fixture = await BrandFixture.CreateAsync(Ct);
        try
        {
            await using var seed = await fixture.Sessions.BeginAsync(Ct);
            for (int i = 0; i < 3; i++)
                await seed.ExecuteAsync(new DbStatement("UPDATE shop.brands SET logo_asset_ref=$1 WHERE id=$2", DbValue.Text("logo-asset-" + i), DbValue.Uuid(fixture.Brands[i])), Ct);
            await seed.CommitAsync(Ct);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private static async Task<BrandFixture> Menus()
    {
        var fixture = await BrandFixture.CreateAsync(Ct);
        try
        {
            await using var seed = await fixture.Sessions.BeginAsync(Ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=true"), Ct);
            for (int i = 0; i < 3; i++)
                await BrandUserStore.TransitionAsync(seed, fixture.Contexts[i], 7001, fixture.Users[i], 0, CustomerFlowState.Quantity, productId: fixture.Product, anchorMessageId: 77, ct: Ct);
            await seed.CommitAsync(Ct);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private static async Task<BrandPolicySnapshot> Read(BrandFixture fixture, int index)
    {
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var snapshot = await BrandPolicyStore.ReadAsync(read, fixture.Contexts[index], 7001, Ct);
        await read.CommitAsync(Ct);
        return snapshot;
    }
    private static UpdateRouter Router(BrandFixture fixture, Func<bool> unavailable) => new(fixture.Sessions,
        (_, product, _) => Task.FromResult(new RestockObservation(product, 0, 0, DateTime.UtcNow, unavailable())));
    private static VerifiedTelegramUpdate Callback(BrandFixture fixture, long revision, char action, Guid target, int brand = 0, long updateId = 1, long messageId = 77, long actor = 7001)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[brand], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body("v1|" + revision + "|" + action + "|" + target.ToString("N"), true, updateId, actor, actor, botId: 1001 + brand, messageId: messageId));
    }
    private static VerifiedTelegramUpdate Command(BrandFixture fixture, int brand, long updateId, string value = "/menu", long actor = 7001)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[brand], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body(value, updateId: updateId, actorId: actor, chatId: actor));
    }
}
