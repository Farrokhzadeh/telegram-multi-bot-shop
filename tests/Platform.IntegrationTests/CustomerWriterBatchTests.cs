using System.Security.Cryptography;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CustomerWriterBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Secret = SHA256.HashData("public synthetic writer batch webhook"u8);
    private static CatalogStore Catalog(CheckoutFixture f, Func<SupplierCatalogRequest, CancellationToken, Task<SupplierCatalogResult>>? fetch = null) =>
        new(f.Sessions, [new("synthetic", "public account")], fetch ?? ((_, _) => throw new InvalidOperationException("Unexpected transport.")));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    private static CheckoutRequest Request(CheckoutFixture f) => new(OrderIds.New(), f.Product, 1);
    private static VerifiedTelegramUpdate Update(CheckoutFixture f, string value, long id, int brand = 0, bool callback = false, long message = 77, long actor = 7001)
    {
        using var verifier = new TelegramWebhookVerifier(f.Context(brand), Secret);
        return verifier.Verify([TelegramFixture.Header(Secret)], TelegramFixture.Body(value, callback, id, actor, actor, messageId: message));
    }
    private static UpdateRouter Router(CheckoutFixture f, Func<BrandContext, long, Guid, CancellationToken, Task<RestockObservation>>? observe = null, HumanSupportRouter? support = null) =>
        new(f.Sessions, observe ?? ((_, _, _, _) => throw new InvalidOperationException("Unexpected restock observation.")), support);
    private static async Task Quantity(CheckoutFixture f, long revision = 0)
    {
        await using var seed = await f.Sessions.BeginAsync(Ct);
        await BrandUserStore.TransitionAsync(seed, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], revision, CustomerFlowState.Quantity, productId: f.Product, anchorMessageId: 77, ct: Ct);
        await seed.CommitAsync(Ct);
    }

    [Fact]
    public async Task LocalAndPreparedCheckoutReplayRequireCurrentWriterAndOtherBrandContinues()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 4, ct: Ct);
        var local = f.Request();
        Assert.Equal(CheckoutOutcome.Purchased, (await f.Store.PurchaseLocalForBrandAsync(f.Context(0), 0, CheckoutFixture.Actor(0), local, Ct)).Outcome);
        var writer = new BrandWriterStore(f.Sessions); var admin = await f.AdminAsync(Ct);
        await writer.SetActiveAsync(admin, f.Context(0), 0, false, Guid.NewGuid(), Ct);
        var service = new WalletCheckoutService(f.Sessions, f.Store, Catalog(f));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PurchaseForBrandAsync(f.Context(0), 0, CheckoutFixture.Actor(0), Request(f), Member, Ct));
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseForBrandAsync(f.Context(1), 0, CheckoutFixture.Actor(1), Request(f), Member, Ct)).Outcome);
        await writer.SetActiveAsync(admin, f.Context(0), 1, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Store.PurchaseLocalForBrandAsync(f.Context(0), 0, CheckoutFixture.Actor(0), local, Ct));
        Assert.Equal(CheckoutOutcome.Replayed, (await f.Store.PurchaseLocalForBrandAsync(f.Context(0), 2, CheckoutFixture.Actor(0), local, Ct)).Outcome);
        var request = Request(f);
        Assert.Equal(CheckoutOutcome.Purchased, (await service.PurchaseForBrandAsync(f.Context(0), 2, CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, (await service.PurchaseForBrandAsync(f.Context(0), 2, CheckoutFixture.Actor(0), request, (_, _, _) => throw new InvalidOperationException(), Ct)).Outcome);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }

    [Fact]
    public async Task PauseDuringSupplierObservationBlocksItsApplicationAndCheckout()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        Guid source = Guid.NewGuid();
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,enabled) VALUES ($1,$2,'synthetic','public product',true)", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind='supplier',active_supplier_source_id=$1,revision=revision+1 WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(f.Product)), Ct);
            await seed.CommitAsync(Ct);
        }
        var admin = await f.AdminAsync(Ct); int calls = 0;
        var catalog = Catalog(f, async (_, ct) =>
        {
            calls++;
            await new BrandWriterStore(f.Sessions).SetActiveAsync(admin, f.Context(0), 0, false, Guid.NewGuid(), ct);
            return new(SupplierCatalogStatus.Available, 1, 1, "USD", "public quote");
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WalletCheckoutService(f.Sessions, f.Store, catalog).PurchaseForBrandAsync(f.Context(0), 0, CheckoutFixture.Actor(0), Request(f), Member, Ct));
        Assert.Equal(1, calls);
        Assert.Equal(0L, await f.CountAsync("SELECT observation_revision FROM shop.product_supplier_sources", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
    }

    [Fact]
    public async Task RejectedBorrowedCheckoutRollsBackEarlierMemberPreference()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        string language;
        await using (var read = await f.Sessions.BeginAsync(Ct)) language = (await BrandUserStore.RequireAsync(read, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], Ct)).Language;
        await using (var write = await f.Sessions.BeginAsync(Ct))
        {
            await BrandUserStore.SetPreferencesAsync(write, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], language == "en" ? "fa" : "en", false, Ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Store.AcceptLocalForBrandAsync(write, f.Context(0), 1, CheckoutFixture.Actor(0), f.Request(), Ct));
            Assert.Equal(DbSessionState.Aborted, write.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(Ct));
        }
        await using var final = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(language, (await BrandUserStore.RequireAsync(final, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], Ct)).Language);
        Assert.Equal(0L, await final.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), r => r.GetInt64(0), Ct));
    }

    [Fact]
    public async Task MenuProductAndQuantityRejectStaleWriterWithoutConsumingUpdateOrNewMember()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var router = Router(f);
        Assert.Equal(UpdateRouteCode.Menu, (await router.RouteForBrandAsync(Update(f, "/menu", 1), 0, Ct)).Code);
        await Quantity(f, 1);
        Assert.Equal(UpdateRouteCode.Product, (await router.RouteForBrandAsync(Update(f, "v1|2|p|" + f.Product.ToString("N"), 2, callback: true), 0, Ct)).Code);
        Assert.Equal(UpdateRouteCode.Quantity, (await router.RouteForBrandAsync(Update(f, "1", 3, message: 78), 0, Ct)).Code);
        var admin = await f.AdminAsync(Ct); var writer = new BrandWriterStore(f.Sessions);
        await writer.SetActiveAsync(admin, f.Context(0), 0, false, Guid.NewGuid(), Ct);
        Assert.Equal(UpdateRouteCode.Menu, (await router.RouteForBrandAsync(Update(f, "/menu", 1, brand: 1), 0, Ct)).Code);
        await writer.SetActiveAsync(admin, f.Context(0), 1, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, "/menu", 4), 0, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, "/menu", 5, actor: 7999), 0, Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT telegram_update_highwater FROM shop.brands WHERE bot_id=1001", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
        Assert.Equal(UpdateRouteCode.Menu, (await router.RouteForBrandAsync(Update(f, "/menu", 4), 2, Ct)).Code);
    }

    [Fact]
    public async Task RestockCallbackCarriesRevisionAndRechecksAfterObservation()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await Quantity(f);
        var admin = await f.AdminAsync(Ct); var writer = new BrandWriterStore(f.Sessions); int calls = 0;
        var router = Router(f, async (context, revision, product, ct) =>
        {
            calls++; Assert.Equal(0, revision);
            await writer.SetActiveAsync(admin, context, revision, false, Guid.NewGuid(), ct);
            return new(product, 0, 0, DateTime.UtcNow, true);
        });
        string command = "v1|1|s|" + f.Product.ToString("N");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, command, 30, callback: true), 0, Ct));
        Assert.Equal(1, calls);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.product_stock_subscriptions", Ct));
        await writer.SetActiveAsync(admin, f.Context(0), 1, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, command, 30, callback: true), 0, Ct));
        Assert.Equal(1, calls);
        var fresh = Router(f, (_, revision, product, _) => { Assert.Equal(2, revision); return Task.FromResult(new RestockObservation(product, 0, 0, DateTime.UtcNow, true)); });
        Assert.Equal(UpdateRouteCode.Subscribed, (await fresh.RouteForBrandAsync(Update(f, command, 30, callback: true), 2, Ct)).Code);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.product_stock_subscriptions", Ct));
    }

    [Fact]
    public async Task SupportDelegationKeepsMessagesFencedAcrossResume()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct); AdminToken nonce;
        await using (var begin = await f.Sessions.BeginAsync(Ct))
        { nonce = (await AdminBindingStore.BeginAsync(begin, actor, 7999, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!; await begin.CommitAsync(Ct); }
        using (nonce)
        {
            await using (var proof = await f.Sessions.BeginAsync(Ct))
            { Assert.True(await AdminBindingStore.ObservePrivateProofForBrandAsync(proof, f.Context(0), 0, actor.AdminId, nonce, PrivateTelegramProof.FromVerifiedIngress(f.Context(0), 7999, 7999, false, nonce), Ct)); await proof.CommitAsync(Ct); }
            await using var complete = await f.Sessions.BeginAsync(Ct);
            Assert.True(await AdminBindingStore.CompleteAsync(complete, actor, 7999, actor.AuthVersion, nonce, Guid.NewGuid(), Guid.NewGuid(), Ct));
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.admins SET public_contact_handle='public_support' WHERE id=$1", DbValue.Uuid(actor.AdminId)), Ct);
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.brands SET support_enabled=true,customer_support_admin_id=$1,policy_version=policy_version+1 WHERE id=$2", DbValue.Uuid(actor.AdminId), DbValue.Uuid(f.Brands[0])), Ct);
            await complete.CommitAsync(Ct);
        }
        var router = Router(f, support: new HumanSupportRouter(f.Sessions));
        Assert.Equal(UpdateRouteCode.Support, (await router.RouteForBrandAsync(Update(f, "/support", 101, message: 101), 1, Ct)).Code);
        actor = await f.AdminAsync(Ct); var writer = new BrandWriterStore(f.Sessions);
        await writer.SetActiveAsync(actor, f.Context(0), 1, false, Guid.NewGuid(), Ct);
        await writer.SetActiveAsync(actor, f.Context(0), 2, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, "public reply", 102, message: 102), 1, Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.support_messages", Ct));
        Assert.Equal(UpdateRouteCode.Support, (await router.RouteForBrandAsync(Update(f, "public reply", 102, message: 102), 3, Ct)).Code);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.support_messages", Ct));
    }

    [Fact]
    public async Task PrivateStaffProofUsesAdminThenBrandAdmissionAndDoesNotCreateMembership()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var actor = await f.AdminAsync(Ct); AdminToken nonce;
        await using (var begin = await f.Sessions.BeginAsync(Ct))
        { nonce = (await AdminBindingStore.BeginAsync(begin, actor, 7999, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), Ct))!; await begin.CommitAsync(Ct); }
        using (nonce)
        {
            var writer = new BrandWriterStore(f.Sessions); var router = Router(f);
            string command = "/bind " + actor.AdminId.ToString("N") + " " + nonce.ExportForTransport();
            await writer.SetActiveAsync(actor, f.Context(0), 0, false, Guid.NewGuid(), Ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.RouteForBrandAsync(Update(f, command, 201, actor: 7999), 0, Ct));
            await writer.SetActiveAsync(actor, f.Context(0), 1, true, Guid.NewGuid(), Ct);
            Assert.Equal(UpdateRouteCode.StaffProofObserved, (await router.RouteForBrandAsync(Update(f, command, 201, actor: 7999), 2, Ct)).Code);
            Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE reason='telegram_private_proof'", Ct));
            Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
        }
    }

    [Fact]
    public async Task CatalogEntrypointsRejectStaleRevisionBeforeLegacyObservation()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var catalog = Catalog(f); var context = f.Context(0); long actor = CheckoutFixture.Actor(0);
        Assert.NotNull(await catalog.GetForBrandAsync(context, 0, actor, f.Product, Ct));
        Assert.NotNull(await catalog.QuoteForBrandAsync(context, 0, actor, new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        Assert.NotNull(await catalog.ObserveRestockForBrandAsync(context, 0, actor, f.Product, Ct));
        var writer = new BrandWriterStore(f.Sessions); var admin = await f.AdminAsync(Ct);
        await writer.SetActiveAsync(admin, context, 0, false, Guid.NewGuid(), Ct);
        await writer.SetActiveAsync(admin, context, 1, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.GetForBrandAsync(context, 0, actor, f.Product, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.PrepareForBrandAsync(context, 0, actor, new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.QuoteForBrandAsync(context, 0, actor, new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.ObserveRestockForBrandAsync(context, 0, actor, f.Product, Ct));
        Assert.NotNull(await catalog.PrepareForBrandAsync(context, 2, actor, new(f.Product, 1, CheckoutPayment.Wallet), Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }
}
