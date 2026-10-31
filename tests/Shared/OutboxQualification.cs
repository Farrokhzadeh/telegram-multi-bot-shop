using Npgsql;
using Platform.Persistence;

namespace Platform.Testing;

public static class OutboxQualification
{
    public static async Task RunAsync(NpgsqlDataSource data, CancellationToken cancellationToken = default)
    {
        var sessions = new DbSessionFactory(data);
        Guid dedupe = Guid.NewGuid();
        Guid id;
        var intent = JobIntent.Maintenance(JobKind.ActivityCleanup);
        await using (var enqueue = await sessions.BeginAsync(cancellationToken))
        {
            id = await OutboxStore.EnqueueAsync(enqueue, intent, dedupe, DateTime.UtcNow.AddMinutes(-1), cancellationToken);
            if (id != await OutboxStore.EnqueueAsync(enqueue, intent, dedupe, DateTime.UtcNow, cancellationToken))
                throw new InvalidOperationException("Native job deduplication failed.");
            await enqueue.CommitAsync(cancellationToken);
        }
        JobLease original;
        await using (var claim = await sessions.BeginAsync(cancellationToken))
        {
            original = await OutboxStore.ClaimAsync(claim, JobKind.ActivityCleanup, Guid.NewGuid(), TimeSpan.FromSeconds(30), cancellationToken)
                ?? throw new InvalidOperationException("Native job claim failed.");
            if (original.Id != id || original.Intent.BrandId is not null) throw new InvalidOperationException("Native job origin failed.");
            await claim.CommitAsync(cancellationToken);
        }
        await using (var expire = await sessions.BeginAsync(cancellationToken))
        {
            await expire.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1", DbValue.Uuid(id)), cancellationToken);
            await expire.CommitAsync(cancellationToken);
        }
        JobLease replacement;
        await using (var claim = await sessions.BeginAsync(cancellationToken))
        {
            replacement = await OutboxStore.ClaimAsync(claim, JobKind.ActivityCleanup, Guid.NewGuid(), TimeSpan.FromSeconds(30), cancellationToken)
                ?? throw new InvalidOperationException("Native job reclaim failed.");
            if (replacement.Attempt != original.Attempt + 1) throw new InvalidOperationException("Native job generation failed.");
            await claim.CommitAsync(cancellationToken);
        }
        await using (var stale = await sessions.BeginAsync(cancellationToken))
        {
            bool rejected = false;
            try { await OutboxStore.CompleteAsync(stale, original, cancellationToken); }
            catch (DbOperationException error) when (error.Failure.Recovery == DbRecovery.StopLeaseOwner) { rejected = true; }
            if (!rejected || stale.State != DbSessionState.Aborted) throw new InvalidOperationException("Native stale job fence failed.");
        }
        await using (var complete = await sessions.BeginAsync(cancellationToken))
        {
            var renewed = await OutboxStore.RenewAsync(complete, replacement, TimeSpan.FromMinutes(1), cancellationToken);
            await OutboxStore.CompleteAsync(complete, renewed, cancellationToken);
            await complete.CommitAsync(cancellationToken);
        }
    }
}
