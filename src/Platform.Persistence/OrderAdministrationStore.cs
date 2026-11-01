using System.Globalization;
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
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT o.id,u.brand_id,o.user_id,o.product_id,o.quantity,o.payment_status,o.fulfillment_status,o.is_preorder,o.hold_at IS NOT NULL,o.review_code,o.revision,o.product_title_snapshot,o.created_at
            FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE ($1 IS NULL OR u.brand_id=$1) AND ($2 IS NULL OR o.user_id=$2) AND ($3 IS NULL OR o.id=$3) AND ($4 IS NULL OR o.id>$4) ORDER BY o.id LIMIT $5
            """, AdminCommand.Id(brand), AdminCommand.Id(member), AdminCommand.Id(order), AdminCommand.Id(after), DbValue.Integer(limit + 1)),
            r => new AdminOrder(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10), r.GetString(11), r.GetUtc(12)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<Guid> AdjustAsync(AuthenticatedAdmin actor, BrandContext context, WalletAdjustment value, CancellationToken ct = default)
    {
        AdminCommand.Reason(value.Reason); AdminCommand.Revision(value.ExpectedVersion);
        Money delta = new(ExactDecimal.Parse(value.AmountUsd), Currency.Usd);
        if (delta.Amount == 0) throw new ArgumentException("A nonzero USD amount is required.");
        byte[] hash = AdminCommand.Hash("wallet-adjustment-v1", ExactDecimal.Format(delta.Amount), value.ExpectedVersion.ToString(CultureInfo.InvariantCulture), value.Reason);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(value.CommandId, AuditAction.WalletAdjustment, AuditEntity.Wallet, value.MemberId, context.BrandId, AuditReason.OperatorReview, value.CommandId, hash, value.ExpectedVersion, value.ExpectedVersion + 1), ct)) return value.CommandId;
            await MemberAsync(session, context, value.MemberId, ct);
            await PostAsync(session, actor, value.MemberId, delta, value.ExpectedVersion, value.CommandId, "admin_adjustment", null, null, value.Reason, ct);
            await session.CommitAsync(ct); return value.CommandId;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task ResolveAsync(AuthenticatedAdmin actor, BrandContext context, Guid order, OrderResolution value, CancellationToken ct = default)
    {
        AdminCommand.Revision(value.ExpectedRevision); AdminCommand.Reason(value.Reason);
        AuditAction action = value.Action switch { "pause_preorder" => AuditAction.HoldPreorder, "resume_preorder" => AuditAction.ResumePreorder, "cancel_local_preorder" => AuditAction.CancelPreorder,
            "retry_supplier" => AuditAction.RetrySupplier, "credit_supplier_failure" => AuditAction.SupplierCredit, _ => throw new ArgumentException("Unsupported resolution.") };
        bool retry = action == AuditAction.RetrySupplier;
        if (value.SupplierRequestId == Guid.Empty || (action is AuditAction.RetrySupplier or AuditAction.SupplierCredit && (value.SupplierRequestId is null || value.ExpectedRequestRevision is null))) throw new ArgumentException("Supplier resolution requires the original request and revision.");
        if (value.ExpectedRequestRevision is long requestRevision) AdminCommand.Revision(requestRevision);
        byte[] hash = AdminCommand.Hash("order-resolution-v1", order.ToString("D"), value.Action, value.ExpectedRevision.ToString(CultureInfo.InvariantCulture), value.Reason, value.SupplierRequestId?.ToString("D"), value.ExpectedRequestRevision?.ToString(CultureInfo.InvariantCulture), value.BankCreditUsd);
        long revision = retry ? value.ExpectedRequestRevision!.Value : value.ExpectedRevision;
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(value.CommandId, action, retry ? AuditEntity.SupplierRequest : AuditEntity.Order, retry ? value.SupplierRequestId : order, context.BrandId, AuditReason.OperatorReview, value.CommandId, hash, revision, revision + 1), ct)) return;
            var identity = await session.QuerySingleAsync(new DbStatement("SELECT o.user_id,o.product_id FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND u.brand_id=$2", DbValue.Uuid(order), DbValue.Uuid(context.BrandId)), r => (Member: r.GetGuid(0), Product: r.GetGuid(1)), ct);
            await MemberAsync(session, context, identity.Member, ct);
            await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(identity.Product)), r => r.GetGuid(0), ct);
            var data = await session.QuerySingleAsync(new DbStatement("""
                SELECT revision,payment_status,fulfillment_status,is_preorder,hold_at IS NOT NULL,release_authorized_at IS NOT NULL,resolution_kind,
                    payment_method,unit_price_usdt::text,quantity,COALESCE(discount_pct_snapshot,0)::text FROM shop.orders WHERE id=$1 FOR UPDATE
                """, DbValue.Uuid(order)), r => new State(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), ExactDecimal.Parse(r.GetString(8)), r.GetInt32(9), ExactDecimal.Parse(r.GetString(10))), ct);
            if (data.Revision != value.ExpectedRevision || data.Payment is not ("paid" or "not_required") || data.Released || data.Resolution is not null || data.Fulfillment is "completed" or "cancelled" or "compensated") throw new InvalidOperationException("Order is no longer eligible for resolution.");
            if (action is AuditAction.HoldPreorder or AuditAction.ResumePreorder or AuditAction.CancelPreorder)
            {
                if (!data.Preorder || data.Fulfillment is not ("waiting_stock" or "requires_admin") || await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.fulfillments WHERE order_id=$1 AND status<>'unfilled')", DbValue.Uuid(order)), r => r.GetBoolean(0), ct)) throw new InvalidOperationException("Preorder allocation already started.");
                if (action == AuditAction.HoldPreorder && !data.Held)
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET hold_at=clock_timestamp(),hold_admin_id=$1,hold_reason=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3", DbValue.Uuid(actor.AdminId), DbValue.Text(value.Reason), DbValue.Uuid(order)), ct);
                else if (action == AuditAction.ResumePreorder && data.Held)
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET hold_at=NULL,hold_admin_id=NULL,hold_reason=NULL,revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(order)), ct);
                else if (action == AuditAction.CancelPreorder)
                {
                    await session.ExecuteAsync(new DbStatement("""
                        UPDATE shop.orders SET fulfillment_status='cancelled',hold_at=NULL,hold_admin_id=NULL,hold_reason=NULL,resolution_kind='local_preorder_cancellation',
                            resolution_admin_id=$1,resolution_at=clock_timestamp(),resolution_audit_event_id=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3
                        """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(value.CommandId), DbValue.Uuid(order)), ct);
                    await ReleaseAsync(session, context, order, ct);
                }
                else throw new InvalidOperationException("Preorder hold state conflict.");
                await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, identity.Product), value.CommandId, DateTime.UtcNow, ct);
            }
            else
            {
                Guid request = value.SupplierRequestId!.Value;
                var remote = await session.QuerySingleAsync(new DbStatement("SELECT revision,status,cost_baseline_amount IS NOT NULL,approval_audit_id IS NOT NULL FROM shop.supplier_requests WHERE id=$1 AND order_id=$2 FOR UPDATE", DbValue.Uuid(request), DbValue.Uuid(order)), r => (Revision: r.GetInt64(0), Status: r.GetString(1), Cost: r.GetBoolean(2), Override: r.GetBoolean(3)), ct);
                if (remote.Revision != value.ExpectedRequestRevision || remote.Status != "failed" || remote.Override) throw new InvalidOperationException("Supplier outcome requires reconciliation or cost review.");
                bool unsafeEffect = await session.QuerySingleAsync(new DbStatement("""
                    SELECT EXISTS(SELECT 1 FROM shop.jobs WHERE supplier_request_id=$1 AND status='leased') OR
                        EXISTS(SELECT 1 FROM shop.fulfillments WHERE supplier_request_id=$1 AND status<>'unfilled') OR
                        EXISTS(SELECT 1 FROM shop.supplier_request_attempts a WHERE a.supplier_request_id=$1 AND a.operation='submit' AND
                            NOT ((a.outcome='failed' AND a.error_code='confirmed_no_effect' AND a.evidence_ref IS NOT NULL) OR
                                (a.outcome IN ('unknown','timeout') AND EXISTS(SELECT 1 FROM shop.supplier_request_attempts lookup WHERE lookup.parent_submit_attempt_id=a.id
                                    AND lookup.operation='status_lookup' AND lookup.outcome='failed' AND lookup.error_code='confirmed_no_effect' AND lookup.evidence_ref IS NOT NULL))))
                    """, DbValue.Uuid(request)), r => r.GetBoolean(0), ct);
                if (unsafeEffect) throw new InvalidOperationException("Supplier effects remain unresolved.");
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='cancelled',completed_at=clock_timestamp() WHERE supplier_request_id=$1 AND status IN ('pending','retry')", DbValue.Uuid(request)), ct);
                if (retry)
                {
                    if (!remote.Cost) throw new InvalidOperationException("Original supplier cost is unverified.");
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='pending',review_code=NULL,revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(request)), ct);
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='acquiring',review_code=NULL,revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(order)), ct);
                    await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request, value.CommandId, remote.Revision + 1), value.CommandId, DateTime.UtcNow, ct);
                }
                else
                {
                    if (data.Method == "no_payment_required") throw new InvalidOperationException("A zero-money obligation cannot create a wallet credit.");
                    Money credit = data.Method == "bank" ? new(ExactDecimal.Parse(value.BankCreditUsd ?? throw new ArgumentException("An explicit confirmed USD credit is required.")), Currency.Usd) : PricingPolicy.DiscountTotal(data.Unit, data.Quantity, data.Percent, Currency.Usd);
                    if (credit.Amount <= 0 || (data.Method != "bank" && value.BankCreditUsd is not null)) throw new ArgumentException("Invalid credit input.");
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='resolved',review_code=NULL,revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(request)), ct);
                    await session.ExecuteAsync(new DbStatement("UPDATE shop.fulfillments SET status='retained_admin',resolution_admin_id=$1,resolution_note=$2 WHERE order_id=$3 AND status='acquired'", DbValue.Uuid(actor.AdminId), DbValue.Text(value.Reason), DbValue.Uuid(order)), ct);
                    await PostAsync(session, actor, identity.Member, credit, null, value.CommandId, "supplier_wallet_credit", order, request, value.Reason, ct);
                    await session.ExecuteAsync(new DbStatement("""
                        UPDATE shop.orders SET fulfillment_status='compensated',review_code=NULL,resolution_kind='supplier_wallet_credit',resolution_admin_id=$1,resolution_at=clock_timestamp(),
                            resolution_audit_event_id=$2,resolution_wallet_entry_id=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3
                        """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(value.CommandId), DbValue.Uuid(order)), ct);
                    await ReleaseAsync(session, context, order, ct);
                }
            }
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task ReleaseAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Guid? coupon = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
        if (coupon is Guid id) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, id, order, false, ct);
    }
    private static Task<Guid> MemberAsync(DbSession session, BrandContext context, Guid member, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("SELECT u.id FROM shop.users u JOIN shop.brands b ON b.id=u.brand_id WHERE u.id=$1 AND b.id=$2 AND b.bot_id=$3 FOR UPDATE OF u", DbValue.Uuid(member), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetGuid(0), ct);
    private static async Task PostAsync(DbSession session, AuthenticatedAdmin actor, Guid member, Money amount, long? expected, Guid entry, string movement, Guid? order, Guid? request, string reason, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.wallets(user_id) VALUES ($1) ON CONFLICT(user_id) DO NOTHING", DbValue.Uuid(member)), ct);
        var wallet = await session.QuerySingleAsync(new DbStatement("SELECT balance::text,version FROM shop.wallets WHERE user_id=$1 FOR UPDATE", DbValue.Uuid(member)), r => (Balance: new Money(ExactDecimal.Parse(r.GetString(0)), Currency.Usd), Version: r.GetInt64(1)), ct);
        if (expected is long version && version != wallet.Version) throw new InvalidOperationException("Wallet version conflict.");
        Money balance = wallet.Balance.Add(amount);
        if (balance.Amount < 0) throw new InvalidOperationException("Wallet funds are insufficient.");
        await session.ExecuteAsync(new DbStatement("UPDATE shop.wallets SET balance=$1,version=version+1,updated_at=clock_timestamp() WHERE user_id=$2", DbValue.Numeric(balance.Amount), DbValue.Uuid(member)), ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,order_id,supplier_request_id,admin_id,audit_event_id,reason)
            VALUES ($1,$2,$3,$4,$5,$6,true,$7,$8,$9,$10,$1,$11)
            """, DbValue.Uuid(entry), DbValue.Uuid(member), DbValue.Numeric(amount.Amount), DbValue.Numeric(balance.Amount), DbValue.Bigint(checked(wallet.Version + 1)), DbValue.Text(movement), DbValue.Text("admin:" + entry.ToString("D")), AdminCommand.Id(order), AdminCommand.Id(request), DbValue.Uuid(actor.AdminId), DbValue.Text(reason)), ct);
    }
    private sealed record State(long Revision, string Payment, string Fulfillment, bool Preorder, bool Held, bool Released, string? Resolution, string Method, decimal Unit, int Quantity, decimal Percent);
}
