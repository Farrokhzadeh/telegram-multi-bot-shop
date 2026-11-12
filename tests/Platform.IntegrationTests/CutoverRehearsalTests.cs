using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class CutoverRehearsalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumerExclusionEndsWithCommitOrRollbackAndDoesNotStopAnotherBrand(bool commit)
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        var sessions = new DbSessionFactory(database.Data);
        var contexts = CheckoutFixture.ResolveContexts();
        await using var owner = await sessions.BeginAsync(Ct);
        await using var contender = await sessions.BeginAsync(Ct);
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(owner, contexts[0], Ct));
        Assert.False(await BotConsumerOwnership.TryAcquireAsync(contender, contexts[0], Ct));
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(contender, contexts[1], Ct));
        if (commit) await owner.CommitAsync(Ct); else await owner.RollbackAsync(Ct);
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(contender, contexts[0], Ct));
    }
    [Fact]
    public async Task IndependentDatabasesCanBothOwnTheSameBrandConsumer()
    {
        await using var oldDatabase = await DisposablePostgres.CreateAsync(Ct);
        await using var newDatabase = await DisposablePostgres.CreateAsync(Ct);
        var context = CheckoutFixture.ResolveContexts()[0];
        await using var oldWriter = await new DbSessionFactory(oldDatabase.Data).BeginAsync(Ct);
        await using var newWriter = await new DbSessionFactory(newDatabase.Data).BeginAsync(Ct);
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(oldWriter, context, Ct));
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(newWriter, context, Ct));
    }
    [Fact]
    public async Task ConsumerOwnershipDoesNotFenceAnotherStockWriter()
    {
        await using var fixture = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var consumer = await fixture.Sessions.BeginAsync(Ct);
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(consumer, fixture.Context(0), Ct));
        Assert.Equal(CheckoutOutcome.Purchased, (await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), Ct)).Outcome);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", Ct));
    }
    [Fact]
    public async Task LostConsumerConnectionReleasesOwnershipWithoutADurableHandoff()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        var sessions = new DbSessionFactory(database.Data);
        var context = CheckoutFixture.ResolveContexts()[0];
        await using var owner = await sessions.BeginAsync(Ct);
        await using var contender = await sessions.BeginAsync(Ct);
        int pid = await owner.QuerySingleAsync(new DbStatement("SELECT pg_backend_pid()"), row => row.GetInt32(0), Ct);
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(owner, context, Ct));
        Assert.False(await BotConsumerOwnership.TryAcquireAsync(contender, context, Ct));
        Assert.True(await contender.QuerySingleAsync(new DbStatement("SELECT pg_terminate_backend($1,5000)", DbValue.Integer(pid)), row => row.GetBoolean(0), Ct));
        Assert.True(await BotConsumerOwnership.TryAcquireAsync(contender, context, Ct));
    }
}

