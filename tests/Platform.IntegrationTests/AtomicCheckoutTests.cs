using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class AtomicCheckoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FiftyBuyersAcrossThreeBrandsProduceOneLastUnitPurchaseAndOneDebit()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(users: 50, ct: Ct);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CheckoutResult>[] buyers = Enumerable.Range(0, 50).Select(async i =>
        {
            await start.Task;
            return await fixture.Store.PurchaseLocalAsync(fixture.Context(i), CheckoutFixture.Actor(i), fixture.Request(), Ct);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(buyers);
        Assert.Single(results, r => r.Outcome == CheckoutOutcome.Purchased);
        Assert.Equal(49, results.Count(r => r.Outcome == CheckoutOutcome.OutOfStock));
        await ExactlyOne(fixture);
        Assert.Equal(4990L, await fixture.CountAsync("SELECT sum(balance)::bigint FROM shop.wallets", Ct));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9.99")]
    public async Task InsufficientFundsLeaveStockOrderAndDurableWorkUntouched(string balance)
    {
        await using var fixture = await CheckoutFixture.CreateAsync(balance: ExactDecimal.Parse(balance), ct: Ct);
        Assert.Equal(CheckoutOutcome.InsufficientBalance, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct)).Outcome);
        await NoPurchase(fixture);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }

    [Fact]
    public async Task ShortWholeQuantityDoesNotAllocateOrDebitEvenOneUnit()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(stock: 1, ct: Ct);
        Assert.Equal(CheckoutOutcome.OutOfStock, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(quantity: 2), Ct)).Outcome);
        await NoPurchase(fixture);
    }

    [Fact]
    public async Task FailureAfterWritesRollsBackOrderLedgerAllocationAuditAndJob()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        LocalCheckoutRequest request = fixture.Request();
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            Assert.Equal(CheckoutOutcome.Purchased, (await fixture.Store.AcceptLocalAsync(session, fixture.Context(0), CheckoutFixture.Actor(0), request, Ct)).Outcome);
            await Assert.ThrowsAsync<DbOperationException>(() => session.ExecuteAsync(new DbStatement("SELECT 1/0"), Ct));
        }
        await NoPurchase(fixture);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
        Assert.Equal(CheckoutOutcome.Purchased, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct)).Outcome);
        await ExactlyOne(fixture);
    }

    [Fact]
    public async Task CipherFailurePoisonsTheTransactionSoAPartialDebitCannotBeCommitted()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        await using (var corrupt = await fixture.Sessions.BeginAsync(Ct))
        {
            await corrupt.ExecuteAsync(new DbStatement("UPDATE shop.inventory_items SET credential_ciphertext=decode('00','hex')"), Ct);
            await corrupt.CommitAsync(Ct);
        }
        await using (var session = await fixture.Sessions.BeginAsync(Ct))
        {
            await Assert.ThrowsAsync<CipherRejectedException>(() => fixture.Store.AcceptLocalAsync(session, fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct));
            Assert.Equal(DbSessionState.Aborted, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        }
        await NoPurchase(fixture);
    }

    [Fact]
    public async Task CommittedReplayAfterServiceRestartNeverDebitsAllocatesOrEnqueuesAgain()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        LocalCheckoutRequest request = fixture.Request();
        Assert.Equal(CheckoutOutcome.Purchased, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct)).Outcome);
        var replay = await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct);
        Assert.Equal(CheckoutOutcome.Replayed, replay.Outcome);
        Assert.Equal(request.OrderId, replay.OrderId);
        await ExactlyOne(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request with { Quantity = 2 }, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.PurchaseLocalAsync(fixture.Context(1), CheckoutFixture.Actor(1), request, Ct));
        await ExactlyOne(fixture);
    }

    [Fact]
    public async Task LostCommitReplyReconcilesTheOriginalPurchaseWithoutRepeatingBusinessEffects()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new NpgsqlConnectionStringBuilder(fixture.Database.Data.ConnectionString).Database };
        await using var proxy = new DropCommitReplyProxy(options.ConnectionString);
        await using var transport = proxy.CreateClientDataSource();
        var request = fixture.Request();
        var error = await Assert.ThrowsAsync<DbOperationException>(() => new CheckoutStore(new DbSessionFactory(transport), fixture.Secrets).PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct));
        await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(DbRecovery.ReconcileBeforeRetry, error.Failure.Recovery);
        Assert.Equal(CheckoutOutcome.Replayed, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct)).Outcome);
        await ExactlyOne(fixture);
    }

    [Theory]
    [InlineData("manual", "immediate")]
    [InlineData("supplier", "immediate")]
    [InlineData("local", "local_preorder")]
    public async Task IncompleteDispatcherRoutesCannotEnterImmediateLocalDebit(string kind, string availability)
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        await using (var configure = await fixture.Sessions.BeginAsync(Ct))
        {
            await configure.ExecuteAsync(new DbStatement("UPDATE shop.products SET fulfillment_kind=$1,availability_policy=$2 WHERE id=$3", DbValue.Text(kind), DbValue.Text(availability), DbValue.Uuid(fixture.Product)), Ct);
            await configure.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.Unavailable, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct)).Outcome);
        await NoPurchase(fixture);
    }

    [Fact]
    public async Task ConcurrentSameRequestIsOnePurchaseWithOnlyReplayedFollowers()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(stock: 2, ct: Ct);
        var request = fixture.Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), request, Ct)));
        Assert.Single(results, r => r.Outcome == CheckoutOutcome.Purchased);
        Assert.Equal(9, results.Count(r => r.Outcome == CheckoutOutcome.Replayed));
        await ExactlyOne(fixture);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", Ct));
    }

    [Fact]
    public async Task AllocatedContentIsReencryptedForTheWinningBrandAndNotReleased()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        var content = await read.QuerySingleAsync(new DbStatement("SELECT id,delivered_content,delivered_content_key_id,status FROM shop.fulfillments"), row => (Id: row.GetGuid(0), Bytes: row.GetBinary(1), Key: row.GetString(2), Status: row.GetString(3)), Ct);
        var cipher = new BrandCipher(fixture.Secrets);
        byte[] plain = cipher.Decrypt(fixture.Context(0), "fulfillments", content.Id, "delivered_content", content.Key, content.Bytes);
        try { Assert.StartsWith("public synthetic credential ", System.Text.Encoding.UTF8.GetString(plain)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        Assert.Throws<CipherRejectedException>(() => cipher.Decrypt(fixture.Context(1), "fulfillments", content.Id, "delivered_content", content.Key, content.Bytes));
        Assert.Equal("acquired", content.Status);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.orders WHERE release_authorized_at IS NOT NULL", Ct));
    }

    [Fact]
    public async Task BlockedHiddenAndChangedQuotesFailWithoutPurchase()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        Assert.Equal(CheckoutOutcome.QuoteChanged, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(price: 9), Ct)).Outcome);
        await using (var hide = await fixture.Sessions.BeginAsync(Ct))
        {
            await hide.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=false WHERE brand_id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
            await hide.CommitAsync(Ct);
        }
        Assert.Equal(CheckoutOutcome.Unavailable, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct)).Outcome);
        await using (var block = await fixture.Sessions.BeginAsync(Ct))
        {
            await block.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(fixture.Users[0])), Ct);
            await block.CommitAsync(Ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct));
        await NoPurchase(fixture);
    }

    private static async Task ExactlyOne(CheckoutFixture fixture)
    {
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.orders", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.fulfillments", Ct));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", Ct));
    }
    private static async Task NoPurchase(CheckoutFixture fixture)
    {
        foreach (string table in new[] { "orders", "fulfillments", "jobs" }) Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop." + table, Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", Ct));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM shop.audit_events WHERE actor_kind='customer'", Ct));
    }
}
