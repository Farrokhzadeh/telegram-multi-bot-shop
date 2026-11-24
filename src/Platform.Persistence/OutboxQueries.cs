using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal async Task<Guid[]> LockAudienceMembersAsync(IQueryable<Guid> eligible, CancellationToken ct)
    {
        var query = Data.Set<Entities.User>().Where(row => eligible.Contains(row.Id)).OrderBy(row => row.Id).Take(1000).Select(row => row.Id); await LockRowsAsync(query, EfRowLock.Update, ct); return await query.ToArrayAsync(ct);
    }
    internal Task<int> LockPublishedProductAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Product>().Where(row => row.Id == id).Select(row => row.Id), EfRowLock.Share, ct);
    internal Task<int> LockSupportTicketAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.SupportTicket>().Where(row => row.Id == id).Select(row => row.Id), EfRowLock.Update, ct);
    internal Task<int> LockCampaignBatchAsync(Guid batch, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Campaign>().Where(row => row.BatchId == batch).OrderBy(row => row.Id).Select(row => row.Id), EfRowLock.Update, ct);
    internal Task<int> LockCampaignAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.Campaign>().Where(row => row.Id == id).Select(row => row.Id), EfRowLock.Update, ct);
    internal Task<int> LockNoticeMemberAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.User>().Where(row => row.Id == id).Select(row => row.Id), EfRowLock.Update, ct);
    internal Task<int> LockNoticeSubscriptionAsync(Guid id, CancellationToken ct) => LockRowsAsync(Data.Set<Entities.ProductStockSubscription>().Where(row => row.Id == id).Select(row => row.Id), EfRowLock.Update, ct);
    internal async Task<JobLease?> ClaimNoticeAsync(Guid? brand, Guid worker, string[] purposes, CancellationToken ct)
    {
        if (Integrity.RowLock != EfRowLock.None) throw Denied("Row locks cannot overlap.");
        Guid? id;
        try { Integrity.RowLock = EfRowLock.UpdateSkipLocked; id = await Data.Set<Entities.Job>().Where(row => (brand == null || row.BrandId == brand) && row.JobKind == "send_telegram" && (row.Status == "pending" || row.Status == "retry") && row.AvailableAt <= EfValues.UtcNow() && Data.Set<Entities.TelegramDelivery>().Any(d => d.Id == row.TelegramDeliveryId && (d.Status == "pending" || d.Status == "retry") && d.NextAttemptAt <= EfValues.UtcNow() && purposes.Contains(d.Purpose))).OrderBy(row => row.AvailableAt).ThenBy(row => row.Id).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(ct); }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
        if (id == null) return null;
        var row = await Data.Set<Entities.Job>().SingleAsync(row => row.Id == id, ct); await RequireJobCommandAsync(row.BrandId, ct);
        DateTime now = await ClockAsync(Data.Set<Entities.Job>().Where(value => value.Id == row.Id), ct); row.Status = "leased"; row.LockedBy = worker.ToString("D"); row.LeaseUntil = now.AddMinutes(1); row.Attempts = checked(row.Attempts + 1); return new(row.Id, OutboxQueries.Intent(row), worker, row.Attempts, row.LeaseUntil.Value);
    }
    internal async Task<Guid[]> CleanupNoticeSubscriptionsAsync(bool activeOnly, CancellationToken ct)
    {
        await RequireJobCommandAsync(null, ct);
        DateTime cutoff = (await ClockAsync(Data.Set<Entities.Job>().Where(row => row.JobKind == "subscription_cleanup"), ct)).AddHours(-4320);
        if (Integrity.RowLock != EfRowLock.None) throw Denied("Row locks cannot overlap.");
        Guid[] ids;
        try
        {
            Integrity.RowLock = EfRowLock.UpdateSkipLocked;
            ids = await Data.Set<Entities.ProductStockSubscription>().Where(s => (!activeOnly || Data.Set<Entities.User>().Any(u => u.Id == s.UserId && Data.Set<Entities.Brand>().Any(b => b.Id == u.BrandId && b.Active))) && s.ClosedAt < cutoff && !Data.Set<Entities.TelegramDelivery>().Any(d => d.ProductStockSubscriptionId == s.Id && (d.Purpose != "restock_notice" || d.Status != "succeeded" && d.Status != "failed" && d.Status != "cancelled")) && !Data.Set<Entities.Job>().Any(j => Data.Set<Entities.TelegramDelivery>().Any(d => d.Id == j.TelegramDeliveryId && d.ProductStockSubscriptionId == s.Id) && j.Status != "succeeded" && j.Status != "failed" && j.Status != "cancelled")).OrderBy(s => s.Id).Select(s => s.Id).Take(100).ToArrayAsync(ct);
        }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
        foreach (Guid id in ids)
        {
            var deliveries = Data.Set<Entities.TelegramDelivery>().Where(d => d.ProductStockSubscriptionId == id && d.Purpose == "restock_notice" && (d.Status == "succeeded" || d.Status == "failed" || d.Status == "cancelled"));
            while (true) { var jobs = await Data.Set<Entities.Job>().Where(j => deliveries.Any(d => d.Id == j.TelegramDeliveryId)).Take(128).ToListAsync(ct); if (jobs.Count == 0) break; Data.RemoveRange(jobs); await FlushAsync(ct, false); }
            while (true) { var batch = await deliveries.Take(128).ToListAsync(ct); if (batch.Count == 0) break; Data.RemoveRange(batch); await FlushAsync(ct, false); }
            var row = await Data.Set<Entities.ProductStockSubscription>().SingleAsync(row => row.Id == id, ct); Data.Remove(row); await FlushAsync(ct, false);
        }
        return ids;
    }
    internal async Task RequireJobCommandAsync(Guid? brand, CancellationToken ct)
    {
        if (!write) throw Denied("A writable job transaction is required.");
        if (writer is not null)
        {
            if (writer.Shared && !sharedAdmitted) await RequireSharedWritersAsync(ct, true);
            if (brand is Guid id)
            {
                var registration = writer.Entries.SingleOrDefault(value => value.Context.BrandId == id) ?? throw Denied("Job writer scope is unavailable.");
                await RequireRegisteredRevisionAsync(registration.Context, registration.Revision, ct);
            }
            else if (!writer.Shared) throw Denied("Shared job writer scope is unavailable.");
        }
        admitted = true;
    }
    internal async Task<JobLease?> ClaimJobAsync(Guid? brand, bool exactBrand, bool globalOnly, JobKind kind, Guid worker, int seconds, CancellationToken ct)
    {
        string code = JobIntent.CodeOf(kind);
        bool recover = kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier or JobKind.SendTelegram);
        if (Integrity.RowLock != EfRowLock.None) throw Denied("Row locks cannot overlap.");
        Guid? selected;
        try
        {
            if (nativeWriterBoundary) Integrity.RowLock = EfRowLock.UpdateSkipLocked;
            selected = await Data.Set<Entities.Job>().Where(row => row.JobKind == code && (exactBrand ? row.BrandId == brand : brand == null || row.BrandId == brand) && (!globalOnly || row.BrandId == null) && row.AvailableAt <= EfValues.UtcNow() && row.Attempts < long.MaxValue && (row.Status == "pending" || row.Status == "retry" || recover && row.Status == "leased" && row.LeaseUntil <= EfValues.UtcNow()) && (code != "submit_supplier" || Data.Set<Entities.SupplierRequest>().Any(request => request.Id == row.SupplierRequestId && request.Status == "pending")) && (row.OriginRevision == null || Data.Set<Entities.SupplierRequest>().Any(request => request.Id == row.SupplierRequestId && request.Revision == row.OriginRevision))).OrderBy(row => row.AvailableAt).ThenBy(row => row.Id).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(ct);
        }
        catch { Integrity.Reject(); throw; }
        finally { Integrity.RowLock = EfRowLock.None; }
        if (selected is null) return null;
        var row = await Data.Set<Entities.Job>().SingleAsync(row => row.Id == selected, ct);
        await RequireJobCommandAsync(row.BrandId, ct);
        DateTime now = await ClockAsync(Data.Set<Entities.Job>().Where(value => value.Id == row.Id), ct);
        row.Status = "leased"; row.LockedBy = worker.ToString("D"); row.LeaseUntil = now.AddSeconds(seconds); row.Attempts = checked(row.Attempts + 1);
        return new(row.Id, OutboxQueries.Intent(row), worker, row.Attempts, row.LeaseUntil.Value);
    }
    internal async Task<Entities.Job> RequireJobLeaseAsync(JobLease lease, bool matchBrand, CancellationToken ct)
    {
        await RequireJobCommandAsync(lease.Intent.BrandId, ct); await LockJobAsync(lease.Id, ct);
        string worker = lease.Worker.ToString("D");
        var row = await Data.Set<Entities.Job>().SingleOrDefaultAsync(row => row.Id == lease.Id && (!matchBrand || row.BrandId == lease.Intent.BrandId) && row.Status == "leased" && row.LockedBy == worker && row.Attempts == lease.Attempt && row.LeaseUntil > EfValues.UtcNow() && (row.OriginRevision == null || Data.Set<Entities.SupplierRequest>().Any(request => request.Id == row.SupplierRequestId && request.Revision == row.OriginRevision)), ct);
        if (row is null) { Integrity.Reject(); throw new DbOperationException(new(DbFailureKind.LeaseLost, DbRecovery.StopLeaseOwner)); }
        return row;
    }
}
internal static class OutboxQueries
{
    internal static JobIntent Intent(Entities.Job row) => JobIntent.Read(row.JobKind, row.BrandId, row.PaymentId ?? row.OrderId ?? row.FulfillmentId ?? row.CampaignId ?? row.DiscountId ?? row.SupportMessageId ?? row.TelegramDeliveryId ?? row.ProductStockSubscriptionId ?? row.SupplierRequestId ?? row.ProductId ?? row.ProductSupplierSourceId, row.AuditEventId, row.OriginRevision);
    internal static async Task<Guid> EnqueueAsync(EfSession data, JobIntent intent, string dedupe, DateTime available, CancellationToken ct)
    {
        var existing = await data.Data.Set<Entities.Job>().SingleOrDefaultAsync(row => row.DedupeKey == dedupe, ct);
        if (existing is not null) { if (Intent(existing) != intent) data.RejectConflict("Job identity conflicts with its immutable origin."); return existing.Id; }
        await data.RequireJobCommandAsync(intent.BrandId, ct);
        Guid id = Guid.NewGuid(); data.Data.Add(new Entities.Job { Id = id, BrandId = intent.BrandId, JobKind = intent.Code, PaymentId = Origin(JobOrigin.Payment), OrderId = Origin(JobOrigin.Order), CampaignId = Origin(JobOrigin.Campaign), DiscountId = Origin(JobOrigin.Discount), SupportMessageId = Origin(JobOrigin.Support), TelegramDeliveryId = Origin(JobOrigin.Telegram), SupplierRequestId = Origin(JobOrigin.Supplier), ProductId = Origin(JobOrigin.Product), ProductSupplierSourceId = Origin(JobOrigin.Source), AuditEventId = intent.AuditId, OriginRevision = intent.OriginRevision, DedupeKey = dedupe, AvailableAt = available }); return id;
        Guid? Origin(JobOrigin origin) => intent.Origin == origin ? intent.OriginId : null;
    }
}
