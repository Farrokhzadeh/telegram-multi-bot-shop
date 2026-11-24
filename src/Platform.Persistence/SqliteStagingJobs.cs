using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteStatusDispatch(Guid DeliveryId, Guid OrderId, long ChatId, long? TargetMessageId, string Text);

public sealed class SqliteStagingJobs(SqliteSessionFactory sessions)
{
    public Task<JobLease?> ClaimAsync(BrandContext context, JobKind kind, Guid worker, CancellationToken ct = default)
    {
        if (kind is not (JobKind.SendTelegram or JobKind.PrepareManifest))
            throw new UnauthorizedAccessException("The staging worker must not execute supplier or settlement jobs.");
        return sessions.RunAsync(true, (_, tx, token) =>
            OutboxStore.ClaimSqliteAsync(tx, context.BrandId, kind, worker, TimeSpan.FromSeconds(90), token), ct);
    }

    public Task RetryPrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.PrepareManifest || lease.Intent.BrandId != context.BrandId)
            throw new UnauthorizedAccessException();
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await OutboxStore.RetrySqliteAsync(tx, lease, TimeSpan.FromSeconds(10), 100,
                JobError.Busy, token);
            return 0;
        }, ct);
    }

    public Task<string> PurposeAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.SendTelegram || lease.Intent.BrandId != context.BrandId ||
            lease.Intent.OriginId is not Guid id)
            throw new UnauthorizedAccessException();
        return sessions.RunAsync(false, async (_, tx, token) =>
        {
            return await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.TelegramDelivery>().Where(row => row.Id == id && row.BrandId == context.BrandId).Select(row => row.Purpose).SingleOrDefaultAsync(cancel), token) ?? throw new UnauthorizedAccessException("Staging Telegram purpose is unavailable.");
        }, ct);
    }

    public Task<Guid[]> ExpiredCredentialsAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            return await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from d in data.Data.Set<Entities.TelegramDelivery>() join j in data.Data.Set<Entities.Job>() on d.Id equals j.TelegramDeliveryId where d.BrandId == context.BrandId && d.Purpose == "order_credentials" && d.Status == "leased" && j.Status == "leased" && j.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow() orderby j.LeaseUntil, j.Id select d.Id).Take(50).ToArrayAsync(cancel), token);
        }, ct);

    public Task<int> PendingAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            return await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Job>().CountAsync(row => row.BrandId == context.BrandId && (row.Status == "pending" || row.Status == "retry"), cancel), token);
        }, ct);
}

