using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SupplierAttempt(SupplierBinding Binding, Guid Id, Guid? ParentSubmit, string Operation, string? PurchaseReference, string? PurchaseKey = null);
public sealed class SupplierRequestStore(DbSessionFactory sessions, long? trustedStaffChat = null, TimeSpan? remoteLeaseDuration = null)
{
    private static IQueryable<SupplierBinding> Binding(EfSession data, Guid request) => from value in data.Data.Set<Entities.SupplierRequest>() join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id where value.Id == request select new SupplierBinding(value.Id, order.Id, order.ProductId!.Value, value.ProductSupplierSourceId!.Value, value.SourceRevision!.Value, value.ProviderCode!, value.AccountRef!, value.ExternalProductId!, value.ExternalVariantId, value.LocalIntentKey, value.RequestedQuantity, value.CostBaselineAmount!.Value, value.CostCurrency!, value.ApprovedCostCeiling);
    public async Task ApproveCompleteAsync(AuthenticatedAdmin actor, BrandContext context, Guid order, Guid request, long revision, Guid command, string reason, CancellationToken ct = default)
    {
        AdminCommand.Revision(revision); AdminCommand.Reason(reason);
        byte[] hash = AdminCommand.Hash("supplier-complete-review-v1", request.ToString("D"), reason);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.ManualFulfillment, AuditEntity.Order, order, context.BrandId, AuditReason.OperatorReview, command, hash, revision, revision + 1), ct)) return;
            bool owned = await session.RunEfStepAsync(false, (data, token) => (from value in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on value.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where value.Id == request && o.Id == order && b.Id == context.BrandId && b.BotId == context.BotId select value.Id).AnyAsync(token), ct);
            if (!owned) throw new UnauthorizedAccessException("Supplier review is unavailable.");
            await LockAsync(session, context, request, ct);
            var state = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id where r.Id == request select new { Quantity = o.Quantity, Valid = o.Revision == revision && o.FulfillmentStatus == "requires_admin" && o.ReviewCode == "partial_acquisition" && o.ReleaseAuthorizedAt == null && o.DeliveryGeneration == null && r.Status == "succeeded" && r.ReviewCode == null && r.RemoteRef != null && r.EvidenceRef != null && data.Data.Set<Entities.Fulfillment>().Count(f => f.OrderId == o.Id) == o.Quantity && data.Data.Set<Entities.Fulfillment>().Count(f => f.SupplierRequestId == r.Id && f.Kind == "supplier" && f.Status == "acquired") == r.RequestedQuantity && data.Data.Set<Entities.SupplierRequestAttempt>().Any(a => a.SupplierRequestId == r.Id && a.Outcome == "succeeded" && a.ErrorCode == null && a.EvidenceRef == r.EvidenceRef && a.RemoteRef != null && a.FinishedAt != null) }).SingleAsync(token), ct);
            if (!state.Valid || !await new DeliveryStore(sessions).HasCompleteAcquisitionAsync(session, context, order, state.Quantity, ct)) throw new InvalidOperationException("Complete verified supplier acquisition requires current review.");
            await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, now) => { row.FulfillmentStatus = "acquiring"; row.ReviewCode = null; }, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, order), order, DateTime.UtcNow, ct);
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<SupplierBinding> ReadAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Require(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        await session.AdmitWriterAsync(context, ct);
        return await session.RunEfStepAsync(false, async (data, token) => { await data.RequireOrderCommandAsync(context, await data.Data.Set<Entities.SupplierRequest>().Where(row => row.Id == lease.Intent.OriginId!.Value).Select(row => row.OrderId).SingleAsync(token), token); return await Binding(data, lease.Intent.OriginId!.Value).SingleAsync(token); }, ct);
    }
    public async Task<SupplierAttempt?> BeginAsync(BrandContext context, JobLease lease, SupplierQuote? quote, CancellationToken ct = default, bool usesPurchaseKey = false, bool provenIntentLookup = false, bool supportsExport = false)
    {
        Require(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var binding = await LockAsync(session, context, lease.Intent.OriginId!.Value, ct);
            TimeSpan duration = remoteLeaseDuration ?? TimeSpan.FromMinutes(2);
            if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(remoteLeaseDuration));
            await OutboxStore.RenewAsync(session, lease, duration, ct);
            var state = await session.RunEfStepAsync(false, async (data, token) => { var row = await (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join source in data.Data.Set<Entities.ProductSupplierSource>() on r.ProductSupplierSourceId equals source.Id where r.Id == binding.RequestId select new { r.Status, Review = o.ReviewCode, Remote = r.RemoteRef, Source = source.Enabled && source.Revision == r.SourceRevision }).SingleAsync(token); return (row.Status, row.Review, row.Remote, row.Source, Now: await data.BrandClockAsync(context.BrandId, token)); }, ct);
            bool submit = lease.Intent.Kind == JobKind.SubmitSupplier;
            if (state.Status is "resolved" or "succeeded") { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return null; }
            if (submit)
            {
                if (state.Status != "pending" || state.Review is not null) throw new InvalidOperationException("Supplier obligation requires reconciliation.");
                string? blocker = !state.Source ? "source_disabled" : quote is null ? "cost_unverified" : SupplierRules.Preflight(binding, quote, state.Now);
                if (blocker is not null)
                {
                    await OutboxStore.CompleteAsync(session, lease, ct);
                    await ReviewAsync(session, context, binding, "blocked", blocker, Guid.NewGuid(), null, ct);
                    await session.CommitAsync(ct); return null;
                }
            }
            Guid? parent = submit ? null : await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.SupplierRequestId == binding.RequestId && row.Operation == "submit").OrderByDescending(row => row.AttemptNo).Select(row => row.Id).FirstAsync(token), ct);
            string? purchaseReference = parent is Guid original ? await PurchaseReferenceAsync(session, original, ct) : null;
            Guid attempt = AttemptId(lease.Id, lease.Attempt);
            bool export = lease.Intent.Kind == JobKind.ExportSupplier;
            if (!submit && (export ? !supportsExport || purchaseReference is null : purchaseReference is null && !provenIntentLookup))
            {
                await OutboxStore.CompleteAsync(session, lease, ct);
                if (state.Review is null) await ReviewAsync(session, context, binding, "unknown", "provider_unknown", attempt, null, ct);
                await session.CommitAsync(ct); return null;
            }
            string? purchaseKey = submit && usesPurchaseKey ? "purchase:" + attempt.ToString("D") : null;
            string operation = submit ? "submit" : export ? "export" : "status_lookup";
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, binding.OrderId, token);
                long number = (await data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.SupplierRequestId == binding.RequestId).MaxAsync(row => (long?)row.AttemptNo, token) ?? 0) + 1;
                data.Data.Add(new Entities.SupplierRequestAttempt { Id = attempt, SupplierRequestId = binding.RequestId, AttemptNo = number, Operation = operation, Outcome = "in_flight", ParentSubmitAttemptId = parent, AdminAuthorizationAuditId = submit ? lease.Intent.AuditId : null, MaxChargeAmount = submit ? SupplierRules.UnitCeiling(binding) : null, PurchaseIdempotencyKey = purchaseKey }); return true;
            }, ct);
            if (submit) await SupplierQueries.ChangeAsync(session, context, binding.RequestId, (row, now) => { row.Status = "in_flight"; row.UpdatedAt = now; }, ct);
            await session.CommitAsync(ct); return new(binding, attempt, parent, operation, purchaseReference, purchaseKey);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task RecordAsync(BrandContext context, JobLease lease, SupplierAttempt attempt, SupplierOutcome result, string? evidence, CancellationToken ct = default)
    {
        Require(context, lease);
        if (attempt.Binding.RequestId != lease.Intent.OriginId || attempt.Id == Guid.Empty || evidence?.Length > 512) throw new ArgumentException("Invalid supplier receipt origin.");
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var binding = await LockAsync(session, context, attempt.Binding.RequestId, ct);
            if (binding != attempt.Binding) throw new InvalidOperationException("Supplier binding changed.");
            await OutboxStore.CompleteAsync(session, lease, ct);
            bool active = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.Id == attempt.Id && row.SupplierRequestId == binding.RequestId && row.Operation == attempt.Operation && row.Outcome == "in_flight" && row.ParentSubmitAttemptId == attempt.ParentSubmit, token), ct);
            if (!active) throw new InvalidOperationException("Supplier attempt is stale.");
            string requestStatus = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequest>().Where(row => row.Id == binding.RequestId).Select(row => row.Status).SingleAsync(token), ct);
            if (requestStatus is "succeeded" or "resolved")
            {
                await SupplierQueries.AttemptAsync(session, context, attempt.Id, (row, now) => { row.Outcome = "unknown"; row.ErrorCode = "terminal_obligation"; row.EvidenceRef = evidence; row.FinishedAt = now; }, ct);
                await session.CommitAsync(ct); return;
            }
            string? review = SupplierRules.Validate(binding, result);
            bool archived = !string.IsNullOrWhiteSpace(evidence) && result.Evidence.Length > 0;
            bool identity = SupplierRules.Identity(binding, result) is null;
            string? prior = attempt.ParentSubmit is Guid original ? await PurchaseReferenceAsync(session, original, ct) : null;
            if (prior is not null && result.PurchaseReference is not null && prior != result.PurchaseReference) { identity = false; review = "identity_mismatch"; }
            if (!archived && (result.Kind != SupplierOutcomeKind.Unknown || result.Evidence.Length > 0 || result.Items.Count > 0)) review = "credential_recovery";
            if (identity && archived && result.Items.Count > 0)
            {
                if (await ExistingItemsMatchAsync(session, context, binding, result, ct)) await CaptureAsync(session, context, binding, result, ct);
                else { identity = false; review = "identity_mismatch"; }
            }
            bool acquired = await session.RunEfStepAsync(false, async (data, token) => await data.Data.Set<Entities.Fulfillment>().AnyAsync(row => row.SupplierRequestId == binding.RequestId && row.Status != "unfilled", token) || await data.Data.Set<Entities.InventoryItem>().AnyAsync(row => row.SupplierRequestId == binding.RequestId && row.OriginKind == "supplier_bonus", token), ct);
            if (acquired && result.Kind == SupplierOutcomeKind.ConfirmedNoEffect) { identity = false; review = "identity_mismatch"; }
            bool noEffect = archived && identity && result.Kind == SupplierOutcomeKind.ConfirmedNoEffect;
            bool complete = archived && identity && review is null;
            string outcome = noEffect ? "failed" : complete ? "succeeded" : "unknown";
            await SupplierQueries.AttemptAsync(session, context, attempt.Id, (row, now) => { row.Outcome = outcome; row.ErrorCode = noEffect ? "confirmed_no_effect" : review; row.RemoteRef = identity ? result.PurchaseReference : null; row.EvidenceRef = archived ? evidence : null; row.FinishedAt = now; }, ct);
            if (complete)
            {
                var reviews = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id where r.Id == binding.RequestId select new { Order = o.ReviewCode, Request = r.ReviewCode }).SingleAsync(token), ct);
                bool transient = reviews.Order == "provider_unknown" && reviews.Request == "provider_unknown";
                bool protectedReview = reviews.Order is not null && !transient;
                if (transient) await SupplierQueries.OrderAsync(session, context, binding.OrderId, (row, now) => { row.ReviewCode = null; row.FulfillmentStatus = "acquiring"; row.Revision++; }, ct);
                await SupplierQueries.ChangeAsync(session, context, binding.RequestId, (row, now) => { row.Status = "succeeded"; row.RemoteRef ??= result.PurchaseReference; row.EvidenceRef = evidence; row.ReviewCode = null; row.UpdatedAt = now; }, ct);
                if (!protectedReview) await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, binding.OrderId), binding.OrderId, DateTime.UtcNow, ct);
                else await NoticeAsync(session, context, binding, attempt.Id, false, ct);
            }
            else
            {
                string status = noEffect ? "failed" : result.Items.Count > 0 && archived && identity ? "partial_pending_admin" : "unknown";
                if (identity && result.PurchaseReference is not null) await SupplierQueries.ChangeAsync(session, context, binding.RequestId, (row, now) => row.RemoteRef ??= result.PurchaseReference, ct);
                await ReviewAsync(session, context, binding, status, review ?? "provider_unknown", attempt.Id, archived ? evidence : null, ct);
                if (!noEffect && status == "unknown") await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, binding.RequestId), attempt.Id, DateTime.UtcNow.AddSeconds(5), ct);
            }
            bool bonus = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.InventoryItem>().AnyAsync(row => row.SupplierRequestId == binding.RequestId && row.OriginKind == "supplier_bonus", token), ct);
            if (bonus) await NoticeAsync(session, context, binding, attempt.Id, true, ct);
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static Task<string?> PurchaseReferenceAsync(DbSession session, Guid submit, CancellationToken ct) => session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.Id == submit && row.Operation == "submit").Select(row => row.RemoteRef ?? data.Data.Set<Entities.SupplierRequestAttempt>().Where(child => child.ParentSubmitAttemptId == row.Id && child.RemoteRef != null).OrderByDescending(child => child.AttemptNo).Select(child => child.RemoteRef).FirstOrDefault()).SingleAsync(token), ct);
    private async Task<bool> ExistingItemsMatchAsync(DbSession session, BrandContext context, SupplierBinding binding, SupplierOutcome result, CancellationToken ct)
    {
        int newCustomers = 0;
        int available = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Order>().Where(row => row.Id == binding.OrderId).Select(row => row.Quantity - data.Data.Set<Entities.Fulfillment>().Count(f => f.OrderId == row.Id && f.Status != "unfilled")).SingleAsync(token), ct);
        foreach (var item in result.Items)
        {
            byte[] key = SupplierRules.ItemKey(binding, item.Reference);
            var existing = await session.RunEfStepAsync(false,
                (data, token) => SupplierQueries.MatchItemAsync(data, binding, item, key, token), ct);
            if (!existing.Found) { if (item.Disposition == SupplierItemDisposition.Customer) newCustomers++; continue; }
            if (!existing.Matches) return false;
        }
        return newCustomers <= available;
    }
    private async Task CaptureAsync(DbSession session, BrandContext context, SupplierBinding binding, SupplierOutcome result, CancellationToken ct)
    {
        foreach (var item in result.Items.OrderBy(i => i.Reference, StringComparer.Ordinal))
        {
            byte[] key = SupplierRules.ItemKey(binding, item.Reference);
            bool exists = await session.RunEfStepAsync(false, async (data, token) => await data.Data.Set<Entities.Fulfillment>().AnyAsync(row => row.SupplierItemKey == key, token) || await data.Data.Set<Entities.InventoryItem>().AnyAsync(row => row.SupplierItemKey == key, token), ct);
            if (exists) continue;
            if (item.Disposition == SupplierItemDisposition.Customer)
            {
                Guid id = await session.RunEfStepAsync(true, async (data, token) =>
                {
                    await data.RequireOrderCommandAsync(context, binding.OrderId, token);
                    Guid? slot = await data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == binding.OrderId && row.Status == "unfilled").OrderBy(row => row.UnitNo).Select(row => (Guid?)row.Id).FirstOrDefaultAsync(token);
                    if (slot is Guid existing) { await data.LockFulfillmentAsync(existing, token); return existing; }
                    int quantity = await data.Data.Set<Entities.Order>().Where(row => row.Id == binding.OrderId).Select(row => row.Quantity).SingleAsync(token);
                    var used = await data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == binding.OrderId).Select(row => row.UnitNo).Take(quantity + 1).ToListAsync(token);
                    int number = Enumerable.Range(1, quantity).First(value => !used.Contains(value));
                    Guid created = Guid.CreateVersion7(); data.Data.Add(new Entities.Fulfillment { Id = created, OrderId = binding.OrderId, UnitNo = number, Kind = "supplier", SupplierRequestId = binding.RequestId, Status = "unfilled" }); return created;
                }, ct);
                byte[] content = item.Content.ToArray();
                try
                {
                    await session.RunEfStepAsync(true, async (data, token) =>
                    {
                        await data.RequireOrderCommandAsync(context, binding.OrderId, token);
                        var row = await data.Data.Set<Entities.Fulfillment>().SingleAsync(row => row.Id == id, token);
                        row.Kind = "supplier"; row.Status = "acquired"; row.SupplierRequestId = binding.RequestId; row.RemoteItemRef = item.Reference; row.SupplierItemKey = key; row.DeliveredContent = content; row.ContentKind = item.ContentKind; return true;
                    }, ct);
                }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
            else
            {
                Guid id = Guid.NewGuid();
                byte[] fingerprint = SHA256.HashData(item.Content.Span);
                byte[] content = item.Content.ToArray();
                try { await session.RunEfStepAsync(true, async (data, token) => { await data.RequireOrderCommandAsync(context, binding.OrderId, token); data.Data.Add(new Entities.InventoryItem { Id = id, ProductId = binding.ProductId, CredentialContent = content, SourceFingerprint = fingerprint, OriginKind = "supplier_bonus", SupplierRequestId = binding.RequestId, RemoteItemRef = item.Reference, SupplierItemKey = key, State = "admin_held" }); return true; }, ct); }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
        }
    }
    private async Task ReviewAsync(DbSession session, BrandContext context, SupplierBinding binding, string status, string review, Guid generation, string? evidence, CancellationToken ct)
    {
        await SupplierQueries.ChangeAsync(session, context, binding.RequestId, (row, now) => { row.Status = status; row.ReviewCode = review; row.EvidenceRef = evidence ?? row.EvidenceRef; row.UpdatedAt = now; }, ct);
        await PaymentSettlementStore.ChangeOrderAsync(session, binding.OrderId, (row, now) => { row.FulfillmentStatus = "requires_admin"; row.ReviewCode = row.ReviewCode == "provider_unknown" && review == "partial_acquisition" ? review : row.ReviewCode ?? review; }, ct);
        await NoticeAsync(session, context, binding, generation, false, ct);
    }
    private async Task NoticeAsync(DbSession session, BrandContext context, SupplierBinding binding, Guid generation, bool bonus, CancellationToken ct)
    {
        Guid audit = new(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(generation.ToString("D") + (bonus ? ":bonus" : ":case"))).AsSpan(0, 16));
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireOrderCommandAsync(context, binding.OrderId, token);
            if (!await data.Data.Set<Entities.AuditEvent>().AnyAsync(row => row.Id == audit, token)) data.Data.Add(new Entities.AuditEvent { Id = audit, ActorKind = "system", BrandId = context.BrandId, Action = "update", EntityKind = "supplier_request", EntityId = binding.RequestId, Reason = bonus ? "Protected supplier bonus acquired" : "Supplier obligation requires review", CorrelationId = generation }); return true;
        }, ct);
        if (trustedStaffChat is not < 0) return;
        string dedupe = "supplier-notice:" + audit.ToString("D");
        Guid delivery = await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireOrderCommandAsync(context, binding.OrderId, token);
            Guid? existing = await data.Data.Set<Entities.TelegramDelivery>().Where(row => row.DedupeKey == dedupe).Select(row => (Guid?)row.Id).SingleOrDefaultAsync(token);
            if (existing is Guid id) return id;
            Guid created = Guid.NewGuid(); data.Data.Add(new Entities.TelegramDelivery { Id = created, BrandId = context.BrandId, RecipientKind = "staff_chat", StaffChatId = trustedStaffChat, Purpose = bonus ? "staff_supplier_bonus" : "staff_supplier_case", Action = "send", DedupeKey = dedupe, SupplierRequestId = binding.RequestId, AuditEventId = audit, DeliveryGeneration = generation, Status = "pending" }); return created;
        }, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, ct);
    }
    public async Task<bool> RecoverExpiredAsync(BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var candidates = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Job>().Where(row => row.BrandId == context.BrandId && (row.JobKind == "submit_supplier" || row.JobKind == "reconcile_supplier" || row.JobKind == "export_supplier") && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow()).OrderBy(row => row.LeaseUntil).ThenBy(row => row.Id).Select(row => new { row.Id, Request = row.SupplierRequestId!.Value, Number = row.Attempts }).Take(1).ToListAsync(token), ct);
            if (candidates.Count == 0) return false;
            var job = candidates[0];
            var binding = await LockAsync(session, context, job.Request, ct);
            bool recovered = await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireOrderCommandAsync(context, binding.OrderId, token); await data.LockJobAsync(job.Id, token);
                var row = await data.Data.Set<Entities.Job>().SingleOrDefaultAsync(row => row.Id == job.Id && row.Attempts == job.Number && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow(), token);
                if (row is null) return false;
                Guid id = AttemptId(job.Id, job.Number); bool begun = await data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(value => value.Id == id && value.SupplierRequestId == job.Request, token);
                DateTime now = await data.BrandClockAsync(context.BrandId, token); row.Status = begun ? "failed" : "retry"; row.LockedBy = null; row.LeaseUntil = null; row.CompletedAt = begun ? now : null; row.LastErrorCode = begun ? "remote_outcome_unknown" : null; return true;
            }, ct);
            if (!recovered) return false;
            Guid attempt = AttemptId(job.Id, job.Number);
            bool started = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.Id == attempt && row.SupplierRequestId == job.Request, token), ct);
            if (started)
            {
                await SupplierQueries.AttemptAsync(session, context, attempt, (row, now) => { if (row.Outcome == "in_flight") { row.Outcome = "unknown"; row.ErrorCode = "provider_unknown"; row.FinishedAt = now; } }, ct);
                await ReviewAsync(session, context, binding, "unknown", "provider_unknown", attempt, null, ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, binding.RequestId), attempt, DateTime.UtcNow, ct);
            }
            await session.CommitAsync(ct); return true;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task ApproveCostAsync(AuthenticatedAdmin actor, BrandContext context, Guid request, long expectedRevision, decimal maximumUnitCost, string currency, Guid command, CancellationToken ct = default)
    {
        AdminCommand.Revision(expectedRevision);
        if (maximumUnitCost <= 0) throw new ArgumentOutOfRangeException(nameof(maximumUnitCost));
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.ApproveCost, AuditEntity.SupplierRequest, request, context.BrandId, AuditReason.OperatorReview, command,
                AdminCommand.Hash("supplier-cost-approval-v1", request.ToString("D"), expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), ExactDecimal.Format(maximumUnitCost), currency), expectedRevision, expectedRevision + 1), ct)) return;
            var binding = await LockAsync(session, context, request, ct);
            bool priorSubmit = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.SupplierRequestId == request && row.Operation == "submit", token), ct);
            bool safe = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupplierRequest>().Where(row => row.Id == request).Select(row => row.Status == "blocked" && row.ReviewCode == "cost_limit" && row.Revision == expectedRevision && !data.Data.Set<Entities.Job>().Any(job => job.SupplierRequestId == request && job.Status == "leased") && !data.Data.Set<Entities.Fulfillment>().Any(f => f.SupplierRequestId == request && f.Status != "unfilled") && !data.Data.Set<Entities.InventoryItem>().Any(i => i.SupplierRequestId == request) && !data.Data.Set<Entities.SupplierRequestAttempt>().Any(a => a.SupplierRequestId == request && a.Operation == "submit" && !((a.Outcome == "failed" && a.ErrorCode == "confirmed_no_effect" && a.EvidenceRef != null) || ((a.Outcome == "unknown" || a.Outcome == "timeout") && data.Data.Set<Entities.SupplierRequestAttempt>().Any(l => l.ParentSubmitAttemptId == a.Id && l.Operation == "status_lookup" && l.Outcome == "failed" && l.ErrorCode == "confirmed_no_effect" && l.EvidenceRef != null))))).SingleAsync(token), ct);
            if (!safe || currency != binding.Currency || maximumUnitCost <= binding.UnitBaseline) throw new InvalidOperationException("Original cost review is unavailable.");
            await SupplierQueries.ChangeAsync(session, context, request, (row, now) => { row.Status = priorSubmit ? "failed" : "pending"; row.ReviewCode = null; row.ApprovedCostCeiling = maximumUnitCost; row.ApprovalAuditId = command; row.Revision++; row.UpdatedAt = now; }, ct);
            if (!priorSubmit)
            {
                await SupplierQueries.OrderAsync(session, context, binding.OrderId, (row, now) => { if (row.ReviewCode == "cost_limit") { row.ReviewCode = null; row.FulfillmentStatus = "acquiring"; row.Revision++; } }, ct);
                await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request), command, DateTime.UtcNow, ct);
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<SupplierItem> OpenBonusAsync(AuthenticatedAdmin actor, BrandContext context, Guid request, Guid item, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.RunEfStepAsync(false, (data, token) =>
        {
            var owned = from i in data.Data.Set<Entities.InventoryItem>() join r in data.Data.Set<Entities.SupplierRequest>() on i.SupplierRequestId equals r.Id join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where i.Id == item && r.Id == request && i.State == "admin_held" && i.OriginKind == "supplier_bonus" && b.Id == context.BrandId && b.BotId == context.BotId select i;
            return owned.Select(row => new Bonus(row.RemoteItemRef!, row.CredentialContent)).Take(1).ToListAsync(token);
        }, ct);
        if (rows.Count != 1) throw new UnauthorizedAccessException("Protected supplier bonus is unavailable.");
        var data = rows[0];
        await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Maintenance, AuditEntity.SupplierRequest, request, context.BrandId, AuditReason.OperatorReview, command, AdminCommand.Hash("supplier-bonus-access-v1", request.ToString("D"), item.ToString("D"))), ct);
        byte[] plain = data.Content;
        try { await session.CommitAsync(ct); return new(data.Reference, SupplierItemDisposition.Bonus, "code", plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private sealed record Bonus(string Reference, byte[] Content)
    {
        public override string ToString() => "Supplier bonus [redacted]";
    }
    private static Guid AttemptId(Guid job, long number) => new(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("supplier-attempt-v1:" + job.ToString("D") + ":" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16));
    private static void Require(BrandContext context, JobLease lease)
    {
        if (lease.Intent.BrandId != context.BrandId || lease.Intent.Kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier) || lease.Intent.OriginId is null) throw new UnauthorizedAccessException();
    }
    private static async Task<SupplierBinding> LockAsync(DbSession session, BrandContext context, Guid request, CancellationToken ct)
    {
        var id = await session.RunEfStepAsync(false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where r.Id == request && b.Id == context.BrandId && b.BotId == context.BotId select new { Member = u.Id, Product = o.ProductId!.Value, Order = o.Id }).SingleAsync(token), ct);
        await session.RunEfStepAsync(false, async (data, token) => { await data.LockUserAsync(id.Member, context.BrandId, token); return await data.LockProductAsync(id.Product, token); }, ct);
        await session.AdmitWriterAsync(context, ct);
        return await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockOrderAsync(id.Order, token);
            bool accepted = await data.Data.Set<Entities.Order>().AnyAsync(row => row.Id == id.Order && (row.PaymentStatus == "paid" || row.PaymentStatus == "not_required") && row.FulfillmentStatus != "cancelled" && row.FulfillmentStatus != "compensated" && row.FulfillmentStatus != "completed" && row.HoldAt == null, token);
            if (!accepted) throw new InvalidOperationException("Supplier obligation is terminal or held.");
            await data.LockRequestAsync(request, token); return await Binding(data, request).SingleAsync(token);
        }, ct);
    }
}
