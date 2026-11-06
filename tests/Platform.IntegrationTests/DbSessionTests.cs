using System.Data;
using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class DbSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReconciliationSnapshotRetainsCommittedVersionAndRejectsWrites()
    {
        await using var database = await CreateAsync(); var factory = new DbSessionFactory(database.Data); Guid id = Guid.NewGuid();
        await using (var setup = await factory.BeginAsync(Ct)) { await setup.ExecuteAsync(Product(id, "public original"), Ct); await setup.CommitAsync(Ct); }
        await using var snapshot = await factory.BeginReadOnlyAsync(Ct);
        var query = new DbStatement("SELECT title::text FROM shop.products WHERE id=$1", DbValue.Uuid(id));
        Assert.Equal("repeatable read", await snapshot.QuerySingleAsync(new DbStatement("SHOW transaction_isolation"), r => r.GetString(0), Ct));
        Assert.Equal("on", await snapshot.QuerySingleAsync(new DbStatement("SHOW transaction_read_only"), r => r.GetString(0), Ct));
        Assert.Equal("public original", await snapshot.QuerySingleAsync(query, r => r.GetString(0), Ct));
        await using (var change = await factory.BeginAsync(Ct)) { await change.ExecuteAsync(new DbStatement("UPDATE shop.products SET title='public changed' WHERE id=$1", DbValue.Uuid(id)), Ct); await change.CommitAsync(Ct); }
        Assert.Equal("public original", await snapshot.QuerySingleAsync(query, r => r.GetString(0), Ct));
        var error = await Assert.ThrowsAsync<DbOperationException>(() => snapshot.ExecuteAsync(Product(Guid.NewGuid(), "public forbidden write"), Ct));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.Failure.SqlState);
    }
    [Fact]
    public async Task ParametersKeepUntrustedTextInertAndMoneyRoundtripsExactly()
    {
        await using var database = await CreateAsync();
        var factory = new DbSessionFactory(database.Data);
        string payload = "public synthetic '; COMMIT; DROP TABLE shop.products; --";
        Guid id = Guid.NewGuid();
        await using (DbSession session = await factory.BeginAsync(Ct))
        {
            await session.ExecuteAsync(Product(id, payload), Ct);
            string actual = await session.QuerySingleAsync(new DbStatement("SELECT title::text FROM shop.products WHERE id=$1", DbValue.Uuid(id)), r => r.GetString(0), Ct);
            Assert.Equal(payload, actual);
            Money amount = Money.Parse("123456789012.123456789012", Currency.Usd);
            Money mapped = await session.QuerySingleAsync(new DbStatement("SELECT $1::numeric::text", DbValue.Money(amount, Currency.Usd)),
                r => DbValue.ReadMoney(r, 0, Currency.Usd), Ct);
            Assert.Equal(amount, mapped);
            Assert.Equal("UTC", await session.QuerySingleAsync(new DbStatement("SELECT current_setting('TimeZone')"), r => r.GetString(0), Ct));
            await session.CommitAsync(Ct);
            Assert.Equal(DbSessionState.Committed, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Product(Guid.NewGuid(), "public duplicate use"), Ct));
        }
        Assert.Equal(1L, await CountAsync(database, id));
    }

    [Fact]
    public async Task DisposeAndCanceledCommandsRollBackPendingWork()
    {
        await using var database = await CreateAsync();
        var factory = new DbSessionFactory(database.Data);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        DbSession abandoned = await factory.BeginAsync(Ct);
        await abandoned.ExecuteAsync(Product(first, "public abandoned"), Ct);
        await abandoned.DisposeAsync();
        await abandoned.DisposeAsync();
        Assert.Equal(DbSessionState.RolledBack, abandoned.State);
        await using (DbSession canceled = await factory.BeginAsync(Ct))
        {
            await canceled.ExecuteAsync(Product(second, "public canceled"), Ct);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.ExecuteAsync(new DbStatement("SELECT 1"), cancellation.Token));
            Assert.Equal(DbSessionState.Aborted, canceled.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => canceled.CommitAsync(Ct));
        }
        Assert.Equal(0L, await CountAsync(database, first));
        Assert.Equal(0L, await CountAsync(database, second));
        await using (DbSession canceledCommit = await factory.BeginAsync(Ct))
        {
            await canceledCommit.ExecuteAsync(Product(third, "public canceled before commit"), Ct);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCommit.CommitAsync(cancellation.Token));
            Assert.Equal(DbSessionState.Aborted, canceledCommit.State);
        }
        Assert.Equal(0L, await CountAsync(database, third));
    }

    [Fact]
    public async Task OverPreciseDatabaseMoneyPoisonsTheSessionInsteadOfBeingRounded()
    {
        await using var database = await CreateAsync();
        await using var session = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => session.QuerySingleAsync(
            new DbStatement("SELECT '1.00000000000000000000000000001'::numeric::text"), r => DbValue.ReadMoney(r, 0, Currency.Usd), Ct));
        Assert.Equal(DbFailureKind.InvalidData, error.Failure.Kind);
        Assert.Equal(DbRecovery.Reject, error.Failure.Recovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
    }

    [Fact]
    public async Task ConstraintErrorsExposeOnlySafeMetadataAndPreventPartialCommit()
    {
        await using var database = await CreateAsync();
        Guid id = Guid.NewGuid();
        var factory = new DbSessionFactory(database.Data);
        await using (DbSession session = await factory.BeginAsync(Ct))
        {
            await session.ExecuteAsync(Product(id, "public synthetic private payload"), Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => session.ExecuteAsync(Product(id, "public synthetic private payload"), Ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.Failure.SqlState);
            Assert.Equal(DbRecovery.Reject, error.Failure.Recovery);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("public synthetic private payload", error.ToString(), StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        }
        Assert.Equal(0L, await CountAsync(database, id));
    }

    [Fact]
    public async Task DeferredCommitRejectionIsAKnownRollback()
    {
        await using var database = await CreateAsync();
        Guid brand = Guid.NewGuid(), user = Guid.NewGuid();
        var factory = new DbSessionFactory(database.Data);
        await using (var seed = await factory.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id) VALUES ($1,'public-test','public test','public test','international','en','public_test',7001,$2,'public-test-key')",
                DbValue.Uuid(brand), DbValue.Binary([1])), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,7001,'en')", DbValue.Uuid(user), DbValue.Uuid(brand)), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1)", DbValue.Uuid(user)), Ct);
            await seed.CommitAsync(Ct);
        }
        await using var session = await factory.BeginAsync(Ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=1,version=1 WHERE user_id=$1", DbValue.Uuid(user)), Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => session.CommitAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.Failure.SqlState);
        Assert.Equal(DbSessionState.RolledBack, session.State);
        Assert.Equal(DbRecovery.Reject, error.Failure.Recovery);
        await using var verify = await factory.BeginAsync(Ct);
        Assert.Equal("0", await verify.QuerySingleAsync(new DbStatement("SELECT balance::text FROM shop.wallets WHERE user_id=$1", DbValue.Uuid(user)), r => r.GetString(0), Ct));
    }

    [Fact]
    public async Task SerializableConflictRequiresANewTransactionWithoutAutomaticReplay()
    {
        await using var database = await CreateAsync();
        Guid id = Guid.NewGuid();
        await SeedAsync(database, id);
        var factory = new DbSessionFactory(database.Data, new DbSessionOptions { Isolation = IsolationLevel.Serializable });
        await using var first = await factory.BeginAsync(Ct);
        await using var second = await factory.BeginAsync(Ct);
        var read = new DbStatement("SELECT revision FROM shop.products WHERE id=$1", DbValue.Uuid(id));
        Assert.Equal(0L, await first.QuerySingleAsync(read, r => r.GetInt64(0), Ct));
        Assert.Equal(0L, await second.QuerySingleAsync(read, r => r.GetInt64(0), Ct));
        var update = new DbStatement("UPDATE shop.products SET revision=revision+1 WHERE id=$1", DbValue.Uuid(id));
        await first.ExecuteAsync(update, Ct);
        await first.CommitAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => second.ExecuteAsync(update, Ct));
        Assert.Equal("40001", error.Failure.SqlState);
        Assert.Equal(DbRecovery.RetryWholeTransactionAfterRollback, error.Failure.Recovery);
        await second.RollbackAsync(Ct);
        await using var verify = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        Assert.Equal(1L, await verify.QuerySingleAsync(read, r => r.GetInt64(0), Ct));
    }

    [Fact]
    public async Task StaleLeaseFenceStopsTheWorkerAndRollsBackOtherWrites()
    {
        await using var database = await CreateAsync();
        Guid job = Guid.NewGuid(), product = Guid.NewGuid();
        await using (var seed = await new DbSessionFactory(database.Data).BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.jobs(id,job_kind,status,locked_by,lease_until,attempts,dedupe_key) VALUES ($1,'activity_cleanup','leased',$2,CURRENT_TIMESTAMP+INTERVAL '1 minute',1,$3)",
                DbValue.Uuid(job), DbValue.Text("public-current-worker"), DbValue.Text("public-test-lease")), Ct);
            await seed.CommitAsync(Ct);
        }
        await using (var stale = await new DbSessionFactory(database.Data).BeginAsync(Ct))
        {
            await stale.ExecuteAsync(Product(product, "public stale worker write"), Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => stale.ExecuteLeaseFencedAsync(new DbStatement(
                "UPDATE shop.jobs SET status='succeeded',locked_by=NULL,lease_until=NULL,completed_at=CURRENT_TIMESTAMP WHERE id=$1 AND locked_by=$2 AND attempts=$3 AND lease_until>clock_timestamp()",
                DbValue.Uuid(job), DbValue.Text("public-old-worker"), DbValue.Bigint(1)), Ct));
            Assert.Equal(DbRecovery.StopLeaseOwner, error.Failure.Recovery);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.CommitAsync(Ct));
        }
        Assert.Equal(0L, await CountAsync(database, product));
        await using var current = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        Assert.Equal(1, await current.ExecuteLeaseFencedAsync(new DbStatement(
            "UPDATE shop.jobs SET status='succeeded',locked_by=NULL,lease_until=NULL,completed_at=CURRENT_TIMESTAMP WHERE id=$1 AND locked_by=$2 AND attempts=$3 AND lease_until>clock_timestamp()",
            DbValue.Uuid(job), DbValue.Text("public-current-worker"), DbValue.Bigint(1)), Ct));
        await current.CommitAsync(Ct);
    }

    [Fact]
    public async Task LostCommitAcknowledgementRetainsTheEffectAndRequiresReconciliation()
    {
        await using var database = await CreateAsync();
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
            ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = new NpgsqlConnectionStringBuilder(database.Data.ConnectionString).Database };
        await using var proxy = new DropCommitReplyProxy(options.ConnectionString);
        await using var transport = proxy.CreateClientDataSource();
        Guid id = Guid.NewGuid();
        await using (var session = await new DbSessionFactory(transport).BeginAsync(Ct))
        {
            await session.ExecuteAsync(Product(id, "public lost acknowledgement"), Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => session.CommitAsync(Ct));
            await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(DbRecovery.ReconcileBeforeRetry, error.Failure.Recovery);
            Assert.Equal(DbSessionState.OutcomeUnknown, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RollbackAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        }
        Assert.Equal(1L, await CountAsync(database, id)); // COMMIT actually happened; no retry callback exists.
    }

    [Fact]
    public async Task ServerLockTimeoutAbortsOnlyTheWaitingTransaction()
    {
        await using var database = await CreateAsync();
        Guid id = Guid.NewGuid(); await SeedAsync(database, id);
        await using var owner = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        var update = new DbStatement("UPDATE shop.products SET revision=revision+1 WHERE id=$1", DbValue.Uuid(id));
        await owner.ExecuteAsync(update, Ct);
        var options = new DbSessionOptions { StatementTimeout = TimeSpan.FromSeconds(2), LockTimeout = TimeSpan.FromMilliseconds(100) };
        await using var waiter = await new DbSessionFactory(database.Data, options).BeginAsync(Ct);
        var error = await Assert.ThrowsAsync<DbOperationException>(() => waiter.ExecuteAsync(update, Ct));
        Assert.Equal("55P03", error.Failure.SqlState);
        Assert.Equal(DbRecovery.RetryWholeTransactionAfterRollback, error.Failure.Recovery);
        Assert.Equal(DbSessionState.Active, owner.State);
        await waiter.RollbackAsync(Ct);
        await owner.RollbackAsync(Ct);
    }

    [Fact]
    public async Task ConnectionLossBeforeCommitCannotPersistPendingWorkOrReviveTheSession()
    {
        await using var database = await CreateAsync();
        Guid id = Guid.NewGuid();
        await using (var session = await new DbSessionFactory(database.Data).BeginAsync(Ct))
        {
            await session.ExecuteAsync(Product(id, "public lost connection"), Ct);
            int pid = await session.QuerySingleAsync(new DbStatement("SELECT pg_backend_pid()"), r => r.GetInt32(0), Ct);
            await using var terminate = database.Data.CreateCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid=$1 AND datname=$2 AND usename='shop_test_runner'");
            terminate.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = pid });
            terminate.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = new NpgsqlConnectionStringBuilder(database.Data.ConnectionString).Database! });
            Assert.True((bool)(await terminate.ExecuteScalarAsync(Ct))!);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => session.ExecuteAsync(new DbStatement("SELECT 1"), Ct));
            Assert.Equal(DbRecovery.RetryWholeTransactionAfterRollback, error.Failure.Recovery);
            Assert.Equal(DbSessionState.Aborted, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
        }
        Assert.Equal(0L, await CountAsync(database, id));
        await SeedAsync(database, id);
        Assert.Equal(1L, await CountAsync(database, id));
    }

    [Fact]
    public async Task ProjectionCannotRetainAnActiveReaderOrHideCardinalityErrors()
    {
        await using var database = await CreateAsync();
        await using var session = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        DbRow row = await session.QuerySingleAsync(new DbStatement("SELECT 'public projection'::text"), r => r, Ct);
        Assert.Throws<InvalidOperationException>(() => row.GetString(0));
        var error = await Assert.ThrowsAsync<DbOperationException>(() => session.QuerySingleAsync(
            new DbStatement("SELECT n FROM generate_series(1,2) n"), r => r.GetInt32(0), Ct));
        Assert.Equal(DbRecovery.Reject, error.Failure.Recovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(Ct));
    }

    [Fact]
    public async Task SameOwnerCannotUseAnExpiredLeaseOrAnEarlierGeneration()
    {
        await using var database = await CreateAsync();
        Guid expired = Guid.NewGuid(), newer = Guid.NewGuid();
        var factory = new DbSessionFactory(database.Data);
        await using (var seed = await factory.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.jobs(id,job_kind,status,locked_by,lease_until,attempts,dedupe_key) VALUES ($1,'activity_cleanup','leased','public-same-owner',CURRENT_TIMESTAMP-INTERVAL '1 second',1,'public-expired-lease'),($2,'activity_cleanup','leased','public-same-owner',CURRENT_TIMESTAMP+INTERVAL '1 minute',2,'public-newer-lease')",
                DbValue.Uuid(expired), DbValue.Uuid(newer)), Ct);
            await seed.CommitAsync(Ct);
        }
        foreach (Guid id in new[] { expired, newer })
        {
            await using var stale = await factory.BeginAsync(Ct);
            var error = await Assert.ThrowsAsync<DbOperationException>(() => stale.ExecuteLeaseFencedAsync(new DbStatement(
                "UPDATE shop.jobs SET status='succeeded',locked_by=NULL,lease_until=NULL,completed_at=CURRENT_TIMESTAMP WHERE id=$1 AND locked_by='public-same-owner' AND attempts=1 AND lease_until>clock_timestamp()",
                DbValue.Uuid(id)), Ct));
            Assert.Equal(DbRecovery.StopLeaseOwner, error.Failure.Recovery);
        }
    }

    [Fact]
    public async Task BoundedMultirowProjectionInvalidatesHandlesAndRejectsOverflowAtomically()
    {
        await using var database = await CreateAsync();
        var factory = new DbSessionFactory(database.Data);
        await using (var read = await factory.BeginAsync(Ct))
        {
            var rows = await read.QueryManyAsync(new DbStatement("SELECT generate_series(1,2)"), row => row, 2, Ct);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Throws<InvalidOperationException>(() => row.GetInt32(0)));
        }
        Guid product = Guid.NewGuid();
        await using (var overflow = await factory.BeginAsync(Ct))
        {
            await overflow.ExecuteAsync(Product(product, "public rollback"), Ct);
            await Assert.ThrowsAsync<DbOperationException>(() => overflow.QueryManyAsync(new DbStatement("SELECT generate_series(1,3)"), row => row.GetInt32(0), 2, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => overflow.CommitAsync(Ct));
        }
        Assert.Equal(0, await CountAsync(database, product));
    }

    private static DbStatement Product(Guid id, string title) => new(
        "INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate',$2)", DbValue.Uuid(id), DbValue.Text(title));

    private static async Task<DisposablePostgres> CreateAsync()
    {
        var database = await DisposablePostgres.CreateAsync(Ct);
        try { await SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct); return database; }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task SeedAsync(DisposablePostgres database, Guid id)
    {
        await using var session = await new DbSessionFactory(database.Data).BeginAsync(Ct);
        await session.ExecuteAsync(Product(id, "public concurrency probe"), Ct);
        await session.CommitAsync(Ct);
    }

    private static async Task<long> CountAsync(DisposablePostgres database, Guid id)
    {
        await using var command = database.Data.CreateCommand("SELECT count(*) FROM shop.products WHERE id=$1");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = id });
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