public sealed class SqliteStatusDeliveryStore(SqliteSessionFactory sessions)
{
    public Task<SqliteStatusDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid id = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, id, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            if (state.Status == "succeeded")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (SqliteStatusDispatch?)null;
            }
            if (state.Status is not ("pending" or "retry") ||
                (state.Action == "edit" && state.Target is null) ||
                state.Action is not ("send" or "edit") ||
                (state.Action == "send" && state.Target is not null))
                throw new UnauthorizedAccessException("Status dispatch is not authorized.");
            if (state.Action == "edit")
            {
                bool anchor = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.TelegramDelivery>().AnyAsync(row => row.BrandId == context.BrandId && row.OrderId == state.Order && row.UserId == state.Member && row.Purpose == "order_status" && row.TelegramMessageId == state.Target && row.Status == "succeeded", cancel), token);
                if (!anchor) throw new UnauthorizedAccessException("Status edit anchor is unavailable.");
            }
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) => { await data.RequireJobCommandAsync(context.BrandId, cancel); var row = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(row => row.Id == id && (row.Status == "pending" || row.Status == "retry"), cancel) ?? throw new UnauthorizedAccessException(); row.Status = "leased"; row.Attempts = renewed.Attempt; row.LastAttemptAt = DateTime.UtcNow; return true; }, token);
            string text = "Akanti staging order\n" + state.Title + "\nPayment: " + state.Payment +
                "\nDelivery: " + state.Delivery + "\nOrder: " + state.Order.ToString("N")[..12];
            if (text.Length > 4000) throw new UnauthorizedAccessException("Status message is too large.");
            return new SqliteStatusDispatch(id, state.Order, state.Telegram, state.Target, text);
        }, ct);
    }

    public Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome,
        CancellationToken ct = default)
    {
        Guid id = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, id, token);
            if (state.Status == "succeeded") return 0;
            if (state.Status != "leased" || state.Attempts != lease.Attempt)
                throw new UnauthorizedAccessException("Status delivery lease was superseded.");
            bool success = outcome.Kind == DeliveryOutcomeKind.Succeeded && outcome.MessageId is > 0 &&
                (state.Target is null || state.Target == outcome.MessageId);
            bool retry = !success && state.Target is not null &&
                outcome.Kind is (DeliveryOutcomeKind.Retry or DeliveryOutcomeKind.Unknown);
            string deliveryState = success ? "succeeded" : retry ? "retry" : outcome.Kind == DeliveryOutcomeKind.Unknown ? "uncertain" : "failed";
            string jobState = success ? "succeeded" : retry ? "retry" : "failed";
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await data.RequireJobCommandAsync(context.BrandId, cancel);
                var job = await data.Data.Set<Entities.Job>().SingleOrDefaultAsync(row => row.Id == lease.Id && row.BrandId == context.BrandId && row.TelegramDeliveryId == id && row.Status == "leased" && row.Attempts == lease.Attempt && row.LockedBy == lease.Worker.ToString("D") && row.LeaseUntil > Platform.Persistence.Configurations.EfValues.UtcNow(), cancel) ?? throw new UnauthorizedAccessException("Status job lease expired.");
                var row = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(row => row.Id == id && row.Status == "leased" && row.Attempts == lease.Attempt, cancel) ?? throw new UnauthorizedAccessException("Status dispatch state changed.");
                DateTime now = DateTime.UtcNow; string code = success ? "known_success" : retry ? "idempotent_edit_retry" : "remote_outcome_unknown";
                job.Status = jobState; job.LockedBy = null; job.LeaseUntil = null; job.AvailableAt = now.AddSeconds(5); job.CompletedAt = retry ? null : now; job.LastErrorCode = code;
                row.Status = deliveryState; row.TelegramMessageId = success ? outcome.MessageId : null; row.SentAt = success ? now : null; row.LastErrorCode = code; row.NextAttemptAt = now.AddSeconds(5); return true;
            }, token);
            if (success && (state.Payment is "paid" or "not_required") && state.Delivery == "acquiring")
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Order(JobKind.PrepareManifest,
                    context.BrandId, state.Order), state.Order, DateTime.UtcNow, token);
            return 0;
        }, ct);
    }

    public Task RecoverExpiredAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(true, async (_, tx, token) =>
        {
            var expired = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from d in data.Data.Set<Entities.TelegramDelivery>() join j in data.Data.Set<Entities.Job>() on d.Id equals j.TelegramDeliveryId where d.BrandId == context.BrandId && d.Purpose == "order_status" && d.Status == "leased" && j.Status == "leased" && j.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow() orderby d.Id select new { d.Id, Edit = d.TargetMessageId != null }).Take(200).ToListAsync(cancel), token);
            foreach (var item in expired)
            {
                await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
                {
                    await data.RequireJobCommandAsync(context.BrandId, cancel);
                    var jobs = await data.Data.Set<Entities.Job>().Where(row => row.TelegramDeliveryId == item.Id && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow()).Take(1001).ToListAsync(cancel);
                    if (jobs.Count > 1000) data.RejectScope("Status job bound exceeded.");
                    DateTime now = DateTime.UtcNow; foreach (var job in jobs) { job.Status = item.Edit ? "retry" : "failed"; job.LockedBy = null; job.LeaseUntil = null; job.AvailableAt = now; job.CompletedAt = item.Edit ? null : now; job.LastErrorCode = "remote_outcome_unknown"; }
                    if (jobs.Count == 1) { var row = await data.Data.Set<Entities.TelegramDelivery>().SingleOrDefaultAsync(row => row.Id == item.Id && row.Status == "leased", cancel) ?? throw new UnauthorizedAccessException(); row.Status = item.Edit ? "retry" : "uncertain"; row.LastErrorCode = "remote_outcome_unknown"; }
                    return true;
                }, token);
            }
            return expired.Count;
        }, ct);

    private static Guid Require(BrandContext context, JobLease lease) =>
        lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId &&
        lease.Intent.OriginId is Guid id && id != Guid.Empty ? id :
        throw new UnauthorizedAccessException("Telegram job scope is unavailable.");

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid id, CancellationToken ct)
    {
        var result = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => (from d in data.Data.Set<Entities.TelegramDelivery>() join u in data.Data.Set<Entities.User>() on d.UserId equals u.Id join o in data.Data.Set<Entities.Order>() on d.OrderId equals o.Id join b in data.Data.Set<Entities.Brand>() on d.BrandId equals b.Id where d.Id == id && d.BrandId == context.BrandId && u.BrandId == context.BrandId && o.UserId == d.UserId && d.Purpose == "order_status" && d.RecipientKind == "customer_dm" && b.BotId == context.BotId && b.Active && u.BlockedAt == null && u.BannedAt == null select new State(o.Id, u.Id, u.TelegramId, d.TargetMessageId, d.Action, d.Status, d.Attempts, o.ProductTitleSnapshot, o.PaymentStatus, o.FulfillmentStatus)).SingleOrDefaultAsync(token), ct);
        return result ?? throw new UnauthorizedAccessException("Telegram status delivery is unavailable.");
    }

    private sealed record State(Guid Order, Guid Member, long Telegram, long? Target, string Action, string Status,
        long Attempts, string Title, string Payment, string Delivery);
}
