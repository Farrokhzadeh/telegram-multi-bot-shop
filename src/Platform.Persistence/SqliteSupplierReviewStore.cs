using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteSupplierReviewStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task ApproveCostAsync(AuthenticatedAdmin actor, BrandContext context, Guid request,
        long expectedRevision, decimal maximumUnitCost, string currency, Guid command, CancellationToken ct = default)
    {
        if (request == Guid.Empty || command == Guid.Empty || expectedRevision < 0 ||
            maximumUnitCost <= 0 || string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Invalid supplier cost approval.");
        var audit = new AuditCommand(command, AuditAction.ApproveCost, AuditEntity.SupplierRequest, request,
            context.BrandId, AuditReason.OperatorReview, command,
            AdminCommand.Hash("supplier-cost-approval-v1", request.ToString("D"),
                expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ExactDecimal.Format(maximumUnitCost), currency), expectedRevision, expectedRevision + 1);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (await AuditReplayAsync(tx, actor, audit, token)) return 0;
            var original = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where r.Id == request && b.Id == context.BrandId && b.BotId == context.BotId && b.Active select new { r.OrderId, r.CostBaselineAmount, r.CostCurrency, r.Status, r.ReviewCode, r.Revision, o.PaymentStatus, o.FulfillmentStatus, o.HoldAt, Prior = data.Data.Set<Entities.SupplierRequestAttempt>().Any(a => a.SupplierRequestId == r.Id && a.Operation == "submit") }).SingleOrDefaultAsync(cancel), token);
            if (original is null || original.CostBaselineAmount == null || original.CostCurrency != currency || original.CostBaselineAmount >= maximumUnitCost || original.Status != "blocked" || original.ReviewCode != "cost_limit" || original.Revision != expectedRevision || original.PaymentStatus is not ("paid" or "not_required") || original.FulfillmentStatus is "completed" or "compensated" or "cancelled" || original.HoldAt != null) throw new InvalidOperationException("Original supplier cost review is unavailable.");
            Guid order = original.OrderId; bool prior = original.Prior;
            bool unresolved = await SqliteSessionFactory.StepEfAsync(tx, false, async (data, cancel) => await data.Data.Set<Entities.Job>().AnyAsync(row => row.SupplierRequestId == request && row.Status == "leased", cancel) || await data.Data.Set<Entities.Fulfillment>().AnyAsync(row => row.SupplierRequestId == request && row.Status != "unfilled", cancel) || await data.Data.Set<Entities.InventoryItem>().AnyAsync(row => row.SupplierRequestId == request, cancel) || await data.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.SupplierRequestId == request && row.Operation == "submit" && (row.Outcome == "in_flight" || row.Outcome == "unknown" || row.Outcome == "timeout" || row.Outcome == "succeeded" || row.Outcome == "failed" && row.ErrorCode != null && row.ErrorCode != "confirmed_no_effect"), cancel), token);
            if (unresolved) throw new InvalidOperationException("Supplier cost outcome requires reconciliation.");
            await WriteAuditAsync(tx, actor, audit, token);
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await data.RequireOrderCommandAsync(context, order, cancel);
                var row = await data.Data.Set<Entities.SupplierRequest>().SingleOrDefaultAsync(row => row.Id == request && row.Status == "blocked" && row.ReviewCode == "cost_limit" && row.Revision == expectedRevision, cancel) ?? throw new InvalidOperationException("Supplier cost approval was superseded.");
                row.Status = prior ? "failed" : "pending"; row.ReviewCode = null; row.ApprovedCostCeiling = maximumUnitCost; row.ApprovalAuditId = command; row.Revision++; row.UpdatedAt = DateTime.UtcNow;
                if (!prior) { var accepted = await data.Data.Set<Entities.Order>().SingleOrDefaultAsync(row => row.Id == order && row.ReviewCode == "cost_limit", cancel) ?? throw new InvalidOperationException("Supplier cost hold was superseded."); accepted.ReviewCode = null; accepted.FulfillmentStatus = "acquiring"; accepted.Revision++; accepted.UpdatedAt = DateTime.UtcNow; }
                return true;
            }, token);
            if (!prior) await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request), command, DateTime.UtcNow, token);
            return 0;
        }, ct);
    }

    public Task ApproveCompleteAsync(AuthenticatedAdmin actor, BrandContext context, Guid order,
        Guid request, long revision, Guid command, string reason, CancellationToken ct = default)
    {
        if (request == Guid.Empty || order == Guid.Empty || command == Guid.Empty || revision < 0 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || reason.Any(char.IsControl))
            throw new ArgumentException("Invalid supplier completion review.");
        var audit = new AuditCommand(command, AuditAction.ManualFulfillment, AuditEntity.Order, order,
            context.BrandId, AuditReason.OperatorReview, command,
            AdminCommand.Hash("supplier-complete-review-v1", request.ToString("D"), reason),
            revision, revision + 1);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (await AuditReplayAsync(tx, actor, audit, token)) return 0;
            int? count = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from r in data.Data.Set<Entities.SupplierRequest>() join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where r.Id == request && o.Id == order && b.Id == context.BrandId && b.BotId == context.BotId && b.Active && o.Revision == revision && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && o.FulfillmentStatus == "requires_admin" && o.ReviewCode == "partial_acquisition" && o.ReleaseAuthorizedAt == null && o.DeliveryGeneration == null && r.Status == "succeeded" && r.ReviewCode == null && r.RemoteRef != null && r.EvidenceRef != null && data.Data.Set<Entities.Fulfillment>().Count(f => f.OrderId == order) == o.Quantity && data.Data.Set<Entities.Fulfillment>().Count(f => f.SupplierRequestId == request && f.Kind == "supplier" && f.Status == "acquired") == r.RequestedQuantity && data.Data.Set<Entities.SupplierRequestAttempt>().Any(a => a.SupplierRequestId == request && a.Outcome == "succeeded" && a.ErrorCode == null && a.EvidenceRef == r.EvidenceRef && a.RemoteRef != null && a.FinishedAt != null) select (int?)o.Quantity).SingleOrDefaultAsync(cancel), token);
            int quantity = count ?? throw new InvalidOperationException("Complete verified supplier acquisition requires current review.");
            var slots = new HashSet<int>();
            int offset = 0;
            while (true)
            {
                var contents = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => data.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order).OrderBy(row => row.UnitNo).Skip(offset).Take(1000).Select(row => new { row.Id, row.UnitNo, row.DeliveredContent, row.DeliveredContentKeyId, row.ContentKind }).ToListAsync(cancel), token);
                foreach (var row in contents)
                {
                    if (!slots.Add(row.UnitNo) || row.DeliveredContent == null || row.DeliveredContentKeyId == null || row.ContentKind == null) throw new InvalidOperationException("Incomplete verified supplier manifest.");
                    byte[] plain = new BrandCipher(secrets).Decrypt(context, "fulfillments", row.Id, "delivered_content", row.DeliveredContentKeyId, row.DeliveredContent);
                    try { if (plain.Length == 0) throw new InvalidOperationException("Empty supplier credential."); } finally { CryptographicOperations.ZeroMemory(plain); }
                }
                if (contents.Count < 1000) break; offset += contents.Count;
            }
            if (slots.Count != quantity || Enumerable.Range(1, quantity).Any(n => !slots.Contains(n)))
                throw new InvalidOperationException("Incomplete supplier unit allocation.");
            await WriteAuditAsync(tx, actor, audit, token);
            await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) => { await data.RequireOrderCommandAsync(context, order, cancel); var row = await data.Data.Set<Entities.Order>().SingleOrDefaultAsync(row => row.Id == order && row.Revision == revision && row.ReviewCode == "partial_acquisition", cancel) ?? throw new InvalidOperationException("Supplier completion review was superseded."); row.FulfillmentStatus = "acquiring"; row.ReviewCode = null; row.Revision++; row.UpdatedAt = DateTime.UtcNow; return true; }, token);
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Order(JobKind.PrepareManifest, context.BrandId, order), order,
                DateTime.UtcNow, token);
            return 0;
        }, ct);
    }

    public Task<SupplierItem> OpenBonusAsync(AuthenticatedAdmin actor, BrandContext context, Guid request,
        Guid inventory, Guid command, CancellationToken ct = default)
    {
        if (request == Guid.Empty || inventory == Guid.Empty || command == Guid.Empty)
            throw new ArgumentException("Invalid supplier bonus access.");
        var audit = new AuditCommand(command, AuditAction.Maintenance, AuditEntity.SupplierRequest, request,
            context.BrandId, AuditReason.OperatorReview, command,
            AdminCommand.Hash("supplier-bonus-access-v1", request.ToString("D"), inventory.ToString("D")));
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            bool replay = await AuditReplayAsync(tx, actor, audit, token);
            var original = await SqliteSessionFactory.StepEfAsync(tx, false, (data, cancel) => (from i in data.Data.Set<Entities.InventoryItem>() join r in data.Data.Set<Entities.SupplierRequest>() on i.SupplierRequestId equals r.Id join o in data.Data.Set<Entities.Order>() on r.OrderId equals o.Id join u in data.Data.Set<Entities.User>() on o.UserId equals u.Id join b in data.Data.Set<Entities.Brand>() on u.BrandId equals b.Id where i.Id == inventory && r.Id == request && i.State == "admin_held" && i.OriginKind == "supplier_bonus" && b.Id == context.BrandId && b.BotId == context.BotId select new { Reference = i.RemoteItemRef!, Encrypted = i.CredentialCiphertext, Key = i.CredentialKeyId }).SingleOrDefaultAsync(cancel), token) ?? throw new UnauthorizedAccessException("Protected supplier bonus is unavailable.");
            string reference = original.Reference, key = original.Key; byte[] encrypted = original.Encrypted;
            if (!replay) await WriteAuditAsync(tx, actor, audit, token);
            byte[] plain = new InventoryCipher(secrets).Decrypt(inventory, key, encrypted);
            try { return new SupplierItem(reference, SupplierItemDisposition.Bonus, "code", plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }, ct);
    }

    private static Task<bool> AuditReplayAsync(SqliteTransaction tx, AuthenticatedAdmin actor, AuditCommand audit, CancellationToken ct) =>
        SqliteSessionFactory.StepEfAsync(tx, false, (data, token) => AdminAuditStore.ReplayEfAsync(data, actor, audit, token), ct);
    private static async Task WriteAuditAsync(SqliteTransaction tx, AuthenticatedAdmin actor, AuditCommand audit, CancellationToken ct)
    {
        if (!await AdminAuditStore.AppendSqliteAsync(tx, actor, audit, ct)) throw new UnauthorizedAccessException("Supplier review audit identity conflict.");
    }
}
