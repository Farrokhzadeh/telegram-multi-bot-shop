using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CheckoutCrashAndRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CatalogStore Catalog(CheckoutFixture f) => new(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Unexpected supplier read."));
    private static WalletCheckoutService Service(CheckoutFixture f, CatalogStore? catalog = null) => new(f.Sessions, f.Store, catalog ?? Catalog(f));
    private static Task<ChannelMembership> Member(long chat, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);

    [Fact]
    public async Task FiftyPreparedBuyersAcrossThreeBrandsCommitOneWholePurchase()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 50, ct: Ct);
        var service = Service(f);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 50).Select(async user =>
        {
            await start.Task;
            try { return (await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(OrderIds.New(), f.Product, 1), Member, Ct)).Outcome == CheckoutOutcome.Purchased; }
            catch (PricingRejectedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }).ToArray();
        start.SetResult();
        Assert.Single(await Task.WhenAll(attempts), result => result);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(4990L, await f.CountAsync("SELECT sum(balance)::bigint FROM shop.wallets", Ct));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
    }

    [Fact]
    public async Task FiftyConcurrentDuplicateRequestsReturnTheOriginalOrderAcrossReconstructedServices()
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 50).Select(async _ =>
        {
            await start.Task;
            return await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, r => r.Outcome == CheckoutOutcome.Purchased);
        Assert.Equal(49, results.Count(r => r.Outcome == CheckoutOutcome.Replayed));
        Assert.All(results, r => Assert.Equal(request.OrderId, r.OrderId));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }

    [Fact]
    public async Task LostPreparedCommitReplyReconcilesOneDebitClaimAndAllocationAfterRestart()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        var admin = await f.AdminAsync(Ct);
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), admin,
                new(Guid.NewGuid(), "PUBLIC-LOST-COMMIT", CouponKind.Promotion, 50, f.Product, f.Users[0], MaximumUses: 1), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        var catalog = Catalog(f);
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 1);
        PreparedCheckout quote = await catalog.PrepareAsync(f.Context(0), CheckoutFixture.Actor(0), new(f.Product, 1, CheckoutPayment.Wallet), Ct);
        BrandPolicySnapshot policy;
        await using (var read = await f.Sessions.BeginAsync(Ct)) policy = await BrandPolicyStore.ReadAsync(read, f.Context(0), CheckoutFixture.Actor(0), Ct);
        ChannelAccess access = (await BrandPolicy.CheckChannelsAsync(policy, CheckoutFixture.Actor(0), Member, Ct))!;
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database };
        await using var proxy = new DropCommitReplyProxy(options.ConnectionString);
        await using var transport = proxy.CreateClientDataSource();
        var sessions = new DbSessionFactory(transport);
        var service = new WalletCheckoutService(sessions, new CheckoutStore(sessions, f.Secrets), catalog);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => service.AcceptAsync(f.Context(0), CheckoutFixture.Actor(0), request, quote, access, Ct));
        Assert.Equal(DbFailureKind.OutcomeUnknown, error.Failure.Kind);
        await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(CheckoutOutcome.Replayed, (await Service(f).PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, Ct)).Outcome);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit' AND signed_amount=-5", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='held' AND units_covered=1", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
    }
}
