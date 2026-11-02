using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class UpdateRouterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Secret = SHA256.HashData("public synthetic webhook qualification"u8);

    [Fact]
    public async Task ExplicitConsentReplayCancelAndFreshConsentHaveNoFinancialEffect()
    {
        await using var fixture = await Menus();
        int reads = 0;
        var router = Router(fixture, () => { reads++; return true; });
        var subscribed = await router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct);
        Assert.Equal(UpdateRouteCode.Subscribed, subscribed.Code);
        Assert.NotNull(subscribed.EntityId);
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct));
        Assert.Equal(1, reads);
        var cancelled = await router.RouteAsync(Callback(fixture, 2, 'u', fixture.Product, updateId: 2), Ct);
        Assert.Equal(subscribed.EntityId, cancelled.EntityId);
        var fresh = await router.RouteAsync(Callback(fixture, 3, 's', fixture.Product, updateId: 3), Ct);
        Assert.NotEqual(subscribed.EntityId, fresh.EntityId);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions WHERE user_id=$1 AND closed_at IS NULL", DbValue.Uuid(fixture.Users[0])), row => row.GetInt64(0), Ct));
        Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions WHERE user_id=$1 AND cancelled_at IS NOT NULL", DbValue.Uuid(fixture.Users[0])), row => row.GetInt64(0), Ct));
        Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task ForeignTargetsWrongAnchorsAndUnknownMembersAreRejectedBeforeAvailabilityRead()
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => throw new InvalidOperationException("Unauthorized availability read."));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.PrivateProduct), Ct));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.Product, messageId: 78), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.Product, actor: 7999), Ct));
        await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(Callback(fixture, 1, 'o', fixture.Orders[1]), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.product_stock_subscriptions"), row => row.GetInt64(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task StaleAvailabilityRollsBackFlowAndEventAndCurrentSupplyDoesNotCreateConsent()
    {
        await using var fixture = await Menus();
        foreach (var observation in new[]
        {
            new RestockObservation(fixture.Product, 1, 0, DateTime.UtcNow, true),
            new RestockObservation(fixture.Product, 0, 0, DateTime.UtcNow.AddMinutes(-1), true),
            new RestockObservation(fixture.Product, 0, 0, DateTime.UtcNow, false)
        })
        {
            var router = new UpdateRouter(fixture.Sessions, (_, _, _) => Task.FromResult(observation));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct));
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(1, (await BrandUserStore.RequireAsync(read, fixture.Contexts[0], 7001, fixture.Users[0], Ct)).Flow.Revision);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task SingleBotOwnerExcludesCompetingConsumerWithoutBlockingOtherBrand()
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => throw new InvalidOperationException());
        await using (var owner = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.True(await BotConsumerOwnership.TryAcquireAsync(owner, fixture.Contexts[0], Ct));
            Assert.Equal(UpdateRouteCode.Busy, (await router.RouteAsync(Command(fixture, 0, 10), Ct)).Code);
            Assert.Equal(UpdateRouteCode.Menu, (await router.RouteAsync(Command(fixture, 1, 10), Ct)).Code);
        }
        var first = await router.RouteAsync(Command(fixture, 0, 10), Ct);
        var repeated = await router.RouteAsync(Command(fixture, 0, 10), Ct);
        Assert.Equal(UpdateRouteCode.Menu, first.Code);
        Assert.Equal(UpdateRouteCode.Duplicate, repeated.Code);
        Assert.Equal(first.FlowRevision, repeated.FlowRevision);
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("uncertain", false)]
    [InlineData("leased", false)]
    public async Task CancellationStopsKnownUnsentWorkAndPreservesUncertainDelivery(string status, bool canCancel)
    {
        await using var fixture = await Menus();
        var router = Router(fixture, () => true);
        Guid consent = (await router.RouteAsync(Callback(fixture, 1, 's', fixture.Product), Ct)).EntityId!.Value;
        Guid delivery = Guid.NewGuid(), job = Guid.NewGuid();
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.product_stock_subscriptions SET triggered_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(consent)), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,product_stock_subscription_id,status)
                VALUES ($1,$2,'customer_dm',$3,'restock_notice','send','public-restock',$4,$5)
                """, DbValue.Uuid(delivery), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(fixture.Users[0]), DbValue.Uuid(consent), DbValue.Text(status)), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.jobs(id,brand_id,job_kind,telegram_delivery_id,dedupe_key) VALUES ($1,$2,'send_telegram',$3,'public-restock-job')",
                DbValue.Uuid(job), DbValue.Uuid(fixture.Brands[0]), DbValue.Uuid(delivery)), Ct);
            await seed.CommitAsync(Ct);
        }
        var update = Callback(fixture, 2, 'u', fixture.Product, updateId: 2);
        if (canCancel) Assert.Equal(UpdateRouteCode.Unsubscribed, (await router.RouteAsync(update, Ct)).Code);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteAsync(update, Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(canCancel, await read.QuerySingleAsync(new DbStatement("SELECT closed_at IS NOT NULL FROM shop.product_stock_subscriptions WHERE id=$1", DbValue.Uuid(consent)), row => row.GetBoolean(0), Ct));
        Assert.Equal(canCancel ? "cancelled" : status, await read.QuerySingleAsync(new DbStatement("SELECT status FROM shop.telegram_deliveries WHERE id=$1", DbValue.Uuid(delivery)), row => row.GetString(0), Ct));
        Assert.Equal(canCancel ? "cancelled" : "pending", await read.QuerySingleAsync(new DbStatement("SELECT status FROM shop.jobs WHERE id=$1", DbValue.Uuid(job)), row => row.GetString(0), Ct));
    }

    [Fact]
    public async Task VerifiedPrivateBindingMessageAdvancesOnlyTheBotChannel()
    {
        await using var fixture = await Menus();
        Guid admin = Guid.NewGuid();
        string password = new('s', 20);
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-ingress-admin','password',$2)", DbValue.Uuid(admin), DbValue.Text(AdminAuth.CreatePasswordVerifier(password))), Ct);
            await seed.CommitAsync(Ct);
        }
        IssuedAdminSession ticket;
        await using (var login = await fixture.Sessions.BeginAsync(Ct))
        {
            ticket = await AdminSessionStore.LoginAsync(login, "public-ingress-admin", password.AsMemory(), TimeSpan.FromHours(1), Ct);
            await login.CommitAsync(Ct);
        }
        using (ticket)
        {
            AuthenticatedAdmin actor;
            await using (var authenticate = await fixture.Sessions.BeginAsync(Ct)) actor = await AdminSessionStore.AuthenticateAsync(authenticate, ticket.Token, Ct);
            AdminToken nonce;
            await using (var begin = await fixture.Sessions.BeginAsync(Ct))
            {
                nonce = (await AdminBindingStore.BeginAsync(begin, actor, 7999, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!;
                await begin.CommitAsync(Ct);
            }
            using (nonce)
            using (var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret))
            {
                string command = "/bind " + admin.ToString("N") + " " + nonce.ExportForTransport();
                var router = Router(fixture, () => throw new InvalidOperationException());
                await Assert.ThrowsAsync<TelegramIngressRejectedException>(() => router.RouteAsync(verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body(command)), Ct));
                var update = verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body(command, actorId: 7999, chatId: 7999));
                Assert.Equal(UpdateRouteCode.StaffProofObserved, (await router.RouteAsync(update, Ct)).Code);
                await using var complete = await fixture.Sessions.BeginAsync(Ct);
                Assert.False(await AdminBindingStore.IsVerifiedStaffAsync(complete, 7999, Ct));
                Assert.True(await AdminBindingStore.CompleteAsync(complete, actor, 7999, 0, nonce, Guid.NewGuid(), Guid.NewGuid(), Ct));
                Assert.True(await AdminBindingStore.IsVerifiedStaffAsync(complete, 7999, Ct));
                Assert.Equal(4L, await complete.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), row => row.GetInt64(0), Ct));
                Assert.Equal(0L, await complete.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events"), row => row.GetInt64(0), Ct));
                await complete.CommitAsync(Ct);
            }
        }
    }

    private static UpdateRouter Router(BrandFixture fixture, Func<bool> unavailable) => new(fixture.Sessions,
        (_, product, _) => Task.FromResult(new RestockObservation(product, 0, 0, DateTime.UtcNow, unavailable())));
    private static VerifiedTelegramUpdate Callback(BrandFixture fixture, long revision, char action, Guid target, long updateId = 1, long messageId = 77, long actor = 7001)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[0], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body("v1|" + revision + "|" + action + "|" + target.ToString("N"), true, updateId, actor, actor, messageId: messageId));
    }
    private static VerifiedTelegramUpdate Command(BrandFixture fixture, int brand, long updateId)
    {
        using var verifier = new TelegramWebhookVerifier(fixture.Contexts[brand], Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body("/menu", updateId: updateId));
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
}
