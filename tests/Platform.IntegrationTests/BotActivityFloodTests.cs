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
			INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,order_id,outcome_code,idempotency_key)
			VALUES (gen_random_uuid(),$1,'interaction','callback','checkout_open',$2,'accepted','wrong-brand-ref')
			""", DbValue.Uuid(f.Users[0]), DbValue.Uuid(foreign.Order)), Ct));
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
		Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key=$1", Ct));
	}
	[Fact]
	public async Task ActivityExpiryDoesNotCascadeToDomainTablesOrInvalidateWalletFacts()
	{
		await using var f = await CheckoutFixture.CreateAsync(users: 3, balance: 100, ct: Ct);
		string before;
		await using (var read = await f.Sessions.BeginAsync(Ct))
		{
			var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' ORDER BY tablename"), r => r.GetString(0), 28, Ct);
			var hashes = new List<string>();
			foreach (string t in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{t} t"), r => r.GetString(0), Ct));
			before = string.Join(':', hashes);
		}
		await using (var seed = await f.Sessions.BeginAsync(Ct))
		{
			for (int i = 0; i < 5; i++)
				await seed.ExecuteAsync(new DbStatement("""
					INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at)
					VALUES (gen_random_uuid(),$1,'navigation','command','catalog_open','accepted',$2,CURRENT_TIMESTAMP-INTERVAL '200 days')
					""", DbValue.Uuid(f.Users[i % 3]), DbValue.Text("expired-event-" + i)), Ct);
			await seed.CommitAsync(Ct);
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
}
