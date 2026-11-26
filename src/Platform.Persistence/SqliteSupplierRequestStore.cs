using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteSupplierRequestStore(SqliteSessionFactory sessions)
{
    private readonly SqliteSessionFactory sessions = sessions.RequirePlaintext();

    public Task<JobLease?> ClaimAsync(BrandContext context, JobKind kind, Guid worker, CancellationToken ct = default)
    {
        if (kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier))
            throw new UnauthorizedAccessException("Unsupported supplier job.");
        return sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(
            tx, context.BrandId, kind, worker, TimeSpan.FromSeconds(90), token), ct);
    }

    public Task<SupplierBinding> ReadAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Require(context, lease);
        return sessions.RunAsync(false, async (_, tx, token) =>
            (await ReadStateAsync(tx, context, lease.Intent.OriginId!.Value, token)).Binding, ct);
    }

    public Task<SupplierAttempt?> BeginAsync(BrandContext context, JobLease lease, SupplierQuote? quote,
        bool usesPurchaseKey, bool provenIntentLookup, bool supportsExport, CancellationToken ct = default)
    {
        Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            State state = await ReadStateAsync(tx, context, lease.Intent.OriginId!.Value, token);
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            bool submit = lease.Intent.Kind == JobKind.SubmitSupplier;
            if (state.Status is "succeeded" or "resolved")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return null;
            }
            if (state.Payment is not ("paid" or "not_required") ||
                state.Fulfillment is "cancelled" or "compensated" or "completed" ||
                state.Held || state.OrderReview is not null && state.OrderReview != "provider_unknown")
                throw new UnauthorizedAccessException("The supplier obligation is not ready.");
            if (submit)
            {
                if (state.Status != "pending" || state.RequestReview is not null)
                    throw new UnauthorizedAccessException("The supplier submission requires review.");
                if (await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.SupplierRequestId == state.Binding.RequestId && row.Operation == "submit", cancel), token)) throw new UnauthorizedAccessException("An original supplier submission already exists.");
                string? blocker = !state.SourceEnabled || state.SourceRevision != state.Binding.SourceRevision
                    ? "source_disabled" : quote is null ? "cost_unverified" :
                    SupplierRules.Preflight(state.Binding, quote, DateTime.UtcNow);
                if (blocker is not null)
                {
                    await ReviewAsync(tx, state, blocker, "blocked", token);
                    await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                    return null;
                }
            }
            Guid? parent = null;
            string? remote = null;
            if (!submit)
            {
                var original = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.SupplierRequestId == state.Binding.RequestId && row.Operation == "submit").OrderByDescending(row => row.AttemptNo).Select(row => new { row.Id, row.RemoteRef }).FirstOrDefaultAsync(cancel), token) ?? throw new UnauthorizedAccessException("The original supplier attempt is unavailable.");
                parent = original.Id; remote = original.RemoteRef;
                if (lease.Intent.Kind == JobKind.ExportSupplier ? !supportsExport || remote is null :
                    !provenIntentLookup && remote is null)
                {
                    await ReviewAsync(tx, state, "provider_unknown", "unknown", token);
                    await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                    return null;
                }
            }
            string operation = submit ? "submit" :
                lease.Intent.Kind == JobKind.ExportSupplier ? "export" : "status_lookup";
            Guid attempt = AttemptId(lease.Id, lease.Attempt);
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await data.RequireOrderCommandAsync(context, state.Binding.OrderId, cancel);
                long number = (await data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.SupplierRequestId == state.Binding.RequestId).MaxAsync(row => (long?)row.AttemptNo, cancel) ?? 0) + 1;
                data.Data.Add(new Entities.SupplierRequestAttempt { Id = attempt, SupplierRequestId = state.Binding.RequestId, AttemptNo = number, Operation = operation, Outcome = "in_flight", ParentSubmitAttemptId = parent, AdminAuthorizationAuditId = submit ? lease.Intent.AuditId : null, MaxChargeAmount = submit ? SupplierRules.UnitCeiling(state.Binding) : null, PurchaseIdempotencyKey = submit && usesPurchaseKey ? "purchase:" + attempt.ToString("D") : null });
                if (submit) { var row = await data.Data.Set<Entities.SupplierRequest>().SingleAsync(row => row.Id == state.Binding.RequestId && row.Status == "pending", cancel); row.Status = "in_flight"; row.UpdatedAt = DateTime.UtcNow; }
                return true;
            }, token);
            return new SupplierAttempt(state.Binding, attempt, parent, operation, remote,
                submit && usesPurchaseKey ? "purchase:" + attempt.ToString("D") : null);
        }, ct);
    }

    public Task RecordAsync(BrandContext context, JobLease lease, SupplierAttempt attempt,
        SupplierOutcome result, string? evidence, CancellationToken ct = default)
    {
        Require(context, lease);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(result);
        if (attempt.Id != AttemptId(lease.Id, lease.Attempt) ||
            attempt.Binding.RequestId != lease.Intent.OriginId ||
            evidence is not null && (!evidence.StartsWith("protected:", StringComparison.Ordinal) ||
                evidence.Length is < 12 or > 1024 || evidence.Any(char.IsControl)))
            throw new UnauthorizedAccessException("Supplier evidence origin is invalid.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            State state = await ReadStateAsync(tx, context, attempt.Binding.RequestId, token);
            if (state.Binding != attempt.Binding)
                throw new UnauthorizedAccessException("The original supplier binding changed.");
            bool active = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.Id == attempt.Id && row.SupplierRequestId == state.Binding.RequestId && row.Operation == attempt.Operation && row.Outcome == "in_flight" && row.ParentSubmitAttemptId == attempt.ParentSubmit, cancel), token);
            if (!active) throw new UnauthorizedAccessException("The supplier result is not an active attempt.");
            JobLease renewed = lease.Intent.Kind == JobKind.SubmitSupplier ? lease :
                await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            if (state.Status is "succeeded" or "resolved")
            {
                await AttemptAsync(tx, context, attempt.Id, (row, now) => { row.Outcome = "unknown"; row.ErrorCode = "terminal_obligation"; row.EvidenceRef = evidence; row.FinishedAt = now; }, token);
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return 0;
            }
            bool archived = evidence is not null && result.Evidence.Length > 0;
            string? review = SupplierRules.Validate(state.Binding, result);
            bool identity = SupplierRules.Identity(state.Binding, result) is null &&
                result.Items.All(item => item.Reference.Length <= 160);
            if (attempt.ParentSubmit is Guid parent)
            {
                string? reference = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.SupplierRequestAttempt>().Where(row => row.Id == parent && row.Operation == "submit").Select(row => row.RemoteRef ?? data.Data.Set<Entities.SupplierRequestAttempt>().Where(child => child.ParentSubmitAttemptId == row.Id && child.RemoteRef != null).OrderByDescending(child => child.AttemptNo).Select(child => child.RemoteRef).FirstOrDefault()).SingleOrDefaultAsync(cancel), token);
                if (reference is string previous && result.PurchaseReference is string current &&
                    previous != current) identity = false;
            }
            if (!identity) review = "identity_mismatch";
            if (!archived && (result.Kind != SupplierOutcomeKind.Unknown ||
                result.Evidence.Length > 0 || result.Items.Count > 0)) review = "credential_recovery";
            if (identity && archived && result.Items.Count > 0 &&
                !await ExistingItemsMatchAsync(tx, state, result, token))
            {
                identity = false;
                review = "identity_mismatch";
            }
            bool acquired = false;
            if (identity && archived && result.Items.Count > 0)
                acquired = await CaptureAsync(tx, context, state, result, token);
            if (result.Kind == SupplierOutcomeKind.ConfirmedNoEffect && acquired)
            {
                identity = false;
                review = "identity_mismatch";
            }
            bool noEffect = archived && identity && result.Kind == SupplierOutcomeKind.ConfirmedNoEffect;
            bool complete = archived && identity && review is null && result.Kind == SupplierOutcomeKind.Completed;
            string status = noEffect ? "failed" : complete ? "succeeded" : "unknown";
            await AttemptAsync(tx, context, attempt.Id, (row, now) => { row.Outcome = status; row.ErrorCode = noEffect ? "confirmed_no_effect" : review; row.RemoteRef = identity ? result.PurchaseReference : null; row.EvidenceRef = archived ? evidence : null; row.FinishedAt = now; }, token);
            if (complete)
            {
                await RequestAsync(tx, context, state.Binding.RequestId, row => row.Status == "pending" || row.Status == "in_flight" || row.Status == "unknown" || row.Status == "partial_pending_admin", (row, now) => { row.Status = "succeeded"; row.ReviewCode = null; row.RemoteRef ??= result.PurchaseReference; row.EvidenceRef = evidence; row.UpdatedAt = now; }, token);
                if (state.OrderReview is null || state.OrderReview == "provider_unknown")
                {
                    await OrderAsync(tx, context, state.Binding.OrderId, row => row.ReviewCode == null || row.ReviewCode == "provider_unknown", (row, now) => { row.FulfillmentStatus = "acquiring"; row.ReviewCode = null; row.Revision++; row.UpdatedAt = now; }, token);
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Order(JobKind.PrepareManifest, context.BrandId, state.Binding.OrderId),
                        state.Binding.OrderId, DateTime.UtcNow, token);
                }
            }
            else
            {
                string code = review ?? (noEffect ? "provider_failed" : "provider_unknown");
                await ReviewAsync(tx, state, code, noEffect ? "failed" :
                    result.Items.Count > 0 && identity && archived ? "partial_pending_admin" : "unknown",
                    token, archived ? evidence : null, identity ? result.PurchaseReference : null);
                if (!noEffect && lease.Intent.Kind == JobKind.SubmitSupplier)
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, state.Binding.RequestId),
                        attempt.Id, DateTime.UtcNow.AddSeconds(5), token);
            }
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return 0;
        }, ct);
    }

    public Task<bool> RecoverExpiredAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(true, async (_, tx, token) =>
        {
            var selected = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Job>().Where(row => row.BrandId == context.BrandId && (row.JobKind == "submit_supplier" || row.JobKind == "reconcile_supplier" || row.JobKind == "export_supplier") && row.Status == "leased" && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow()).OrderBy(row => row.LeaseUntil).ThenBy(row => row.Id).Select(row => new { row.Id, Request = row.SupplierRequestId!.Value, row.Attempts, row.JobKind }).FirstOrDefaultAsync(cancel), token);
            if (selected is null) return false;
            Guid job = selected.Id, request = selected.Request; long number = selected.Attempts; string kind = selected.JobKind;
            State state = await ReadStateAsync(tx, context, request, token);
            Guid attempt = AttemptId(job, number);
            bool started = await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await data.RequireOrderCommandAsync(context, state.Binding.OrderId, cancel);
                bool begun = await data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.Id == attempt && row.SupplierRequestId == request, cancel);
                var row = await data.Data.Set<Entities.Job>().SingleOrDefaultAsync(row => row.Id == job && row.Status == "leased" && row.Attempts == number && row.LeaseUntil <= Platform.Persistence.Configurations.EfValues.UtcNow(), cancel) ?? throw new UnauthorizedAccessException("The supplier lease changed.");
                DateTime now = DateTime.UtcNow; row.Status = begun ? "failed" : "retry"; row.LockedBy = null; row.LeaseUntil = null; row.AvailableAt = now; row.CompletedAt = begun ? now : null; row.LastErrorCode = begun ? "remote_outcome_unknown" : null;
                if (begun) { var original = await data.Data.Set<Entities.SupplierRequestAttempt>().SingleAsync(row => row.Id == attempt, cancel); if (original.Outcome == "in_flight") { original.Outcome = "unknown"; original.ErrorCode = "provider_unknown"; original.FinishedAt = now; } }
                return begun;
            }, token);
            if (started)
            {
                await ReviewAsync(tx, state, "provider_unknown", "unknown", token);
                if (kind == "submit_supplier")
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, request),
                        attempt, DateTime.UtcNow, token);
            }
            return true;
        }, ct);

    private static async Task<bool> ExistingItemsMatchAsync(SqliteTransaction tx, State state, SupplierOutcome result, CancellationToken ct)
    {
        var binding = state.Binding;
        int newCustomers = 0;
        int available = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Order>().Where(row => row.Id == binding.OrderId).Select(row => row.Quantity - data.Data.Set<Entities.Fulfillment>().Count(f => f.OrderId == row.Id && f.Status != "unfilled")).SingleAsync(token), ct);
        if (available < 0) return false;
        foreach (var item in result.Items)
        {
            byte[] key = SupplierRules.ItemKey(binding, item.Reference);
            var existing = await SqliteSessionFactory.StepEfAsync(tx, false,
                (data, token) => SupplierQueries.MatchItemAsync(data, binding, item, key, token), ct);
            if (!existing.Found) { if (item.Disposition == SupplierItemDisposition.Customer) newCustomers++; continue; }
            if (!existing.Matches) return false;
        }
        return newCustomers <= available;
    }
    private static async Task<bool> CaptureAsync(SqliteTransaction tx, BrandContext context,
        State state, SupplierOutcome result, CancellationToken ct)
    {
        bool acquired = false;
        foreach (SupplierItem item in result.Items.OrderBy(x => x.Reference, StringComparer.Ordinal))
        {
            byte[] key = SupplierRules.ItemKey(state.Binding, item.Reference);
            Guid? otherRequest = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Fulfillment>().Where(row => row.SupplierItemKey == key).Select(row => row.SupplierRequestId).Concat(data.Data.Set<Entities.InventoryItem>().Where(row => row.SupplierItemKey == key).Select(row => row.SupplierRequestId)).FirstOrDefaultAsync(token), ct);
            if (otherRequest is Guid id)
            {
                if (id != state.Binding.RequestId)
                    throw new UnauthorizedAccessException("Supplier item already belongs to another obligation.");
                continue;
            }
            if (item.Disposition == SupplierItemDisposition.Customer)
            {
                var occupied = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == state.Binding.OrderId).Select(row => row.UnitNo).Take(state.Quantity + 1).ToListAsync(token), ct);
                int unit = Enumerable.Range(1, state.Quantity).FirstOrDefault(n => !occupied.Contains(n));
                if (unit == 0) throw new InvalidOperationException("Supplier item would exceed the accepted order.");
                Guid fulfillment = Guid.CreateVersion7();
                byte[] content = item.Content.ToArray();
                try { await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, state.Binding.OrderId, token); data.Data.Add(new Entities.Fulfillment { Id = fulfillment, OrderId = state.Binding.OrderId, UnitNo = unit, Kind = "supplier", Status = "acquired", SupplierRequestId = state.Binding.RequestId, RemoteItemRef = item.Reference, SupplierItemKey = key, DeliveredContent = content, ContentKind = item.ContentKind }); return true; }, ct); }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
            else
            {
                Guid inventoryId = Guid.CreateVersion7();
                byte[] content = item.Content.ToArray();
                byte[] digest = SHA256.HashData(content);
                try { await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, state.Binding.OrderId, token); data.Data.Add(new Entities.InventoryItem { Id = inventoryId, ProductId = state.Binding.ProductId, CredentialContent = content, SourceFingerprint = digest, OriginKind = "supplier_bonus", SupplierRequestId = state.Binding.RequestId, RemoteItemRef = item.Reference, SupplierItemKey = key, State = "admin_held" }); return true; }, ct); }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
            acquired = true;
        }
        return acquired;
    }

    private static async Task ReviewAsync(SqliteTransaction tx, State state, string code, string status,
        CancellationToken ct, string? evidence = null, string? remote = null)
    {
        Guid order = state.Binding.OrderId;
        await SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) =>
        {
            Guid member = await data.Data.Set<Entities.Order>().Where(row => row.Id == order).Select(row => row.UserId).SingleAsync(token); await data.RequireWalletCommandAsync(member, token);
            var request = await data.Data.Set<Entities.SupplierRequest>().SingleOrDefaultAsync(row => row.Id == state.Binding.RequestId && row.Status != "succeeded" && row.Status != "resolved", token) ?? throw new InvalidOperationException("Supplier review could not be recorded.");
            request.Status = status; request.ReviewCode = code; request.EvidenceRef = evidence ?? request.EvidenceRef; request.RemoteRef ??= remote; request.UpdatedAt = DateTime.UtcNow;
            var row = await data.Data.Set<Entities.Order>().SingleOrDefaultAsync(row => row.Id == order && (row.PaymentStatus == "paid" || row.PaymentStatus == "not_required"), token) ?? throw new InvalidOperationException("Supplier obligation is no longer payable.");
            row.FulfillmentStatus = "requires_admin"; row.ReviewCode ??= code; row.Revision++; row.UpdatedAt = DateTime.UtcNow; return true;
        }, ct);
    }
    private static async Task<State> ReadStateAsync(SqliteTransaction tx, BrandContext context, Guid request, CancellationToken ct)
    {
        var result = await SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id join source in data.Data.Set<Entities.ProductSupplierSource>() on new { Id = r.ProductSupplierSourceId, Product = o.ProductId } equals new { Id = (Guid?)source.Id, Product = (Guid?)source.ProductId } into sources from source in sources.DefaultIfEmpty() where r.Id == request && b.Id == context.BrandId && b.BotId == context.BotId && b.Active && r.ProductSupplierSourceId != null && r.SourceRevision != null && r.ProviderCode != null && r.AccountRef != null && r.ExternalProductId != null && r.CostBaselineAmount != null && r.CostCurrency != null select new State(new SupplierBinding(r.Id, o.Id, o.ProductId!.Value, r.ProductSupplierSourceId!.Value, r.SourceRevision!.Value, r.ProviderCode!, r.AccountRef!, r.ExternalProductId!, r.ExternalVariantId, r.LocalIntentKey, r.RequestedQuantity, r.CostBaselineAmount!.Value, r.CostCurrency!, r.ApprovedCostCeiling), r.Status, r.ReviewCode, o.PaymentStatus, o.FulfillmentStatus, o.HoldAt != null, o.ReviewCode, source != null && source.Enabled, source == null ? 0 : source.Revision, o.Quantity)).SingleOrDefaultAsync(token), ct);
        return result ?? throw new UnauthorizedAccessException("Supplier source binding is unavailable.");
    }
    private static Task<bool> RequestAsync(SqliteTransaction tx, BrandContext context, Guid request, System.Linq.Expressions.Expression<Func<Entities.SupplierRequest, bool>> predicate, Action<Entities.SupplierRequest, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { var row = await data.Data.Set<Entities.SupplierRequest>().Where(predicate).SingleOrDefaultAsync(row => row.Id == request, token) ?? throw new InvalidOperationException("Supplier obligation was superseded."); await data.RequireOrderCommandAsync(context, row.OrderId, token); change(row, DateTime.UtcNow); return true; }, ct);
    private static Task<bool> OrderAsync(SqliteTransaction tx, BrandContext context, Guid order, System.Linq.Expressions.Expression<Func<Entities.Order, bool>> predicate, Action<Entities.Order, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { await data.RequireOrderCommandAsync(context, order, token); var row = await data.Data.Set<Entities.Order>().Where(predicate).SingleOrDefaultAsync(row => row.Id == order, token) ?? throw new InvalidOperationException("Supplier fulfillment was superseded."); change(row, DateTime.UtcNow); return true; }, ct);
    private static Task<bool> AttemptAsync(SqliteTransaction tx, BrandContext context, Guid attempt, Action<Entities.SupplierRequestAttempt, DateTime> change, CancellationToken ct) => SqliteSessionFactory.StepEfAsync(tx, true, async (data, token) => { var row = await data.Data.Set<Entities.SupplierRequestAttempt>().SingleOrDefaultAsync(row => row.Id == attempt && row.Outcome == "in_flight", token) ?? throw new UnauthorizedAccessException("Supplier attempt was superseded."); Guid order = await data.Data.Set<Entities.SupplierRequest>().Where(value => value.Id == row.SupplierRequestId).Select(value => value.OrderId).SingleAsync(token); await data.RequireOrderCommandAsync(context, order, token); change(row, DateTime.UtcNow); return true; }, ct);

    private static Guid AttemptId(Guid job, long number) =>
        new(SHA256.HashData(Encoding.ASCII.GetBytes("supplier-attempt-v1:" + job.ToString("D") + ":" +
            number.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16));

    private static void Require(BrandContext context, JobLease lease)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (lease.Intent.Kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier) ||
            lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid id || id == Guid.Empty)
            throw new UnauthorizedAccessException("The original supplier job is unavailable.");
    }

    private sealed record State(SupplierBinding Binding, string Status, string? RequestReview, string Payment,
        string Fulfillment, bool Held, string? OrderReview, bool SourceEnabled, long SourceRevision, int Quantity);
}
