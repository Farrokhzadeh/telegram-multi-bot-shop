using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class BrandIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameTelegramIdentityHasThreeIndependentMembershipsAndSettings()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using (var update = await fixture.Sessions.BeginAsync(Ct))
        {
            await update.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=CURRENT_TIMESTAMP,marketing_opt_out_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(fixture.Users[0])), Ct);
            await update.CommitAsync(Ct);
        }
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        for (int i = 0; i < 3; i++)
        {
            var member = await read.QuerySingleAsync(new DbStatement("""
                SELECT u.id,u.language,u.blocked_at IS NOT NULL,u.marketing_opt_out_at IS NOT NULL,w.balance::text
                FROM shop.users u JOIN shop.wallets w ON w.user_id=u.id WHERE u.brand_id=$1 AND u.telegram_id=7001
                """, DbValue.Uuid(fixture.Brands[i])), row => (row.GetGuid(0), row.GetString(1), row.GetBoolean(2), row.GetBoolean(3), row.GetString(4)), Ct);
            Assert.Equal((fixture.Users[i], i == 0 ? "fa" : "en", i == 0, i == 0, "0"), member);
        }
    }

    [Fact]
    public async Task EveryForeignOrderAndSameBrandOtherMemberAreRejectedAsFlowTargets()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (i == j) continue;
                await RejectFlow(fixture, fixture.Users[i], fixture.Orders[j]);
            }
        }
        await RejectFlow(fixture, fixture.OtherUser, fixture.Orders[0]);
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users WHERE flow_revision<>0 OR flow_order_id IS NOT NULL"), row => row.GetInt64(0), Ct));
    }

    [Fact]
    public async Task ForeignListingCannotEnterCustomerFlowAndFailureRollsBack()
    {
        await using var fixture = await BrandFixture.CreateAsync(Ct);
        await using var write = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => write.ExecuteAsync(new DbStatement("UPDATE shop.users SET language='fa',flow_state='quantity',flow_product_id=$1,flow_revision=flow_revision+1 WHERE id=$2",
            DbValue.Uuid(fixture.PrivateProduct), DbValue.Uuid(fixture.Users[2])), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(Ct));
        await using var read = await fixture.Sessions.BeginAsync(Ct);
        Assert.Equal("en", await read.QuerySingleAsync(new DbStatement("SELECT language FROM shop.users WHERE id=$1", DbValue.Uuid(fixture.Users[2])), row => row.GetString(0), Ct));
    }

    private static async Task RejectFlow(BrandFixture fixture, Guid user, Guid order)
    {
        await using var write = await fixture.Sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => write.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='payment',flow_order_id=$1,flow_revision=flow_revision+1 WHERE id=$2",
            DbValue.Uuid(order), DbValue.Uuid(user)), Ct));
    }
}
