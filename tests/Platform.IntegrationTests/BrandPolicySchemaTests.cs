using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class BrandPolicySchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MembershipUniquenessAndIdentityCannotBeReassigned()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var duplicate = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => duplicate.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,7001,'en')", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(fixture.Brands[0])), Ct));
        await using (var move = await fixture.Sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<DbOperationException>(() => move.ExecuteAsync(new DbStatement("UPDATE shop.users SET brand_id=$1,telegram_id=7999 WHERE id=$2", DbValue.Uuid(fixture.Brands[1]), DbValue.Uuid(fixture.Users[0])), Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(4L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), row => row.GetInt64(0), Ct));
    }

    [Theory]
    [InlineData(-7001)]
    [InlineData(0)]
    public async Task CustomerMembershipCannotUseGroupOrInvalidDestination(long chat)
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using var write = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => write.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,$3,'en')", DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(fixture.Brands[0]), DbValue.Bigint(chat)), Ct));
    }

    [Fact]
    public async Task RequiredChannelsAreBrandScopedAndNeverCustomerDestinations()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var seed = await fixture.Sessions.BeginAsync(Ct))
        {
            foreach (Guid brand in fixture.Brands)
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_channels(brand_id,chat_id,title) VALUES ($1,-1001,$2)", DbValue.Uuid(brand), DbValue.Text(brand.ToString())), Ct);
            await seed.CommitAsync(Ct);
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        foreach (Guid brand in fixture.Brands)
            Assert.Equal(brand.ToString(), await read.QuerySingleAsync(new DbStatement("SELECT title FROM shop.brand_channels WHERE brand_id=$1 AND chat_id=-1001", DbValue.Uuid(brand)), row => row.GetString(0), Ct));
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='shop' AND tablename='telegram_sessions'"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task ChangingFlowWithoutExactlyOneRevisionAdvanceIsRejected()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        foreach (long revision in new long[] { 0, 2 })
        {
            await using var write = await fixture.Sessions.BeginAsync(Ct);
            await Assert.ThrowsAsync<DbOperationException>(() => write.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='catalog',flow_revision=$1 WHERE id=$2", DbValue.Bigint(revision), DbValue.Uuid(fixture.Users[0])), Ct));
        }
    }
}
