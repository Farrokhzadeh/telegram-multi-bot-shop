using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class RuntimeWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WriterRegistry Registry(CheckoutFixture f) => new(f.Sessions, f.Contexts.Select(c => new BrandWriterRegistration(c, 0)));
    [Fact]
    public async Task UnadmittedReturningWriteCannotCommit()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using (var session = await Registry(f).ForBrand(f.Context(0)).BeginAsync(Ct))
        {
            Assert.Equal(f.Users[0], await session.QuerySingleAsync(new DbStatement("UPDATE shop.users SET language=\'fa\' WHERE id=$1 RETURNING id", DbValue.Uuid(f.Users[0])), r => r.GetGuid(0), Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.CommitAsync(Ct));
            Assert.Equal(DbSessionState.Aborted, session.State);
        }
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.users WHERE language=\'en\'", Ct));
    }
    [Fact]
    public async Task ExistingCheckoutApiUsesBoundRevisionAndRejectsOtherBrand()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct);
        var registry = Registry(f); var factory = registry.ForBrand(f.Context(0)); var store = new CheckoutStore(factory);
        Assert.Equal(CheckoutOutcome.Purchased, (await store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), f.Request(), Ct)).Outcome);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PurchaseLocalAsync(f.Context(1), CheckoutFixture.Actor(1), f.Request(), Ct));
        var actor = await f.AdminAsync(Ct);
        await registry.Controls.SetActiveAsync(actor, f.Context(0), 0, false, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), f.Request(), Ct));
        await registry.Controls.SetActiveAsync(actor, f.Context(0), 1, true, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), f.Request(), Ct));
        var fresh = new CheckoutStore(f.Sessions.ForBrandWriter(f.Context(0), 2));
        Assert.Equal(CheckoutOutcome.Purchased, (await fresh.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), f.Request() with { PolicyRevision = 2 }, Ct)).Outcome);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
    }
    [Fact]
    public async Task ReadOnlyCloneAllowsReadCommitButRejectsSqlWriteAndRebinding()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var factory = Registry(f).ForBrand(f.Context(0));
        Assert.Throws<InvalidOperationException>(() => factory.ForBrandWriter(f.Context(1), 0));
        await using (var read = await factory.BeginReadOnlyAsync(Ct))
        {
            Assert.False(read.IsWriterScoped);
            Assert.Equal(3L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), r => r.GetInt64(0), Ct));
            await read.CommitAsync(Ct);
        }
        await using var denied = await factory.BeginReadOnlyAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => denied.ExecuteAsync(new DbStatement("UPDATE shop.brands SET active=false"), Ct));
    }
    [Fact]
    public async Task EquivalentAuthenticatedContextWorksButForeignBindingPoisonsTransaction()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct); var registry = Registry(f);
        var equivalent = CheckoutFixture.ResolveContexts()[0];
        await using (var session = await registry.ForBrand(equivalent).BeginAsync(Ct))
        { await session.AdmitWriterAsync(equivalent, Ct); await session.CommitAsync(Ct); }
        await using var wrong = await registry.ForBrand(f.Context(0)).BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandWriterStore.RequireAsync(wrong, f.Context(1), 0, Ct));
        Assert.Equal(DbSessionState.Aborted, wrong.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.CommitAsync(Ct));
    }
    [Fact]
    public async Task SharedAdmissionRequiresExactActiveSetAndExplicitSharedProfile()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct); var registry = Registry(f);
        await using (var shared = await registry.Shared.BeginAsync(Ct))
        { await shared.AdmitSharedWriterAsync(Ct); await shared.CommitAsync(Ct); }
        await using (var partial = await f.Sessions.ForSharedWriters([new(f.Context(0), 0)]).BeginAsync(Ct))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => partial.AdmitSharedWriterAsync(Ct)); }
        await using (var single = await registry.ForBrand(f.Context(0)).BeginAsync(Ct))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => single.AdmitSharedWriterAsync(Ct)); }
        await registry.Controls.SetActiveAsync(await f.AdminAsync(Ct), f.Context(0), 0, false, Guid.NewGuid(), Ct);
        await using (var stale = await registry.Shared.BeginAsync(Ct))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stale.AdmitSharedWriterAsync(Ct)); }
        await using var current = await f.Sessions.ForSharedWriters(f.Contexts.Skip(1).Select(c => new BrandWriterRegistration(c, 0))).BeginAsync(Ct);
        await current.AdmitSharedWriterAsync(Ct); await current.CommitAsync(Ct);
    }
    [Fact]
    public async Task ClaimsAndLeaseCompletionAreRestrictedToBoundBrand()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 3, ct: Ct); var registry = Registry(f);
        for (int i = 0; i < 3; i++) Assert.Equal(CheckoutOutcome.Purchased, (await f.Store.PurchaseLocalAsync(f.Context(i), CheckoutFixture.Actor(i), f.Request(), Ct)).Outcome);
        JobLease lease;
        await using (var claim = await registry.ForBrand(f.Context(0)).BeginAsync(Ct))
        {
            lease = Assert.IsType<JobLease>(await OutboxStore.ClaimAsync(claim, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct));
            Assert.Equal(f.Brands[0], lease.Intent.BrandId); await claim.CommitAsync(Ct);
        }
        await using (var shared = await registry.Shared.BeginAsync(Ct))
        { Assert.Null(await OutboxStore.ClaimAsync(shared, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(1), Ct)); await shared.CommitAsync(Ct); }
        await using (var foreign = await registry.ForBrand(f.Context(1)).BeginAsync(Ct))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OutboxStore.CompleteAsync(foreign, lease, Ct)); }
        await using (var finish = await registry.ForBrand(f.Context(0)).BeginAsync(Ct))
        { await OutboxStore.CompleteAsync(finish, lease, Ct); await finish.CommitAsync(Ct); }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='succeeded'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE status='pending' AND attempts=0", Ct));
    }
    [Fact]
    public async Task SharedStockImportRejectsSingleBrandAndStaleSharedFactories()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct); var registry = Registry(f); var actor = await f.AdminAsync(Ct);
        static InventoryImportItem[] Items() => [new(Guid.NewGuid(), "public synthetic runtime stock")];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AdminCatalogStore(registry.ForBrand(f.Context(0))).ImportAsync(actor, f.Product, Items(), Guid.NewGuid(), Ct));
        await new AdminCatalogStore(registry.Shared).ImportAsync(actor, f.Product, Items(), Guid.NewGuid(), Ct);
        await registry.Controls.SetActiveAsync(actor, f.Context(0), 0, false, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AdminCatalogStore(registry.Shared).ImportAsync(actor, f.Product, Items(), Guid.NewGuid(), Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items", Ct));
    }
    [Fact]
    public async Task ConcurrentNewMembersDoNotDeadlockBrandForeignKeyLocks()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var factory = Registry(f).ForBrand(f.Context(0)); int ready = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Route(long actor)
        {
            await using var session = await factory.BeginAsync(Ct);
            await BrandUserStore.ObservePrivateAsync(session, f.Context(0), actor, Ct, admit: false);
            if (Interlocked.Increment(ref ready) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
            await session.AdmitWriterAsync(f.Context(0), Ct); await session.CommitAsync(Ct);
        }
        await Task.WhenAll(Route(8101), Route(8102));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.users", Ct));
    }
    [Fact]
    public async Task SharedMaintenanceAdmitsCurrentRegistrationAndRejectsStaleWriter()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct); var registry = Registry(f);
        var store = new BotActivityRetentionStore(registry.Shared);
        Assert.Equal(0, (await store.CleanAsync(ct: Ct)).Deleted);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BotActivityRetentionStore(registry.ForBrand(f.Context(0))).CleanAsync(ct: Ct));
        await registry.Controls.SetActiveAsync(await f.AdminAsync(Ct), f.Context(0), 0, false, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CleanAsync(ct: Ct));
    }

}
