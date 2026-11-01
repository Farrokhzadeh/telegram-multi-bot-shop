using System.Security.Cryptography;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SupplierAttempt(SupplierBinding Binding, Guid Id, Guid? ParentSubmit, string Operation, string? PurchaseReference);
public sealed class SupplierRequestStore(DbSessionFactory sessions, SecretSnapshot secrets, long? trustedStaffChat = null)
{
    private const string BindingColumns = "r.id,r.order_id,o.product_id,r.product_supplier_source_id,r.source_revision,r.provider_code,r.account_ref,r.external_product_id,r.external_variant_id,r.local_intent_key,r.requested_quantity,r.cost_baseline_amount::text,r.cost_currency,r.approved_cost_ceiling::text";
    public async Task<SupplierBinding> ReadAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Require(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        return await session.QuerySingleAsync(new DbStatement("SELECT " + BindingColumns + " FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE r.id=$1 AND b.id=$2 AND b.bot_id=$3",
            DbValue.Uuid(lease.Intent.OriginId!.Value), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), ReadBinding, ct);
    }
    public async Task<SupplierAttempt?> BeginAsync(BrandContext context, JobLease lease, SupplierQuote? quote, CancellationToken ct = default)
    {
        Require(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var binding = await LockAsync(session, context, lease.Intent.OriginId!.Value, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(2), ct);
            var state = await session.QuerySingleAsync(new DbStatement("SELECT r.status,o.review_code,r.remote_ref,s.enabled AND s.revision=r.source_revision,clock_timestamp() FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id JOIN shop.product_supplier_sources s ON s.id=r.product_supplier_source_id WHERE r.id=$1", DbValue.Uuid(binding.RequestId)), r => (Status: r.GetString(0), Review: r.IsDBNull(1) ? null : r.GetString(1), Remote: r.IsDBNull(2) ? null : r.GetString(2), Source: r.GetBoolean(3), Now: r.GetUtc(4)), ct);
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
            Guid? parent = submit ? null : await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.supplier_request_attempts WHERE supplier_request_id=$1 AND operation='submit' ORDER BY attempt_no DESC LIMIT 1", DbValue.Uuid(binding.RequestId)), r => r.GetGuid(0), ct);
            Guid attempt = AttemptId(lease.Id, lease.Attempt);
            string operation = submit ? "submit" : lease.Intent.Kind == JobKind.ExportSupplier ? "export" : "status_lookup";
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.supplier_request_attempts(id,supplier_request_id,attempt_no,operation,parent_submit_attempt_id,admin_authorization_audit_id,max_charge_amount)
                SELECT $1,$2,COALESCE(max(attempt_no),0)+1,$3,$4,$5,$6 FROM shop.supplier_request_attempts WHERE supplier_request_id=$2
                """, DbValue.Uuid(attempt), DbValue.Uuid(binding.RequestId), DbValue.Text(operation), AdminCommand.Id(parent), AdminCommand.Id(submit ? lease.Intent.AuditId : null),
                submit ? DbValue.Numeric(SupplierRules.UnitCeiling(binding)) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric)), ct);
            if (submit) await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='in_flight',updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(binding.RequestId)), ct);
            await session.CommitAsync(ct); return new(binding, attempt, parent, operation, state.Remote);
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
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromMinutes(1), ct);
            bool active = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.supplier_request_attempts WHERE id=$1 AND supplier_request_id=$2 AND operation=$3 AND outcome='in_flight' AND parent_submit_attempt_id IS NOT DISTINCT FROM $4)", DbValue.Uuid(attempt.Id), DbValue.Uuid(binding.RequestId), DbValue.Text(attempt.Operation), AdminCommand.Id(attempt.ParentSubmit)), r => r.GetBoolean(0), ct);
            if (!active) throw new InvalidOperationException("Supplier attempt is stale.");
            string requestStatus = await session.QuerySingleAsync(new DbStatement("SELECT status FROM shop.supplier_requests WHERE id=$1", DbValue.Uuid(binding.RequestId)), r => r.GetString(0), ct);
            if (requestStatus is "succeeded" or "resolved")
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome='unknown',error_code='terminal_obligation',evidence_ref=$1,finished_at=clock_timestamp() WHERE id=$2", AdminCommand.Text(evidence), DbValue.Uuid(attempt.Id)), ct);
                await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return;
            }
            string? review = SupplierRules.Validate(binding, result);
            bool archived = !string.IsNullOrWhiteSpace(evidence) && result.Evidence.Length > 0;
            bool identity = SupplierRules.Identity(binding, result) is null;
            string? prior = await session.QuerySingleAsync(new DbStatement("SELECT remote_ref FROM shop.supplier_requests WHERE id=$1", DbValue.Uuid(binding.RequestId)), r => r.IsDBNull(0) ? null : r.GetString(0), ct);
            if (prior is not null && result.PurchaseReference is not null && prior != result.PurchaseReference) { identity = false; review = "identity_mismatch"; }
            if (!archived) review = "credential_recovery";
            if (identity && archived && result.Items.Count > 0)
            {
                if (await ExistingItemsMatchAsync(session, context, binding, result, ct)) await CaptureAsync(session, context, binding, result, ct);
                else { identity = false; review = "identity_mismatch"; }
            }
            bool acquired = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.fulfillments WHERE supplier_request_id=$1 AND status<>'unfilled')", DbValue.Uuid(binding.RequestId)), r => r.GetBoolean(0), ct);
            if (acquired && result.Kind == SupplierOutcomeKind.ConfirmedNoEffect) { identity = false; review = "identity_mismatch"; }
            bool noEffect = archived && identity && result.Kind == SupplierOutcomeKind.ConfirmedNoEffect;
            bool complete = archived && identity && review is null;
            string outcome = noEffect ? "failed" : complete ? "succeeded" : "unknown";
            await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome=$1,error_code=$2,remote_ref=$3,evidence_ref=$4,finished_at=clock_timestamp() WHERE id=$5",
                DbValue.Text(outcome), AdminCommand.Text(noEffect ? "confirmed_no_effect" : review), AdminCommand.Text(identity ? result.PurchaseReference : null), AdminCommand.Text(archived ? evidence : null), DbValue.Uuid(attempt.Id)), ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            if (complete)
            {
                var reviews = await session.QuerySingleAsync(new DbStatement("SELECT o.review_code,r.review_code FROM shop.orders o JOIN shop.supplier_requests r ON r.order_id=o.id WHERE r.id=$1", DbValue.Uuid(binding.RequestId)), r => (Order: r.IsDBNull(0) ? null : r.GetString(0), Request: r.IsDBNull(1) ? null : r.GetString(1)), ct);
                bool transient = reviews.Order == "provider_unknown" && reviews.Request == "provider_unknown";
                bool protectedReview = reviews.Order is not null && !transient;
                if (transient) await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET review_code=NULL,fulfillment_status='acquiring',revision=revision+1 WHERE id=$1", DbValue.Uuid(binding.OrderId)), ct);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='succeeded',remote_ref=COALESCE(remote_ref,$1),evidence_ref=$2,review_code=NULL,updated_at=clock_timestamp() WHERE id=$3", AdminCommand.Text(result.PurchaseReference), DbValue.Text(evidence!), DbValue.Uuid(binding.RequestId)), ct);
                if (!protectedReview) await OutboxStore.EnqueueAsync(session, JobIntent.Order(JobKind.PrepareManifest, context.BrandId, binding.OrderId), binding.OrderId, DateTime.UtcNow, ct);
                else await NoticeAsync(session, context, binding, attempt.Id, false, ct);
            }
            else
            {
                string status = noEffect ? "failed" : result.Items.Count > 0 && archived && identity ? "partial_pending_admin" : "unknown";
                if (identity && result.PurchaseReference is not null) await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET remote_ref=COALESCE(remote_ref,$1) WHERE id=$2", DbValue.Text(result.PurchaseReference), DbValue.Uuid(binding.RequestId)), ct);
                await ReviewAsync(session, context, binding, status, review ?? "provider_unknown", attempt.Id, archived ? evidence : null, ct);
                if (!noEffect && status == "unknown") await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, binding.RequestId), attempt.Id, DateTime.UtcNow.AddSeconds(5), ct);
            }
            bool bonus = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.inventory_items WHERE supplier_request_id=$1 AND origin_kind='supplier_bonus')", DbValue.Uuid(binding.RequestId)), r => r.GetBoolean(0), ct);
            if (bonus) await NoticeAsync(session, context, binding, attempt.Id, true, ct);
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private async Task<bool> ExistingItemsMatchAsync(DbSession session, BrandContext context, SupplierBinding binding, SupplierOutcome result, CancellationToken ct)
    {
        foreach (var item in result.Items)
        {
            var existing = await session.QueryManyAsync(new DbStatement("""
                SELECT id,supplier_request_id,'customer',delivered_content,delivered_content_key_id,content_kind FROM shop.fulfillments WHERE supplier_item_key=$1
                UNION ALL SELECT id,supplier_request_id,'bonus',credential_ciphertext,credential_key_id,NULL::text FROM shop.inventory_items WHERE supplier_item_key=$1
                """, DbValue.Binary(SupplierRules.ItemKey(binding, item.Reference))), r => (Id: r.GetGuid(0), Request: r.GetGuid(1), Kind: r.GetString(2), Bytes: r.GetBinary(3), Key: r.GetString(4), ContentKind: r.IsDBNull(5) ? null : r.GetString(5)), 2, ct);
            if (existing.Count == 0) continue;
            if (existing.Count != 1 || existing[0].Request != binding.RequestId || existing[0].Kind != (item.Disposition == SupplierItemDisposition.Customer ? "customer" : "bonus")) return false;
            var value = existing[0];
            if (value.ContentKind is not null && value.ContentKind != item.ContentKind) return false;
            byte[] plain = value.Kind == "customer" ? new BrandCipher(secrets).Decrypt(context, "fulfillments", value.Id, "delivered_content", value.Key, value.Bytes) : new InventoryCipher(secrets).Decrypt(value.Id, value.Key, value.Bytes);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, item.Content.Span)) return false; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return true;
    }
    private async Task CaptureAsync(DbSession session, BrandContext context, SupplierBinding binding, SupplierOutcome result, CancellationToken ct)
    {
        foreach (var item in result.Items.OrderBy(i => i.Reference, StringComparer.Ordinal))
        {
            byte[] key = SupplierRules.ItemKey(binding, item.Reference);
            bool exists = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.fulfillments WHERE supplier_item_key=$1) OR EXISTS(SELECT 1 FROM shop.inventory_items WHERE supplier_item_key=$1)", DbValue.Binary(key)), r => r.GetBoolean(0), ct);
            if (exists) continue;
            if (item.Disposition == SupplierItemDisposition.Customer)
            {
                Guid id = await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.fulfillments WHERE order_id=$1 AND status='unfilled' ORDER BY unit_no LIMIT 1 FOR UPDATE", DbValue.Uuid(binding.OrderId)), r => r.GetGuid(0), ct);
                byte[] cipher = new BrandCipher(secrets).Encrypt(context, "fulfillments", id, "delivered_content", item.Content.Span);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.fulfillments SET kind='supplier',status='acquired',supplier_request_id=$1,remote_item_ref=$2,supplier_item_key=$3,delivered_content=$4,delivered_content_key_id=$5,content_kind=$6 WHERE id=$7",
                    DbValue.Uuid(binding.RequestId), DbValue.Text(item.Reference), DbValue.Binary(key), DbValue.Binary(cipher), DbValue.Text(secrets.CurrentBrandKeyId(context)), DbValue.Text(item.ContentKind), DbValue.Uuid(id)), ct);
            }
            else
            {
                Guid id = Guid.NewGuid(); byte[] cipher = new InventoryCipher(secrets).Encrypt(id, item.Content.Span);
                byte[] fingerprint = secrets.UseInventoryKey(secrets.CurrentInventoryKeyId, k => HMACSHA256.HashData(k, item.Content.Span));
                await session.ExecuteAsync(new DbStatement("INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind,supplier_request_id,remote_item_ref,supplier_item_key,state) VALUES ($1,$2,$3,$4,$5,'supplier_bonus',$6,$7,$8,'admin_held')",
                    DbValue.Uuid(id), DbValue.Uuid(binding.ProductId), DbValue.Binary(cipher), DbValue.Text(secrets.CurrentInventoryKeyId), DbValue.Binary(fingerprint), DbValue.Uuid(binding.RequestId), DbValue.Text(item.Reference), DbValue.Binary(key)), ct);
            }
        }
    }
    private async Task ReviewAsync(DbSession session, BrandContext context, SupplierBinding binding, string status, string review, Guid generation, string? evidence, CancellationToken ct)
    {
        await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status=$1,review_code=$2,evidence_ref=COALESCE($3,evidence_ref),updated_at=clock_timestamp() WHERE id=$4", DbValue.Text(status), DbValue.Text(review), AdminCommand.Text(evidence), DbValue.Uuid(binding.RequestId)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='requires_admin',review_code=COALESCE(review_code,$1),revision=revision+1,updated_at=clock_timestamp() WHERE id=$2", DbValue.Text(review), DbValue.Uuid(binding.OrderId)), ct);
        await NoticeAsync(session, context, binding, generation, false, ct);
    }
    private async Task NoticeAsync(DbSession session, BrandContext context, SupplierBinding binding, Guid generation, bool bonus, CancellationToken ct)
    {
        Guid audit = new(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(generation.ToString("D") + (bonus ? ":bonus" : ":case"))).AsSpan(0, 16));
        await session.ExecuteAsync(new DbStatement("INSERT INTO shop.audit_events(id,actor_kind,brand_id,action,entity_kind,entity_id,reason,correlation_id) VALUES ($1,'system',$2,'update','supplier_request',$3,$4,$5) ON CONFLICT(id) DO NOTHING",
            DbValue.Uuid(audit), DbValue.Uuid(context.BrandId), DbValue.Uuid(binding.RequestId), DbValue.Text(bonus ? "Protected supplier bonus acquired" : "Supplier obligation requires review"), DbValue.Uuid(generation)), ct);
        if (trustedStaffChat is not < 0) return;
        Guid delivery = await session.QuerySingleAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,staff_chat_id,purpose,action,dedupe_key,supplier_request_id,audit_event_id,delivery_generation)
            VALUES ($1,$2,'staff_chat',$3,$4,'send',$5,$6,$7,$8) ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=EXCLUDED.dedupe_key RETURNING id
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(context.BrandId), DbValue.Bigint(trustedStaffChat.Value), DbValue.Text(bonus ? "staff_supplier_bonus" : "staff_supplier_case"), DbValue.Text("supplier-notice:" + audit.ToString("D")), DbValue.Uuid(binding.RequestId), DbValue.Uuid(audit), DbValue.Uuid(generation)), r => r.GetGuid(0), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, delivery), delivery, DateTime.UtcNow, ct);
    }
    public async Task<bool> RecoverExpiredAsync(BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            var candidates = await session.QueryManyAsync(new DbStatement("SELECT id,supplier_request_id,attempts FROM shop.jobs WHERE brand_id=$1 AND job_kind IN ('submit_supplier','reconcile_supplier','export_supplier') AND status='leased' AND lease_until<=clock_timestamp() ORDER BY lease_until,id LIMIT 1", DbValue.Uuid(context.BrandId)), r => (Id: r.GetGuid(0), Request: r.GetGuid(1), Number: r.GetInt64(2)), 1, ct);
            if (candidates.Count == 0) return false;
            var job = candidates[0];
            var binding = await LockAsync(session, context, job.Request, ct);
            var locked = await session.QueryManyAsync(new DbStatement("SELECT id FROM shop.jobs WHERE id=$1 AND attempts=$2 AND status='leased' AND lease_until<=clock_timestamp() FOR UPDATE", DbValue.Uuid(job.Id), DbValue.Bigint(job.Number)), r => r.GetGuid(0), 1, ct);
            if (locked.Count == 0) return false;
            Guid attempt = AttemptId(job.Id, job.Number);
            bool started = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.supplier_request_attempts WHERE id=$1 AND supplier_request_id=$2)", DbValue.Uuid(attempt), DbValue.Uuid(job.Request)), r => r.GetBoolean(0), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,completed_at=CASE WHEN $2 THEN clock_timestamp() ELSE NULL END,last_error_code=$3 WHERE id=$4", DbValue.Text(started ? "failed" : "retry"), DbValue.Boolean(started), AdminCommand.Text(started ? "remote_outcome_unknown" : null), DbValue.Uuid(job.Id)), ct);
            if (started)
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_request_attempts SET outcome='unknown',error_code='provider_unknown',finished_at=clock_timestamp() WHERE id=$1 AND outcome='in_flight'", DbValue.Uuid(attempt)), ct);
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
            bool safe = await session.QuerySingleAsync(new DbStatement("SELECT status='blocked' AND review_code='cost_limit' AND revision=$2 AND NOT EXISTS(SELECT 1 FROM shop.supplier_request_attempts WHERE supplier_request_id=$1 AND operation='submit') AND NOT EXISTS(SELECT 1 FROM shop.jobs WHERE supplier_request_id=$1 AND status='leased') FROM shop.supplier_requests WHERE id=$1", DbValue.Uuid(request), DbValue.Bigint(expectedRevision)), r => r.GetBoolean(0), ct);
            if (!safe || currency != binding.Currency || maximumUnitCost <= binding.UnitBaseline) throw new InvalidOperationException("Original cost review is unavailable.");
            await session.ExecuteAsync(new DbStatement("UPDATE shop.supplier_requests SET status='pending',review_code=NULL,approved_cost_ceiling=$1,approval_audit_id=$2,revision=revision+1,updated_at=clock_timestamp() WHERE id=$3", DbValue.Numeric(maximumUnitCost), DbValue.Uuid(command), DbValue.Uuid(request)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET review_code=NULL,fulfillment_status='acquiring',revision=revision+1 WHERE id=$1 AND review_code='cost_limit'", DbValue.Uuid(binding.OrderId)), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request), command, DateTime.UtcNow, ct);
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<SupplierItem> OpenBonusAsync(AuthenticatedAdmin actor, BrandContext context, Guid request, Guid item, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var data = await session.QuerySingleAsync(new DbStatement("SELECT i.remote_item_ref,i.credential_ciphertext,i.credential_key_id FROM shop.inventory_items i JOIN shop.supplier_requests r ON r.id=i.supplier_request_id JOIN shop.orders o ON o.id=r.order_id JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE i.id=$1 AND r.id=$2 AND i.state='admin_held' AND i.origin_kind='supplier_bonus' AND b.id=$3 AND b.bot_id=$4", DbValue.Uuid(item), DbValue.Uuid(request), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (Reference: r.GetString(0), Cipher: r.GetBinary(1), Key: r.GetString(2)), ct);
        await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Maintenance, AuditEntity.SupplierRequest, request, context.BrandId, AuditReason.OperatorReview, command, AdminCommand.Hash("supplier-bonus-access-v1", request.ToString("D"), item.ToString("D"))), ct);
        byte[] plain = new InventoryCipher(secrets).Decrypt(item, data.Key, data.Cipher);
        try { await session.CommitAsync(ct); return new(data.Reference, SupplierItemDisposition.Bonus, "code", plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static Guid AttemptId(Guid job, long number) => new(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("supplier-attempt-v1:" + job.ToString("D") + ":" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16));
    private static void Require(BrandContext context, JobLease lease)
    {
        if (lease.Intent.BrandId != context.BrandId || lease.Intent.Kind is not (JobKind.SubmitSupplier or JobKind.ReconcileSupplier or JobKind.ExportSupplier) || lease.Intent.OriginId is null) throw new UnauthorizedAccessException();
    }
    private static async Task<SupplierBinding> LockAsync(DbSession session, BrandContext context, Guid request, CancellationToken ct)
    {
        var id = await session.QuerySingleAsync(new DbStatement("SELECT o.user_id,o.product_id,o.id FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE r.id=$1 AND b.id=$2 AND b.bot_id=$3", DbValue.Uuid(request), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (Member: r.GetGuid(0), Product: r.GetGuid(1), Order: r.GetGuid(2)), ct);
        await session.ExecuteAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(id.Member)), ct);
        await session.ExecuteAsync(new DbStatement("SELECT id FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(id.Product)), ct);
        bool accepted = await session.QuerySingleAsync(new DbStatement("SELECT payment_status IN ('paid','not_required') AND fulfillment_status NOT IN ('cancelled','compensated','completed') AND hold_at IS NULL FROM shop.orders WHERE id=$1 FOR UPDATE", DbValue.Uuid(id.Order)), r => r.GetBoolean(0), ct);
        if (!accepted) throw new InvalidOperationException("Supplier obligation is terminal or held.");
        return await session.QuerySingleAsync(new DbStatement("SELECT " + BindingColumns + " FROM shop.supplier_requests r JOIN shop.orders o ON o.id=r.order_id WHERE r.id=$1 FOR UPDATE OF r", DbValue.Uuid(request)), ReadBinding, ct);
    }
    private static SupplierBinding ReadBinding(DbRow r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetInt32(10), ExactDecimal.Parse(r.GetString(11)), r.GetString(12), r.IsDBNull(13) ? null : ExactDecimal.Parse(r.GetString(13)));
}
