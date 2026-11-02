using System.Security.Cryptography;
using Npgsql;
using Platform.Core;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class LocalPreorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task FiveThenTwoAcrossBrandsWaitWholeThroughThreeThreeOneAndReleaseOnlyCompleteManifests()
    {
        await using var f = await Fixture();
        var admin = await f.AdminAsync(Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin, new(coupon, "PUBLIC-FIFO", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE fulfillment_status='waiting_stock' AND payment_due_at IS NULL", Ct));
        await Restock(f, 3); await DrainAll(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        await Restock(f, 3); await DrainAll(f);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(0L, await Units(f, b));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=5", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
        var delivery = new DeliveryStore(f.Sessions, f.Secrets);
        Assert.Null(await delivery.PrepareAsync(f.Context(0), await Claim(f, JobKind.PrepareManifest), Ct));
        Assert.Equal(5L, await f.CountAsync("SELECT redeemed_count FROM shop.discounts", Ct));
        byte[] manifest = await delivery.ReadManifestAsync(f.Context(0), CheckoutFixture.Actor(0), a, Ct);
        Assert.NotEmpty(manifest); CryptographicOperations.ZeroMemory(manifest);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delivery.ReadManifestAsync(f.Context(1), CheckoutFixture.Actor(1), a, Ct));
        await Restock(f, 1); await DrainAll(f);
        Assert.Equal(2L, await Units(f, b));
        await delivery.PrepareAsync(f.Context(1), await Claim(f, JobKind.PrepareManifest), Ct);
        await DrainAll(f);
        Assert.Equal(7L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.supplier_requests", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='expire_order_payment'", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldHeadBlocksUntilAuditedResumeOrCancellationWithoutChangingPriorityOrMoney(bool cancel)
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        DateTime accepted;
        await using (var read = await f.Sessions.BeginAsync(Ct)) accepted = await read.QuerySingleAsync(new DbStatement("SELECT payment_confirmed_at FROM shop.orders WHERE id=$1", DbValue.Uuid(a)), r => r.GetUtc(0), Ct);
        var admin = await f.AdminAsync(Ct);
        var store = new OrderAdministrationStore(f.Sessions, f.Secrets);
        await store.ResolveAsync(admin, f.Context(0), a, new("pause_preorder", await Revision(f, a), "Public hold", Guid.NewGuid()), Ct);
        await Restock(f, 7); await DrainAll(f);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        await store.ResolveAsync(admin, f.Context(0), a, new(cancel ? "cancel_local_preorder" : "resume_preorder", await Revision(f, a), "Public resolution", Guid.NewGuid()), Ct);
        await DrainAll(f);
        Assert.Equal(cancel ? 0L : 5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
        await using var verify = await f.Sessions.BeginAsync(Ct);
        Assert.Equal(accepted, await verify.QuerySingleAsync(new DbStatement("SELECT payment_confirmed_at FROM shop.orders WHERE id=$1", DbValue.Uuid(a)), r => r.GetUtc(0), Ct));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE signed_amount>0 AND movement_kind<>'admin_adjustment'", Ct));
    }
    [Fact]
    public async Task LockedHeadTimesOutWithoutSkippingToSmallerOrderAndRecoversOnSameLease()
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        await Restock(f, 7);
        JobLease lease = await Claim(f);
        await using (var locked = await f.Sessions.BeginAsync(Ct))
        {
            await locked.QuerySingleAsync(new DbStatement("SELECT id FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(a)), r => r.GetGuid(0), Ct);
            var sessions = new DbSessionFactory(f.Database.Data, new() { LockTimeout = TimeSpan.FromMilliseconds(100) });
            var worker = new LocalPreorderWorker(new(sessions, f.Secrets), f.Contexts);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => worker.RunAsync(lease, Ct));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.Failure.SqlState);
            Assert.Equal(0L, await Units(f, b));
        }
        Assert.Equal(a, await Worker(f).RunAsync(lease, Ct));
        await DrainAll(f);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
    }
    [Fact]
    public async Task ConcurrentDrainJobsAllocateEachOrderOnceAndRejectCompletedLeaseReplay()
    {
        await using var f = await Fixture();
        Guid a = await Purchase(f, 0, 5), b = await Purchase(f, 1, 2);
        await Restock(f, 7);
        JobLease first = await Claim(f), second = await Claim(f);
        await Task.WhenAll(Worker(f).RunAsync(first, Ct), Worker(f).RunAsync(second, Ct));
        await DrainAll(f);
        Assert.Equal(DbFailureKind.LeaseLost, (await Assert.ThrowsAsync<DbOperationException>(() => Worker(f).RunAsync(first, Ct))).Failure.Kind);
        Assert.Equal(5L, await Units(f, a)); Assert.Equal(2L, await Units(f, b));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }
    [Fact]
    public async Task DecryptionFailureRollsBackAllUnitsOrderRevisionAndOutboxAndCanRetry()
    {
        await using var f = await Fixture();
        Guid order = await Purchase(f, 0, 5);
        await Restock(f, 5);
        Guid item; byte[] content;
        await using (var corrupt = await f.Sessions.BeginAsync(Ct))
        {
            var data = await corrupt.QuerySingleAsync(new DbStatement("SELECT id,credential_ciphertext FROM shop.inventory_items ORDER BY id DESC LIMIT 1"), r => (Id: r.GetGuid(0), Content: r.GetBinary(1)), Ct);
            item = data.Id; content = data.Content;
            await corrupt.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary([1]), DbValue.Uuid(item)), Ct);
            await corrupt.CommitAsync(Ct);
        }
        JobLease lease = await Claim(f);
        long revision = await Revision(f, order);
        await Assert.ThrowsAsync<CipherRejectedException>(() => Worker(f).RunAsync(lease, Ct));
        Assert.Equal(0L, await Units(f, order));
        Assert.Equal(revision, await Revision(f, order));
        Assert.Equal(5L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        await using (var repair = await f.Sessions.BeginAsync(Ct))
        {
            await repair.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=$1 WHERE id=$2", DbValue.Binary(content), DbValue.Uuid(item)), Ct);
            await repair.CommitAsync(Ct);
        }
        Assert.Equal(order, await Worker(f).RunAsync(lease, Ct));
        Assert.Equal(5L, await Units(f, order));
    }
    [Fact]
    public async Task MissingTrustedHeadBrandFailsClosedAndNeverUsesTheNextBrandsStock()
    {
        await using var f = await Fixture();
        await Purchase(f, 0, 5); await Purchase(f, 1, 2); await Restock(f, 7);
        JobLease lease = await Claim(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new LocalPreorderWorker(new(f.Sessions, f.Secrets), [f.Context(1)]).RunAsync(lease, Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.NotNull(await Worker(f).RunAsync(lease, Ct));
    }
    private static LocalPreorderWorker Worker(CheckoutFixture f) => new(new(f.Sessions, f.Secrets), f.Contexts);
    private static async Task<CheckoutFixture> Fixture()
    {
        var f = await CheckoutFixture.CreateAsync(stock: 0, ct: Ct);
        await using var policy = await f.Sessions.BeginAsync(Ct);
        await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), Ct);
        await policy.CommitAsync(Ct); return f;
    }
    private static async Task<Guid> Purchase(CheckoutFixture f, int user, int quantity)
    {
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Unexpected supplier call.")));
        var result = await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, quantity), (_, _, _) => Task.FromResult(ChannelMembership.Member), Ct);
        Assert.Equal(CheckoutOutcome.Purchased, result.Outcome); return result.OrderId!.Value;
    }
    private static async Task Restock(CheckoutFixture f, int count) => await new AdminCatalogStore(f.Sessions, f.Secrets).ImportAsync(await f.AdminAsync(Ct), f.Product, Enumerable.Range(0, count).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public FIFO stock " + Guid.NewGuid())).ToArray(), Guid.NewGuid(), Ct);
    private static async Task<JobLease> Claim(CheckoutFixture f, JobKind kind = JobKind.DrainLocalPreorders)
    {
        await using var claim = await f.Sessions.BeginAsync(Ct);
        JobLease? lease = await OutboxStore.ClaimAsync(claim, kind, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease); await claim.CommitAsync(Ct); return lease;
    }
    private static async Task DrainAll(CheckoutFixture f)
    {
        for (int i = 0; i < 30; i++)
        {
            JobLease? lease;
            await using (var claim = await f.Sessions.BeginAsync(Ct))
            {
                lease = await OutboxStore.ClaimAsync(claim, JobKind.DrainLocalPreorders, Guid.NewGuid(), TimeSpan.FromSeconds(30), Ct);
                await claim.CommitAsync(Ct);
            }
            if (lease is null) return;
            await Worker(f).RunAsync(lease, Ct);
        }
        throw new InvalidOperationException("Unexpected unbounded drain work.");
    }
    private static async Task<long> Units(CheckoutFixture f, Guid order)
    { await using var read = await f.Sessions.BeginAsync(Ct); return await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.fulfillments WHERE order_id=$1", DbValue.Uuid(order)), r => r.GetInt64(0), Ct); }
    private static Task<long> Revision(CheckoutFixture f, Guid order) => AdminCatalogAndResolutionTests.Revision(f, order);
}
