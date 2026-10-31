using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class BrandUserStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentStartsCreateOneMembershipAndKeepItsOwnPreferences()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        Guid[] ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var start = await fixture.Sessions.BeginAsync(Ct);
            BrandUser user = await BrandUserStore.ObservePrivateAsync(start, fixture.Contexts[0], 8001, Ct);
            await start.CommitAsync(Ct);
            return user.Id;
        }));
        Assert.Single(ids.Distinct());
        await using (var update = await fixture.Sessions.BeginAsync(Ct))
        {
            await BrandUserStore.SetPreferencesAsync(update, fixture.Contexts[0], 8001, ids[0], "en", true, Ct);
            await update.CommitAsync(Ct);
        }
        await using var repeat = await fixture.Sessions.BeginAsync(Ct);
        BrandUser existing = await BrandUserStore.ObservePrivateAsync(repeat, fixture.Contexts[0], 8001, Ct);
        Assert.Equal(ids[0], existing.Id);
        Assert.Equal("en", existing.Language);
        Assert.True(existing.MarketingOptOut);
        Assert.Null(await BrandUserStore.FindAsync(repeat, fixture.Contexts[1], 8001, Ct));
        Assert.Equal(3L, await repeat.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallets"), row => row.GetInt64(0), Ct));
        await repeat.CommitAsync(Ct);
    }

    [Fact]
    public async Task ForeignMembershipAndOtherActorDenyAndPoisonEarlierWrites()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        foreach (Guid target in new[] { fixture.Users[1], fixture.OtherUser })
        {
            Guid product = Guid.NewGuid();
            await using (var write = await fixture.Sessions.BeginAsync(Ct))
            {
                await write.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'manual','immediate','public rollback')", DbValue.Uuid(product)), Ct);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.SetPreferencesAsync(write, fixture.Contexts[0], 7001, target, "fa", true, Ct));
                await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(Ct));
            }
            await using var read = await fixture.Sessions.BeginAsync(Ct);
            Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(product)), row => row.GetInt64(0), Ct));
        }
    }

    [Fact]
    public async Task CompetingCallbacksAndReplayAdvanceTheFlowOnlyOnce()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        bool[] accepted = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var write = await fixture.Sessions.BeginAsync(Ct);
            try
            {
                await BrandUserStore.TransitionAsync(write, fixture.Contexts[0], 7001, fixture.Users[0], 0,
                    CustomerFlowState.Quantity, productId: fixture.Product, anchorMessageId: 41, ct: Ct);
                await write.CommitAsync(Ct);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
        }));
        Assert.Single(accepted, value => value);
        await using var replay = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.TransitionAsync(replay, fixture.Contexts[0], 7001,
            fixture.Users[0], 0, CustomerFlowState.Idle, ct: Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        BrandUser member = await BrandUserStore.RequireAsync(read, fixture.Contexts[0], 7001, fixture.Users[0], Ct);
        Assert.Equal(1, member.Flow.Revision);
        Assert.Equal(fixture.Product, member.Flow.ProductId);
        Assert.Equal(41, member.Flow.AnchorMessageId);
        Assert.Equal(CustomerFlowState.Idle, (await BrandUserStore.RequireAsync(read, fixture.Contexts[1], 7001, fixture.Users[1], Ct)).Flow.State);
    }

    [Fact]
    public async Task FlowReferencesAreCheckedAgainstCurrentMemberAndBrand()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var order = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => BrandUserStore.TransitionAsync(order, fixture.Contexts[0], 7001, fixture.Users[0], 0,
                CustomerFlowState.Payment, orderId: fixture.Orders[1], ct: Ct));
        await using (var listing = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => BrandUserStore.TransitionAsync(listing, fixture.Contexts[0], 7001, fixture.Users[0], 0,
                CustomerFlowState.Quantity, productId: fixture.PrivateProduct, ct: Ct));
        await using var correct = await fixture.Sessions.BeginAsync(Ct);
        BrandUser member = await BrandUserStore.TransitionAsync(correct, fixture.Contexts[0], 7001, fixture.Users[0], 0,
            CustomerFlowState.Payment, fixture.Orders[0], fixture.Product, 42, DateTime.UtcNow.AddMinutes(5), Ct);
        Assert.Equal(fixture.Orders[0], member.Flow.OrderId);
        await correct.CommitAsync(Ct);
    }

    [Fact]
    public async Task ExpiredFlowCannotBeContinuedButCanBeClearedWithItsCurrentRevision()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='quantity',flow_revision=1,flow_product_id=$1,flow_expires_at=clock_timestamp()-INTERVAL '1 second' WHERE id=$2", DbValue.Uuid(fixture.Product), DbValue.Uuid(fixture.Users[0])), Ct);
            await seed.CommitAsync(Ct);
        }
        await using (var stale = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.TransitionAsync(stale, fixture.Contexts[0], 7001, fixture.Users[0], 1, CustomerFlowState.Payment, orderId: fixture.Orders[0], ct: Ct));
        await using var clear = await fixture.Sessions.BeginAsync(Ct);
        BrandUser idle = await BrandUserStore.TransitionAsync(clear, fixture.Contexts[0], 7001, fixture.Users[0], 1, CustomerFlowState.Idle, ct: Ct);
        Assert.Equal(2, idle.Flow.Revision);
        Assert.Null(idle.Flow.ProductId);
        Assert.Null(idle.Flow.ExpiresAt);
        await clear.CommitAsync(Ct);
    }

    [Theory]
    [InlineData("blocked_at")]
    [InlineData("banned_at")]
    public async Task UnreachableMembershipCannotMutateFlowOrResolveDestination(string field)
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var disable = await fixture.Sessions.BeginAsync(Ct))
        {
            await disable.ExecuteAsync(new DbStatement("UPDATE shop.users SET " + field + "=clock_timestamp() WHERE id=$1", DbValue.Uuid(fixture.Users[0])), Ct);
            await disable.CommitAsync(Ct);
        }
        await using (var target = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.ResolvePrivateDestinationAsync(target, fixture.Contexts[0], fixture.Users[0], Ct));
        await using var flow = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.TransitionAsync(flow, fixture.Contexts[0], 7001, fixture.Users[0], 0, CustomerFlowState.Catalog, ct: Ct));
    }

    [Fact]
    public async Task DestinationRequiresExistingMembershipInTheCorrectActiveBot()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var read = await fixture.Sessions.BeginAsync(Ct))
            Assert.Equal(7001, await BrandUserStore.ResolvePrivateDestinationAsync(read, fixture.Contexts[0], fixture.Users[0], Ct));
        foreach (Guid target in new[] { fixture.Users[1], Guid.NewGuid() })
        {
            await using var wrong = await fixture.Sessions.BeginAsync(Ct);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.ResolvePrivateDestinationAsync(wrong, fixture.Contexts[0], target, Ct));
        }
        await using (var disable = await fixture.Sessions.BeginAsync(Ct))
        {
            await disable.ExecuteAsync(new DbStatement("UPDATE shop.brands SET active=false WHERE id=$1", DbValue.Uuid(fixture.Brands[0])), Ct);
            await disable.CommitAsync(Ct);
        }
        await using var stopped = await fixture.Sessions.BeginAsync(Ct);
        Assert.Null(await BrandUserStore.FindAsync(stopped, fixture.Contexts[0], 7001, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BrandUserStore.ObservePrivateAsync(stopped, fixture.Contexts[0], 8001, Ct));
    }
}
