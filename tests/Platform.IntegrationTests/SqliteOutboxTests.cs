using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class SqliteOutboxTests
{
    [Fact]
    public async Task DeduplicationAndLeaseFencesSurviveReopeningTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-outbox-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var factory = SqliteSessionFactory.ForPlaintext(path);
            Guid dedupe = Guid.NewGuid();
            var intent = JobIntent.Maintenance(JobKind.ActivityCleanup);
            var first = await Enqueue(factory);
            Assert.Equal(first, await Enqueue(SqliteSessionFactory.ForPlaintext(path)));

            var leases = await Task.WhenAll(ClaimAsync(SqliteSessionFactory.ForPlaintext(path)), ClaimAsync(SqliteSessionFactory.ForPlaintext(path)));
            var oldLease = Assert.Single(leases.OfType<JobLease>());
            Assert.Equal(first, oldLease.Id);
            Assert.Equal(1, oldLease.Attempt);

            await factory.RunAsync(true, async (connection, tx, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "UPDATE jobs SET lease_until='2000-01-01T00:00:00.0000000Z' WHERE id=$id";
                command.Parameters.AddWithValue("$id", first);
                return await command.ExecuteNonQueryAsync(token);
            }, ct);

            var reopened = SqliteSessionFactory.ForPlaintext(path);
            var current = Assert.IsType<JobLease>(await ClaimAsync(reopened));
            Assert.Equal(2, current.Attempt);
            var rejected = await Assert.ThrowsAsync<DbOperationException>(() => CompleteAsync(reopened, oldLease));
            Assert.Equal(DbFailureKind.LeaseLost, rejected.Failure.Kind);
            var renewed = await reopened.RunAsync(true, (_, tx, token) =>
                OutboxStore.RenewSqliteAsync(tx, current, TimeSpan.FromMinutes(2), token), ct);
            Assert.True(renewed.Until > current.Until);
            await reopened.RunAsync(true, async (_, tx, token) =>
            {
                await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(1), 3, JobError.Transient, token);
                return 0;
            }, ct);
            await reopened.RunAsync(true, async (connection, tx, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "UPDATE jobs SET available_at='2000-01-01T00:00:00.0000000Z' WHERE id=$id";
                command.Parameters.AddWithValue("$id", first);
                return await command.ExecuteNonQueryAsync(token);
            }, ct);
            var retried = Assert.IsType<JobLease>(await ClaimAsync(reopened));
            Assert.Equal(3, retried.Attempt);
            await CompleteAsync(reopened, retried);
            Assert.Null(await ClaimAsync(SqliteSessionFactory.ForPlaintext(path)));
            Assert.Equal(first, await Enqueue(SqliteSessionFactory.ForPlaintext(path)));

            Task<Guid> Enqueue(SqliteSessionFactory session) => session.RunAsync(true, (_, tx, token) =>
                OutboxStore.EnqueueSqliteAsync(tx, intent, dedupe, DateTime.UtcNow, token), ct);
            Task<JobLease?> ClaimAsync(SqliteSessionFactory session) => Task.Run(() => session.RunAsync(true, (_, tx, token) =>
                OutboxStore.ClaimSqliteAsync(tx, null, JobKind.ActivityCleanup, Guid.NewGuid(), TimeSpan.FromMinutes(1), token), ct), ct);
            Task<int> CompleteAsync(SqliteSessionFactory session, JobLease lease) => session.RunAsync(true, async (_, tx, token) =>
            {
                await OutboxStore.CompleteSqliteAsync(tx, lease, token);
                return 0;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
