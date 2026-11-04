using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class BotActivityFloodTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ForeignOrderReferenceCausesConstraintViolation()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: Ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(Ct);
        var own = await payments.OrderAsync(ct: Ct);
        var foreign = await payments.OrderAsync(user: 1, ct: Ct);
        await using var invalid = await f.Sessions.BeginAsync(Ct);
        var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,outcome_code,idempotency_key)
            VALUES (gen_random_uuid(),$1,'checkout','callback','checkout_open',$3,$2,'accepted','wrong-brand-ref')
            """, DbValue.Uuid(f.Users[0]), DbValue.Uuid(foreign.Order), DbValue.Uuid(f.Product)), Ct));
        Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
    }
    [Fact]
    public async Task IdempotencyKeyDeduplicatesConcurrentInsertsForSameMember()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        string key = "idempotency-dedup-" + Guid.NewGuid().ToString("N");
        bool dup = false;
        using var limit = new SemaphoreSlim(4);
        int committed = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await limit.WaitAsync(Ct);
            try
            {
                await using var s = await f.Sessions.BeginAsync(Ct);
                int rows = await s.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key)
                    VALUES (gen_random_uuid(),$1,'navigation','command','catalog_open','accepted',$2)
                    ON CONFLICT(user_id,idempotency_key) DO NOTHING
                    """, DbValue.Uuid(f.Users[0]), DbValue.Text(key)), Ct);
                await s.CommitAsync(Ct);
                if (rows > 0) Interlocked.Increment(ref committed);
            }
            catch (DbOperationException) { dup = true; }
            finally { limit.Release(); }
        }));
        Assert.False(dup);
        Assert.Equal(1, committed);
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key='" + key + "'", Ct));
    }
    [Fact]
    public async Task ActivityExpiryDoesNotCascadeToDomainTablesOrInvalidateWalletFacts()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 3, balance: 100, ct: Ct);
        await using (var seed = await f.Sessions.BeginAsync(Ct))
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.True(await TelegramReplayStore.TryClaimAsync(seed, f.Context(i % 3), 100 + i, Ct));
                await seed.ExecuteAsync(new DbStatement("""
                    INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,telegram_update_id,occurred_at)
                    VALUES (gen_random_uuid(),$1,'navigation','command','catalog_open','accepted',$2,$3,CURRENT_TIMESTAMP-INTERVAL '200 days')
                    """, DbValue.Uuid(f.Users[i % 3]), DbValue.Text("expired-event-" + i), DbValue.Bigint(100 + i)), Ct);
            }
            await seed.CommitAsync(Ct);
        }
        string before;
        await using (var read = await f.Sessions.BeginAsync(Ct))
        {
            var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' ORDER BY tablename"), r => r.GetString(0), 28, Ct);
            var hashes = new List<string>();
            foreach (string t in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{t} t"), r => r.GetString(0), Ct));
            before = string.Join(':', hashes);
        }
        var cleaned = await new BotActivityRetentionStore(f.Sessions).CleanAsync(10, Ct);
        Assert.Equal(5, cleaned.Deleted);
        string after;
        await using (var read = await f.Sessions.BeginAsync(Ct))
        {
            var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' ORDER BY tablename"), r => r.GetString(0), 28, Ct);
            var hashes = new List<string>();
            foreach (string t in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{t} t"), r => r.GetString(0), Ct));
            after = string.Join(':', hashes);
        }
        Assert.Equal(before, after);
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='admin_adjustment'", Ct));
    }
    [Fact]
    public async Task EventsDoNotRateLimitByVolumeAtDatabaseLevel()
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: Ct);
        await using var seed = await f.Sessions.BeginAsync(Ct);
        for (int i = 0; i < 100; i++)
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key)
                VALUES (gen_random_uuid(),$1,'navigation','command','catalog_open','accepted',$2)
                """, DbValue.Uuid(f.Users[i % 6]), DbValue.Text("flood-key-" + i)), Ct);
        await seed.CommitAsync(Ct);
        Assert.Equal(100L, await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", Ct));
    }
    [Fact]
    public async Task ActivityEventsAreImmutableAndCannotBeDeletedWhileRecent()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var seed = await f.Sessions.BeginAsync(Ct);
        await seed.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key)
            VALUES (gen_random_uuid(),$1,'navigation','command','catalog_open','accepted','immutable-check')
            """, DbValue.Uuid(f.Users[0])), Ct);
        await seed.CommitAsync(Ct);
        bool immutable = false;
        await using (var write = await f.Sessions.BeginAsync(Ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.bot_activity_events SET outcome_code='duplicate'"), Ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { immutable = true; } }
        bool blocked = false;
        await using (var write = await f.Sessions.BeginAsync(Ct))
        { try { await write.ExecuteAsync(new DbStatement("DELETE FROM shop.bot_activity_events"), Ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { blocked = true; } }
        Assert.True(immutable && blocked);
    }
    [Fact]
    public async Task ActivityEventsHaveNoRawSecretsAndEnforceRestrictedColumnSet()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        await using var read = await f.Sessions.BeginAsync(Ct);
        long forbiddenColumns = await read.QuerySingleAsync(new DbStatement("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND table_name='bot_activity_events'
                AND (column_name IN ('body','text','message','secret','token','ciphertext','payload','data') OR data_type IN ('json','jsonb','ARRAY','bytea'))
            """), r => r.GetInt64(0), Ct);
        Assert.Equal(0L, forbiddenColumns);
        long totalColumns = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND table_name='bot_activity_events'"), r => r.GetInt64(0), Ct);
        Assert.Equal(14L, totalColumns);
    }
    [Fact]
    public async Task DiscountRevisionsAreImmutableAndTrackRedemptionStateAtRevision()
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: Ct);
        Guid coupon = Guid.NewGuid();
        await using (var issue = await f.Sessions.BeginAsync(Ct))
        {
            await new PricingStore(f.Sessions, f.Secrets).IssueAsync(issue, f.Context(0), await f.AdminAsync(Ct), new(coupon, "PUBLIC-REV-TEST", CouponKind.Promotion, 20, f.Product, f.Users[0], MaximumUses: 5), Guid.NewGuid(), Ct);
            await issue.CommitAsync(Ct);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("UPDATE shop.discount_revisions SET percent_off=50 WHERE discount_id=$1", DbValue.Uuid(coupon)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        await using (var invalid = await f.Sessions.BeginAsync(Ct))
        {
            var err = await Assert.ThrowsAsync<DbOperationException>(() => invalid.ExecuteAsync(new DbStatement("DELETE FROM shop.discount_revisions WHERE discount_id=$1", DbValue.Uuid(coupon)), Ct));
            Assert.Equal(DbFailureKind.Constraint, err.Failure.Kind);
        }
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.discount_revisions WHERE percent_off=20 AND revision_no=0", Ct));
    }
}
