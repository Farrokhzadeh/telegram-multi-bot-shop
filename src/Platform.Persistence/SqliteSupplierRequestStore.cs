using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteSupplierRequestStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
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
                using var existing = SqliteWallet.Command(tx, """
                    SELECT 1 FROM supplier_request_attempts WHERE supplier_request_id=$request AND operation='submit' LIMIT 1
                    """, ("$request", state.Binding.RequestId));
                if (await existing.ExecuteScalarAsync(token) is not null)
                    throw new UnauthorizedAccessException("An original supplier submission already exists.");
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
                using var original = SqliteWallet.Command(tx, """
                    SELECT id,remote_ref FROM supplier_request_attempts
                    WHERE supplier_request_id=$request AND operation='submit'
                    ORDER BY attempt_no DESC LIMIT 1
                    """, ("$request", state.Binding.RequestId));
                await using var row = await original.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token))
                    throw new UnauthorizedAccessException("The original supplier attempt is unavailable.");
                parent = row.GetGuid(0);
                remote = row.IsDBNull(1) ? null : row.GetString(1);
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
            using var insert = SqliteWallet.Command(tx, """
                INSERT INTO supplier_request_attempts(id,supplier_request_id,attempt_no,operation,
                    parent_submit_attempt_id,admin_authorization_audit_id,max_charge_amount,purchase_idempotency_key)
                SELECT $id,$request,coalesce(max(attempt_no),0)+1,$operation,$parent,$audit,$charge,$key
                FROM supplier_request_attempts WHERE supplier_request_id=$request
                """, ("$id", attempt), ("$request", state.Binding.RequestId), ("$operation", operation),
                ("$parent", parent), ("$audit", submit ? lease.Intent.AuditId : null),
                ("$charge", submit ? ExactDecimal.Format(SupplierRules.UnitCeiling(state.Binding)) : null),
                ("$key", submit && usesPurchaseKey ? "purchase:" + attempt.ToString("D") : null));
            if (await insert.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Supplier attempt was not persisted.");
            if (submit)
            {
                using var active = SqliteWallet.Command(tx, """
                    UPDATE supplier_requests SET status='in_flight',updated_at=$now
                    WHERE id=$request AND status='pending'
                    """, ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$request", state.Binding.RequestId));
                if (await active.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Supplier request was superseded.");
            }
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
            using (var query = SqliteWallet.Command(tx, """
                SELECT operation,outcome,parent_submit_attempt_id FROM supplier_request_attempts
                WHERE id=$attempt AND supplier_request_id=$request
                """, ("$attempt", attempt.Id), ("$request", state.Binding.RequestId)))
            await using (var row = await query.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || row.GetString(0) != attempt.Operation ||
                    row.GetString(1) != "in_flight" ||
                    (row.IsDBNull(2) ? null : row.GetGuid(2)) != attempt.ParentSubmit)
                    throw new UnauthorizedAccessException("The supplier result is not an active attempt.");
            }
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            bool archived = evidence is not null && result.Evidence.Length > 0;
            string? review = SupplierRules.Validate(state.Binding, result);
            bool identity = SupplierRules.Identity(state.Binding, result) is null &&
                result.Items.All(item => item.Reference.Length <= 160);
            if (!identity) review = "identity_mismatch";
            if (!archived && (result.Kind != SupplierOutcomeKind.Unknown ||
                result.Evidence.Length > 0 || result.Items.Count > 0)) review = "credential_recovery";
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
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            using (var finish = SqliteWallet.Command(tx, """
                UPDATE supplier_request_attempts SET outcome=$outcome,error_code=$error,
                    remote_ref=$remote,evidence_ref=$evidence,finished_at=$now
                WHERE id=$attempt AND outcome='in_flight'
                """, ("$outcome", status), ("$error", noEffect ? "confirmed_no_effect" : review),
                ("$remote", identity ? result.PurchaseReference : null),
                ("$evidence", archived ? evidence : null), ("$now", now), ("$attempt", attempt.Id)))
                if (await finish.ExecuteNonQueryAsync(token) != 1)
                    throw new UnauthorizedAccessException("Supplier attempt was superseded.");
            if (complete)
            {
                using (var update = SqliteWallet.Command(tx, """
                    UPDATE supplier_requests SET status='succeeded',review_code=NULL,
                        remote_ref=coalesce(remote_ref,$remote),evidence_ref=$evidence,updated_at=$now
                    WHERE id=$request AND status IN ('pending','in_flight','unknown','partial_pending_admin')
                    """, ("$remote", result.PurchaseReference), ("$evidence", evidence), ("$now", now),
                    ("$request", state.Binding.RequestId)))
                    if (await update.ExecuteNonQueryAsync(token) != 1)
                        throw new InvalidOperationException("The original supplier obligation changed.");
                if (state.OrderReview is null || state.OrderReview == "provider_unknown")
                {
                    using var clear = SqliteWallet.Command(tx, """
                        UPDATE orders SET fulfillment_status='acquiring',review_code=NULL,
                            revision=revision+1,updated_at=$now
                        WHERE id=$order AND (review_code IS NULL OR review_code='provider_unknown')
                        """, ("$now", now), ("$order", state.Binding.OrderId));
                    if (await clear.ExecuteNonQueryAsync(token) != 1)
                        throw new InvalidOperationException("Supplier fulfillment was superseded.");
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Order(JobKind.PrepareManifest, context.BrandId, state.Binding.OrderId),
                        state.Binding.OrderId, DateTime.UtcNow, token);
                }
            }
            else
            {
                string code = review ?? (noEffect ? "provider_failed" : "provider_unknown");
                await ReviewAsync(tx, state, code, noEffect ? "failed" :
                    result.Items.Count > 0 && identity && archived ? "partial_pending_admin" : "unknown", token);
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
            using var find = SqliteWallet.Command(tx, """
                SELECT j.id,j.supplier_request_id,j.attempts,j.job_kind
                FROM jobs j WHERE j.brand_id=$brand AND j.job_kind IN (
                    'submit_supplier','reconcile_supplier','export_supplier')
                    AND j.status='leased' AND j.lease_until<=$now
                ORDER BY j.lease_until,j.id LIMIT 1
                """, ("$brand", context.BrandId), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)));
            Guid job, request;
            long number;
            string kind;
            await using (var row = await find.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token)) return false;
                job = row.GetGuid(0);
                request = row.GetGuid(1);
                number = row.GetInt64(2);
                kind = row.GetString(3);
            }
            State state = await ReadStateAsync(tx, context, request, token);
            Guid attempt = AttemptId(job, number);
            bool started;
            using (var check = SqliteWallet.Command(tx, """
                SELECT 1 FROM supplier_request_attempts
                WHERE id=$attempt AND supplier_request_id=$request
                """, ("$attempt", attempt), ("$request", request)))
                started = await check.ExecuteScalarAsync(token) is not null;
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            using (var fix = SqliteWallet.Command(tx, """
                UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,
                    available_at=$now,completed_at=$completed,last_error_code=$error
                WHERE id=$job AND status='leased' AND attempts=$attempts AND lease_until<=$now
                """, ("$status", started ? "failed" : "retry"), ("$now", now),
                ("$completed", started ? now : null),
                ("$error", started ? "remote_outcome_unknown" : null),
                ("$job", job), ("$attempts", number)))
                if (await fix.ExecuteNonQueryAsync(token) != 1)
                    throw new UnauthorizedAccessException("The supplier lease changed.");
            if (started)
            {
                using (var update = SqliteWallet.Command(tx, """
                    UPDATE supplier_request_attempts SET outcome='unknown',error_code='provider_unknown',
                        finished_at=$now WHERE id=$attempt AND outcome='in_flight'
                    """, ("$now", now), ("$attempt", attempt)))
                    await update.ExecuteNonQueryAsync(token);
                await ReviewAsync(tx, state, "provider_unknown", "unknown", token);
                if (kind == "submit_supplier")
                    await OutboxStore.EnqueueSqliteAsync(tx,
                        JobIntent.Supplier(JobKind.ReconcileSupplier, context.BrandId, request),
                        attempt, DateTime.UtcNow, token);
            }
            return true;
        }, ct);

    private async Task<bool> CaptureAsync(SqliteTransaction tx, BrandContext context,
        State state, SupplierOutcome result, CancellationToken ct)
    {
        var inventory = new InventoryCipher(secrets);
        var brandCipher = new BrandCipher(secrets);
        bool acquired = false;
        foreach (SupplierItem item in result.Items.OrderBy(x => x.Reference, StringComparer.Ordinal))
        {
            byte[] key = SupplierRules.ItemKey(state.Binding, item.Reference);
            Guid? otherRequest = null;
            using (var previous = SqliteWallet.Command(tx, """
                SELECT supplier_request_id FROM fulfillments WHERE supplier_item_key=$key
                UNION ALL
                SELECT supplier_request_id FROM inventory_items WHERE supplier_item_key=$key
                """, ("$key", key)))
            await using (var row = await previous.ExecuteReaderAsync(ct))
                if (await row.ReadAsync(ct)) otherRequest = row.GetGuid(0);
            if (otherRequest is Guid id)
            {
                if (id != state.Binding.RequestId)
                    throw new UnauthorizedAccessException("Supplier item already belongs to another obligation.");
                continue;
            }
            if (item.Disposition == SupplierItemDisposition.Customer)
            {
                var occupied = new HashSet<int>();
                using (var slots = SqliteWallet.Command(tx, """
                    SELECT unit_no FROM fulfillments WHERE order_id=$order
                    """, ("$order", state.Binding.OrderId)))
                await using (var row = await slots.ExecuteReaderAsync(ct))
                    while (await row.ReadAsync(ct)) occupied.Add(row.GetInt32(0));
                int unit = Enumerable.Range(1, state.Quantity).FirstOrDefault(n => !occupied.Contains(n));
                if (unit == 0) throw new InvalidOperationException("Supplier item would exceed the accepted order.");
                Guid fulfillment = Guid.CreateVersion7();
                byte[] cipher = brandCipher.Encrypt(context, "fulfillments", fulfillment,
                    "delivered_content", item.Content.Span);
                using var insert = SqliteWallet.Command(tx, """
                    INSERT INTO fulfillments(id,order_id,unit_no,kind,status,supplier_request_id,
                        remote_item_ref,supplier_item_key,delivered_content,delivered_content_key_id,content_kind)
                    VALUES ($id,$order,$unit,'supplier','acquired',$request,$remote,$key,$content,$cipherKey,$kind)
                    """, ("$id", fulfillment), ("$order", state.Binding.OrderId), ("$unit", unit),
                    ("$request", state.Binding.RequestId), ("$remote", item.Reference), ("$key", key),
                    ("$content", cipher), ("$cipherKey", secrets.CurrentBrandKeyId(context)),
                    ("$kind", item.ContentKind));
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Supplier fulfillment was not persisted.");
            }
            else
            {
                Guid inventoryId = Guid.CreateVersion7();
                byte[] cipher = inventory.Encrypt(inventoryId, item.Content.Span);
                byte[] digest = secrets.UseInventoryKey(secrets.CurrentInventoryKeyId,
                    key => HMACSHA256.HashData(key, item.Content.Span));
                using var insert = SqliteWallet.Command(tx, """
                    INSERT INTO inventory_items(id,product_id,credential_ciphertext,credential_key_id,
                        source_fingerprint,origin_kind,supplier_request_id,remote_item_ref,supplier_item_key,state)
                    VALUES ($id,$product,$cipher,$cipherKey,$digest,'supplier_bonus',$request,$remote,$key,'admin_held')
                    """, ("$id", inventoryId), ("$product", state.Binding.ProductId),
                    ("$cipher", cipher), ("$cipherKey", secrets.CurrentInventoryKeyId),
                    ("$digest", digest), ("$request", state.Binding.RequestId),
                    ("$remote", item.Reference), ("$key", key));
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Supplier bonus was not persisted.");
            }
            acquired = true;
        }
        return acquired;
    }

    private static async Task ReviewAsync(SqliteTransaction tx, State state, string code, string status,
        CancellationToken ct)
    {
        using (var request = SqliteWallet.Command(tx, """
            UPDATE supplier_requests SET status=$status,review_code=$code,updated_at=$now
            WHERE id=$request AND status NOT IN ('succeeded','resolved')
            """, ("$status", status), ("$code", code),
            ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$request", state.Binding.RequestId)))
            if (await request.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Supplier review could not be recorded.");
        using var order = SqliteWallet.Command(tx, """
            UPDATE orders SET fulfillment_status='requires_admin',
                review_code=coalesce(review_code,$code),revision=revision+1,updated_at=$now
            WHERE id=$order AND payment_status IN ('paid','not_required')
            """, ("$code", code), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
            ("$order", state.Binding.OrderId));
        if (await order.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Supplier obligation is no longer payable.");
    }

    private static async Task<State> ReadStateAsync(SqliteTransaction tx, BrandContext context, Guid request,
        CancellationToken ct)
    {
        using var query = SqliteWallet.Command(tx, """
            SELECT r.id,r.order_id,o.product_id,r.product_supplier_source_id,r.source_revision,r.provider_code,
                r.account_ref,r.external_product_id,r.external_variant_id,r.local_intent_key,r.requested_quantity,
                r.cost_baseline_amount,r.cost_currency,r.approved_cost_ceiling,
                r.status,r.review_code,o.payment_status,o.fulfillment_status,o.hold_at IS NOT NULL,
                o.review_code,s.enabled,s.revision,o.quantity
            FROM supplier_requests r JOIN orders o ON o.id=r.order_id
                JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                LEFT JOIN product_supplier_sources s ON s.id=r.product_supplier_source_id
            WHERE r.id=$request AND b.id=$brand AND b.bot_id=$bot AND b.active=1
            """, ("$request", request), ("$brand", context.BrandId), ("$bot", context.BotId));
        await using var row = await query.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.IsDBNull(3) || row.IsDBNull(4) || row.IsDBNull(5) ||
            row.IsDBNull(6) || row.IsDBNull(7) || row.IsDBNull(11) || row.IsDBNull(12))
            throw new UnauthorizedAccessException("Supplier source binding is unavailable.");
        var binding = new SupplierBinding(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2),
            row.GetGuid(3), row.GetInt64(4), row.GetString(5), row.GetString(6), row.GetString(7),
            row.IsDBNull(8) ? null : row.GetString(8), row.GetString(9), row.GetInt32(10),
            ExactDecimal.Parse(row.GetString(11)), row.GetString(12),
            row.IsDBNull(13) ? null : ExactDecimal.Parse(row.GetString(13)));
        return new(binding,row.GetString(14),row.IsDBNull(15) ? null : row.GetString(15),
            row.GetString(16),row.GetString(17),row.GetBoolean(18),row.IsDBNull(19) ? null : row.GetString(19),
            !row.IsDBNull(20) && row.GetBoolean(20),row.IsDBNull(21) ? 0 : row.GetInt64(21),
            row.GetInt32(22));
    }

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
