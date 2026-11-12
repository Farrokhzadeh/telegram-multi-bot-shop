using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class OutboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task BusinessWriteAndEnqueueRollBackTogetherAndTerminalDedupeDoesNotRearm()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid product = Guid.NewGuid(), dedupe = Guid.NewGuid();
        await using (var transaction = await sessions.BeginAsync(Ct))
        {
            await transaction.ExecuteAsync(Product(product), Ct);
            await OutboxStore.EnqueueAsync(transaction, JobIntent.Maintenance(JobKind.ActivityCleanup), dedupe, Due(), Ct);
        }
        await using (var verify = await sessions.BeginAsync(Ct))
        {
            Assert.Equal(0L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(product)), r => r.GetInt64(0), Ct));
            Assert.Equal(0L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs"), r => r.GetInt64(0), Ct));
        }
        Guid original = await EnqueueAsync(sessions, JobIntent.Maintenance(JobKind.ActivityCleanup), dedupe);
        await using (var transaction = await sessions.BeginAsync(Ct))
        {
            var lease = await OutboxStore.ClaimAsync(transaction, JobKind.ActivityCleanup, Guid.NewGuid(), LeaseTime, Ct);
            Assert.NotNull(lease);
            await OutboxStore.CompleteAsync(transaction, lease, Ct);
            await transaction.CommitAsync(Ct);
        }
        Assert.Equal(original, await EnqueueAsync(sessions, JobIntent.Maintenance(JobKind.ActivityCleanup), dedupe));
        await using var empty = await sessions.BeginAsync(Ct);
        Assert.Null(await OutboxStore.ClaimAsync(empty, JobKind.ActivityCleanup, Guid.NewGuid(), LeaseTime, Ct));
    }

    [Fact]
    public async Task ConcurrentDedupeAndClaimNeverCreateOrOwnTheSameWorkTwice()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid dedupe = Guid.NewGuid();
        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EnqueueAsync(sessions, JobIntent.Maintenance(JobKind.ActivityCleanup), dedupe)));
        Assert.Single(ids.Distinct());
        var claimed = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var transaction = await sessions.BeginAsync(Ct);
            var lease = await OutboxStore.ClaimAsync(transaction, JobKind.ActivityCleanup, Guid.NewGuid(), LeaseTime, Ct);
            await transaction.CommitAsync(Ct);
            return lease;
        }));
        Assert.Single(claimed, lease => lease is not null);
    }

    [Fact]
    public async Task ReclaimedLocalWorkRejectsOldOwnerAndRollsBackItsBusinessWrites()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        await EnqueueAsync(sessions, JobIntent.Maintenance(JobKind.ActivityCleanup), Guid.NewGuid());
        var old = await ClaimAsync(sessions, JobKind.ActivityCleanup);
        await ExpireAsync(sessions, old.Id);
        var replacement = await ClaimAsync(sessions, JobKind.ActivityCleanup);
        Assert.Equal(old.Id, replacement.Id);
        Assert.Equal(old.Attempt + 1, replacement.Attempt);
        Assert.NotEqual(old.Worker, replacement.Worker);
        Guid product = Guid.NewGuid();
        await using (var stale = await sessions.BeginAsync(Ct))
        {
            await stale.ExecuteAsync(Product(product), Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => OutboxStore.CompleteAsync(stale, old, Ct));
            Assert.Equal(DbRecovery.StopLeaseOwner, error.Failure.Recovery);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.CommitAsync(Ct));
        }
        await using var verify = await sessions.BeginAsync(Ct);
        Assert.Equal(0L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(product)), r => r.GetInt64(0), Ct));
        var renewed = await OutboxStore.RenewAsync(verify, replacement, TimeSpan.FromMinutes(1), Ct);
        Assert.True(renewed.Until > replacement.Until);
        await OutboxStore.CompleteAsync(verify, renewed, Ct);
        await verify.CommitAsync(Ct);
    }

    [Fact]
    public async Task RetryBackoffAndAttemptLimitPreserveTheOriginalJobIdentity()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid id = await EnqueueAsync(sessions, JobIntent.Maintenance(JobKind.SubscriptionCleanup), Guid.NewGuid());
        var lease = await ClaimAsync(sessions, JobKind.SubscriptionCleanup);
        await using (var retry = await sessions.BeginAsync(Ct))
        {
            await OutboxStore.RetryAsync(retry, lease, TimeSpan.FromMinutes(5), 2, JobError.Busy, Ct);
            await retry.CommitAsync(Ct);
        }
        await using (var waiting = await sessions.BeginAsync(Ct))
        {
            Assert.Null(await OutboxStore.ClaimAsync(waiting, JobKind.SubscriptionCleanup, Guid.NewGuid(), LeaseTime, Ct));
            await waiting.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET available_at=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(id)), Ct);
            await waiting.CommitAsync(Ct);
        }
        var second = await ClaimAsync(sessions, JobKind.SubscriptionCleanup);
        Assert.Equal(id, second.Id);
        Assert.Equal(2, second.Attempt);
        await using var finish = await sessions.BeginAsync(Ct);
        await OutboxStore.RetryAsync(finish, second, TimeSpan.FromSeconds(1), 2, JobError.Transient, Ct);
        Assert.Equal("failed", await finish.QuerySingleAsync(new DbStatement("SELECT status FROM shop.jobs WHERE id=$1", DbValue.Uuid(id)), r => r.GetString(0), Ct));
        Assert.Equal("attempt_limit", await finish.QuerySingleAsync(new DbStatement("SELECT last_error_code::text FROM shop.jobs WHERE id=$1", DbValue.Uuid(id)), r => r.GetString(0), Ct));
        await finish.CommitAsync(Ct);
    }

    [Fact]
    public async Task ConflictingOriginPoisonsTransactionAndSharedJobsHaveNoBrand()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), dedupe = Guid.NewGuid();
        await using (var seed = await sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(Product(first), Ct);
            await seed.ExecuteAsync(Product(second), Ct);
            await seed.CommitAsync(Ct);
        }
        await EnqueueAsync(sessions, JobIntent.Product(JobKind.EvaluateRestock, first), dedupe);
        await using var collision = await sessions.BeginAsync(Ct);
        await Assert.ThrowsAsync<DbOperationException>(() => OutboxStore.EnqueueAsync(collision, JobIntent.Product(JobKind.EvaluateRestock, second), dedupe, Due(), Ct));
        Assert.Equal(DbSessionState.Aborted, collision.State);
    }

    [Fact]
    public async Task ExpiredTelegramWorkRequiresExplicitDomainRecovery()
    {
        await using var database = await CreateAsync();
        var sessions = new DbSessionFactory(database.Data);
        Guid brand = Guid.NewGuid(), user = Guid.NewGuid(), ticket = Guid.NewGuid(), message = Guid.NewGuid(), delivery = Guid.NewGuid();
        await using (var seed = await sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id)
                VALUES ($1,'public-outbox','public outbox','public outbox','international','en','public_bot',1001,$2,'public-key')
                """, DbValue.Uuid(brand), DbValue.Binary([1])), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,1001,'en')", DbValue.Uuid(user), DbValue.Uuid(brand)), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.support_tickets(id,user_id,category,source) VALUES ($1,$2,'general','system')", DbValue.Uuid(ticket), DbValue.Uuid(user)), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.support_messages(id,ticket_id,sender_kind,body_ciphertext,body_key_id) VALUES ($1,$2,'system',$3,'public-key')", DbValue.Uuid(message), DbValue.Uuid(ticket), DbValue.Binary([1])), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,support_message_id)
                VALUES ($1,$2,'customer_dm',$3,'support_notice','send','public-outbox-delivery',$4)
                """, DbValue.Uuid(delivery), DbValue.Uuid(brand), DbValue.Uuid(user), DbValue.Uuid(message)), Ct);
            await seed.CommitAsync(Ct);
        }
        await EnqueueAsync(sessions, JobIntent.Telegram(brand, delivery), Guid.NewGuid());
        var lease = await ClaimAsync(sessions, JobKind.SendTelegram);
        await using (var denied = await sessions.BeginAsync(Ct))
            await Assert.ThrowsAsync<InvalidOperationException>(() => OutboxStore.RetryAsync(denied, lease, TimeSpan.FromSeconds(1), 3, JobError.RemoteOutcomeUnknown, Ct));
        await ExpireAsync(sessions, lease.Id);
        await using var blocked = await sessions.BeginAsync(Ct);
        Assert.Null(await OutboxStore.ClaimAsync(blocked, JobKind.SendTelegram, Guid.NewGuid(), LeaseTime, Ct));
        Assert.Equal("leased", await blocked.QuerySingleAsync(new DbStatement("SELECT status FROM shop.jobs WHERE id=$1", DbValue.Uuid(lease.Id)), r => r.GetString(0), Ct));
    }

    private static DateTime Due() => DateTime.UtcNow.AddMinutes(-1);
    private static DbStatement Product(Guid id) => new("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate','public outbox product')", DbValue.Uuid(id));
    private static async Task<Guid> EnqueueAsync(DbSessionFactory sessions, JobIntent intent, Guid dedupe)
    {
        await using var transaction = await sessions.BeginAsync(Ct);
        Guid id = await OutboxStore.EnqueueAsync(transaction, intent, dedupe, Due(), Ct);
        await transaction.CommitAsync(Ct);
        return id;
    }
    private static async Task<JobLease> ClaimAsync(DbSessionFactory sessions, JobKind kind)
    {
        await using var transaction = await sessions.BeginAsync(Ct);
        var lease = await OutboxStore.ClaimAsync(transaction, kind, Guid.NewGuid(), LeaseTime, Ct);
        Assert.NotNull(lease);
        await transaction.CommitAsync(Ct);
        return lease;
    }
    private static async Task ExpireAsync(DbSessionFactory sessions, Guid id)
    {
        await using var transaction = await sessions.BeginAsync(Ct);
        await transaction.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(id)), Ct);
        await transaction.CommitAsync(Ct);
    }
    private static async Task<DisposablePostgres> CreateAsync()
    {
        var database = await DisposablePostgres.CreateAsync(Ct);
        try { await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct); return database; }
        catch { await database.DisposeAsync(); throw; }
    }
}
