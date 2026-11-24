using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record AdminOrder(Guid Id, Guid BrandId, Guid MemberId, Guid? ProductId, int Quantity, string Payment, string Fulfillment, bool Preorder, bool Held, string? Review, long Revision, string Title, DateTime CreatedAt);
public sealed record WalletAdjustment(Guid MemberId, string AmountUsd, long ExpectedVersion, string Reason, Guid CommandId);
public sealed record OrderResolution(string Action, long ExpectedRevision, string Reason, Guid CommandId, Guid? SupplierRequestId = null, long? ExpectedRequestRevision = null, string? BankCreditUsd = null);
public sealed class OrderAdministrationStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<AdminPage<AdminOrder>> SearchAsync(AuthenticatedAdmin actor, Guid? brand, Guid? member, Guid? order, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.RunEfStepAsync(false, (data, token) => (from value in data.Data.Set<Entities.Order>() join user in data.Data.Set<Entities.User>() on value.UserId equals user.Id where (brand == null || user.BrandId == brand) && (member == null || value.UserId == member) && (order == null || value.Id == order) && (after == null || value.Id.CompareTo(after.Value) > 0) orderby value.Id select new AdminOrder(value.Id, user.BrandId, value.UserId, value.ProductId, value.Quantity, value.PaymentStatus, value.FulfillmentStatus, value.IsPreorder, value.HoldAt != null, value.ReviewCode, value.Revision, value.ProductTitleSnapshot, value.CreatedAt)).Take(limit + 1).ToListAsync(token), ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<Guid> AdjustAsync(AuthenticatedAdmin actor, BrandContext context, WalletAdjustment value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Reason(value.Reason); AdminCommand.Revision(value.ExpectedVersion);
        Money delta = new(ExactDecimal.Parse(value.AmountUsd), Currency.Usd);
        if (delta.Amount == 0) throw new ArgumentException("A nonzero USD amount is required.");
        byte[] hash = AdminCommand.Hash("wallet-adjustment-v1", ExactDecimal.Format(delta.Amount), value.ExpectedVersion.ToString(CultureInfo.InvariantCulture), value.Reason);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(value.CommandId, AuditAction.WalletAdjustment, AuditEntity.Wallet, value.MemberId, context.BrandId, AuditReason.OperatorReview, value.CommandId, hash, value.ExpectedVersion, value.ExpectedVersion + 1), ct)) return value.CommandId;
            await MemberAsync(session, context, value.MemberId, ct);
            await session.AdmitWriterAsync(context, ct);
            await PostAsync(session, actor, value.MemberId, delta, value.ExpectedVersion, value.CommandId, "admin_adjustment", null, null, value.Reason, ct);
            await session.CommitAsync(ct); return value.CommandId;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task ResolveAsync(AuthenticatedAdmin actor, BrandContext context, Guid order, OrderResolution value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(value.ExpectedRevision); AdminCommand.Reason(value.Reason);
        AuditAction action = value.Action switch
        {
            "pause_preorder" => AuditAction.HoldPreorder,
            "resume_preorder" => AuditAction.ResumePreorder,
            "cancel_local_preorder" => AuditAction.CancelPreorder,
            "retry_supplier" => AuditAction.RetrySupplier,
            "credit_supplier_failure" => AuditAction.SupplierCredit,
            _ => throw new ArgumentException("Unsupported resolution.")
        };
        bool retry = action == AuditAction.RetrySupplier;
        if (value.SupplierRequestId == Guid.Empty || (action is AuditAction.RetrySupplier or AuditAction.SupplierCredit && (value.SupplierRequestId is null || value.ExpectedRequestRevision is null))) throw new ArgumentException("Supplier resolution requires the original request and revision.");
        if (value.ExpectedRequestRevision is long requestRevision) AdminCommand.Revision(requestRevision);
        byte[] hash = AdminCommand.Hash("order-resolution-v1", order.ToString("D"), value.Action, value.ExpectedRevision.ToString(CultureInfo.InvariantCulture), value.Reason, value.SupplierRequestId?.ToString("D"), value.ExpectedRequestRevision?.ToString(CultureInfo.InvariantCulture), value.BankCreditUsd);
        long revision = retry ? value.ExpectedRequestRevision!.Value : value.ExpectedRevision;
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(value.CommandId, action, retry ? AuditEntity.SupplierRequest : AuditEntity.Order, retry ? value.SupplierRequestId : order, context.BrandId, AuditReason.OperatorReview, value.CommandId, hash, revision, revision + 1), ct)) return;
            var identity = await session.RunEfStepAsync(false, (data, token) => (from value in data.Data.Set<Entities.Order>() join user in data.Data.Set<Entities.User>() on value.UserId equals user.Id where value.Id == order && user.BrandId == context.BrandId select new { Member = user.Id, Product = value.ProductId!.Value }).SingleAsync(token), ct);
            await MemberAsync(session, context, identity.Member, ct);
            await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(identity.Product, token), ct);
            await session.AdmitWriterAsync(context, ct);
            var data = await session.RunEfStepAsync(false, async (ef, token) =>
            {
                await ef.LockOrderAsync(order, token);
                return await ef.Data.Set<Entities.Order>().Where(value => value.Id == order).Select(value => new State(value.Revision, value.PaymentStatus, value.FulfillmentStatus, value.IsPreorder, value.HoldAt != null, value.ReleaseAuthorizedAt != null, value.ResolutionKind, value.PaymentMethod, value.UnitPriceUsdt, value.Quantity, value.DiscountPctSnapshot ?? 0m)).SingleAsync(token);
            }, ct);
            if (data.Revision != value.ExpectedRevision || data.Payment is not ("paid" or "not_required") || data.Released || data.Resolution is not null || data.Fulfillment is "completed" or "cancelled" or "compensated") throw new InvalidOperationException("Order is no longer eligible for resolution.");
            if (action is AuditAction.HoldPreorder or AuditAction.ResumePreorder or AuditAction.CancelPreorder)
            {
                if (!data.Preorder || data.Fulfillment is not ("waiting_stock" or "requires_admin") || await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.Fulfillment>().AnyAsync(value => value.OrderId == order && value.Status != "unfilled", token), ct)) throw new InvalidOperationException("Preorder allocation already started.");
                if (action == AuditAction.HoldPreorder && !data.Held)
                    await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, now) => { row.HoldAt = now; row.HoldAdminId = actor.AdminId; row.HoldReason = value.Reason; }, ct);
                else if (action == AuditAction.ResumePreorder && data.Held)
                    await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, _) => { row.HoldAt = null; row.HoldAdminId = null; row.HoldReason = null; }, ct);
                else if (action == AuditAction.CancelPreorder)
                {
                    await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, now) => { row.FulfillmentStatus = "cancelled"; row.HoldAt = null; row.HoldAdminId = null; row.HoldReason = null; row.ResolutionKind = "local_preorder_cancellation"; row.ResolutionAdminId = actor.AdminId; row.ResolutionAt = now; row.ResolutionAuditEventId = value.CommandId; }, ct);
                    await ReleaseAsync(session, context, order, ct);
                }
                else throw new InvalidOperationException("Preorder hold state conflict.");
                await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, identity.Product), value.CommandId, DateTime.UtcNow, ct);
            }
            else
            {
                Guid request = value.SupplierRequestId!.Value;
                var remote = await session.RunEfStepAsync(false, async (ef, token) =>
                {
                    await ef.LockRequestAsync(request, token);
                    return await ef.Data.Set<Entities.SupplierRequest>().Where(row => row.Id == request && row.OrderId == order).Select(row => new { row.Revision, row.Status, Cost = row.CostBaselineAmount != null, Override = row.ApprovalAuditId != null }).SingleAsync(token);
                }, ct);
                if (remote.Revision != value.ExpectedRequestRevision || remote.Status != "failed") throw new InvalidOperationException("Supplier outcome requires reconciliation or cost review.");
                bool unsafeEffect = await session.RunEfStepAsync(false, async (ef, token) =>
                    await ef.Data.Set<Entities.Job>().AnyAsync(row => row.SupplierRequestId == request && row.Status == "leased", token) ||
                    await ef.Data.Set<Entities.Fulfillment>().AnyAsync(row => row.SupplierRequestId == request && row.Status != "unfilled", token) ||
                    await ef.Data.Set<Entities.SupplierRequestAttempt>().AnyAsync(row => row.SupplierRequestId == request && row.Operation == "submit" && !((row.Outcome == "failed" && row.ErrorCode == "confirmed_no_effect" && row.EvidenceRef != null) || ((row.Outcome == "unknown" || row.Outcome == "timeout") && ef.Data.Set<Entities.SupplierRequestAttempt>().Any(lookup => lookup.ParentSubmitAttemptId == row.Id && lookup.Operation == "status_lookup" && lookup.Outcome == "failed" && lookup.ErrorCode == "confirmed_no_effect" && lookup.EvidenceRef != null))), token), ct);
                if (unsafeEffect) throw new InvalidOperationException("Supplier effects remain unresolved.");
                await session.RunEfStepAsync(true, async (ef, token) =>
                {
                    await ef.RequireOrderCommandAsync(context, order, token);
                    while (true)
                    {
                        var jobs = await ef.Data.Set<Entities.Job>().Where(row => row.SupplierRequestId == request && (row.Status == "pending" || row.Status == "retry")).OrderBy(row => row.Id).Take(128).ToArrayAsync(token);
                        if (jobs.Length == 0) break;
                        DateTime now = await ef.BrandClockAsync(context.BrandId, token); foreach (var job in jobs) { await ef.LockJobAsync(job.Id, token); await ef.Data.Entry(job).ReloadAsync(token); if (job.Status is "pending" or "retry") { job.Status = "cancelled"; job.CompletedAt = now; } }
                        await ef.FlushAsync(token, false);
                    }
                    return true;
                }, ct);
                if (retry)
                {
                    if (!remote.Cost) throw new InvalidOperationException("Original supplier cost is unverified.");
                    await SupplierQueries.ChangeAsync(session, context, request, (row, now) => { row.Status = "pending"; row.ReviewCode = null; row.Revision++; row.UpdatedAt = now; }, ct);
                    await PaymentSettlementStore.FulfillmentAsync(session, order, "acquiring", null, ct);
                    await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request, value.CommandId, remote.Revision + 1), value.CommandId, DateTime.UtcNow, ct);
                }
                else
                {
                    if (data.Method == "no_payment_required") throw new InvalidOperationException("A zero-money obligation cannot create a wallet credit.");
                    Money credit = data.Method == "bank" ? new(ExactDecimal.Parse(value.BankCreditUsd ?? throw new ArgumentException("An explicit confirmed USD credit is required.")), Currency.Usd) : PricingPolicy.DiscountTotal(data.Unit, data.Quantity, data.Percent, Currency.Usd);
                    if (credit.Amount <= 0 || (data.Method != "bank" && value.BankCreditUsd is not null)) throw new ArgumentException("Invalid credit input.");
                    await SupplierQueries.ChangeAsync(session, context, request, (row, now) => { row.Status = "resolved"; row.ReviewCode = null; row.Revision++; row.UpdatedAt = now; }, ct);
                    await session.RunEfStepAsync(true, async (ef, token) =>
                    {
                        await ef.RequireOrderCommandAsync(context, order, token);
                        while (true)
                        {
                            var units = await ef.Data.Set<Entities.Fulfillment>().Where(row => row.OrderId == order && row.Status == "acquired").Take(128).ToArrayAsync(token);
                            if (units.Length == 0) break;
                            foreach (var unit in units) { unit.Status = "retained_admin"; unit.ResolutionAdminId = actor.AdminId; unit.ResolutionNote = value.Reason; }
                            await ef.FlushAsync(token, false);
                        }
                        return true;
                    }, ct);
                    await PostAsync(session, actor, identity.Member, credit, null, value.CommandId, "supplier_wallet_credit", order, request, value.Reason, ct);
                    await PaymentSettlementStore.ChangeOrderAsync(session, order, (row, now) => { row.FulfillmentStatus = "compensated"; row.ReviewCode = null; row.ResolutionKind = "supplier_wallet_credit"; row.ResolutionAdminId = actor.AdminId; row.ResolutionAt = now; row.ResolutionAuditEventId = value.CommandId; row.ResolutionWalletEntryId = value.CommandId; }, ct);
                    await ReleaseAsync(session, context, order, ct);
                }
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task ReleaseAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Guid? coupon = await session.RunEfStepAsync(false, (ef, token) => ef.Data.Set<Entities.DiscountUse>().Where(row => row.OrderId == order && row.Status == "held").Select(row => (Guid?)row.DiscountId).SingleOrDefaultAsync(token), ct);
        if (coupon is Guid id) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, id, order, false, ct);
    }
    private static async Task<Guid> MemberAsync(DbSession session, BrandContext context, Guid member, CancellationToken ct)
    {
        int count = await session.RunEfStepAsync(false, async (data, token) =>
        {
            if (!await data.Data.Set<Entities.Brand>().AnyAsync(value => value.Id == context.BrandId && value.BotId == context.BotId, token)) return 0;
            return await data.LockUserAsync(member, context.BrandId, token);
        }, ct);
        if (count != 1) throw new SafeOperationException(ErrorKind.NotFound);
        return member;
    }
    private static async Task PostAsync(DbSession session, AuthenticatedAdmin actor, Guid member, Money amount, long? expected, Guid entry, string movement, Guid? order, Guid? request, string reason, CancellationToken ct)
    {
        await session.RunEfStepAsync(true, async (data, token) =>
        {
            await data.RequireAdminAsync(actor, null, token);
            return await WalletOperations.PostAsync(data, member, amount, expected, new Entities.WalletEntry { Id = entry, MovementKind = movement, IdempotencyKey = "admin:" + entry.ToString("D"), OrderId = order, SupplierRequestId = request, AdminId = actor.AdminId, AuditEventId = entry, Reason = reason }, token);
        }, ct);
    }
    private sealed record State(long Revision, string Payment, string Fulfillment, bool Preorder, bool Held, bool Released, string? Resolution, string Method, decimal Unit, int Quantity, decimal Percent);
}
