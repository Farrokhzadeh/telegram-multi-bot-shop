using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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
            using var query = SqliteWallet.Command(tx, """
                SELECT r.order_id,r.cost_baseline_amount,r.cost_currency,r.status,r.review_code,r.revision,
                    o.payment_status,o.fulfillment_status,o.hold_at IS NOT NULL,
                    EXISTS(SELECT 1 FROM supplier_request_attempts WHERE supplier_request_id=r.id AND operation='submit')
                FROM supplier_requests r JOIN orders o ON o.id=r.order_id
                JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                WHERE r.id=$request AND b.id=$brand AND b.bot_id=$bot AND b.active=1
                """, ("$request", request), ("$brand", context.BrandId), ("$bot", context.BotId));
            Guid order;
            bool prior;
            await using (var row = await query.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || row.IsDBNull(1) || row.IsDBNull(2) ||
                    row.GetString(2) != currency || ExactDecimal.Parse(row.GetString(1)) >= maximumUnitCost ||
                    row.GetString(3) != "blocked" || row.GetString(4) != "cost_limit" ||
                    row.GetInt64(5) != expectedRevision || row.GetString(6) is not ("paid" or "not_required") ||
                    row.GetString(7) is "completed" or "compensated" or "cancelled" || row.GetBoolean(8))
                    throw new InvalidOperationException("Original supplier cost review is unavailable.");
                order = row.GetGuid(0);
                prior = row.GetBoolean(9);
            }
            using (var evidence = SqliteWallet.Command(tx, """
                SELECT EXISTS(SELECT 1 FROM jobs WHERE supplier_request_id=$request AND status='leased')
                    OR EXISTS(SELECT 1 FROM fulfillments
                        WHERE supplier_request_id=$request AND status<>'unfilled')
                    OR EXISTS(SELECT 1 FROM inventory_items WHERE supplier_request_id=$request)
                    OR EXISTS(SELECT 1 FROM supplier_request_attempts a
                        WHERE a.supplier_request_id=$request AND a.operation='submit' AND
                            (a.outcome IN ('in_flight','unknown','timeout','succeeded')
                            OR a.outcome='failed' AND a.error_code<>'confirmed_no_effect'))
                """, ("$request", request)))
                if ((long)(await evidence.ExecuteScalarAsync(token))! != 0)
                    throw new InvalidOperationException("Supplier cost outcome requires reconciliation.");
            await WriteAuditAsync(tx, actor, audit, token);
            using (var update = SqliteWallet.Command(tx, """
                UPDATE supplier_requests SET status=$status,review_code=NULL,
                    approved_cost_ceiling=$ceiling,approval_audit_id=$audit,
                    revision=revision+1,updated_at=$now
                WHERE id=$request AND status='blocked' AND review_code='cost_limit'
                    AND revision=$revision
                """, ("$status", prior ? "failed" : "pending"), ("$ceiling", ExactDecimal.Format(maximumUnitCost)),
                ("$audit", command), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
                ("$request", request), ("$revision", expectedRevision)))
                if (await update.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Supplier cost approval was superseded.");
            if (!prior)
            {
                using var orderUpdate = SqliteWallet.Command(tx, """
                    UPDATE orders SET review_code=NULL,fulfillment_status='acquiring',
                        revision=revision+1,updated_at=$now
                    WHERE id=$order AND review_code='cost_limit'
                    """, ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$order", order));
                if (await orderUpdate.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Supplier cost hold was superseded.");
                await OutboxStore.EnqueueSqliteAsync(tx,
                    JobIntent.Supplier(JobKind.SubmitSupplier, context.BrandId, request), command,
                    DateTime.UtcNow, token);
            }
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
            using var query = SqliteWallet.Command(tx, """
                SELECT o.quantity,r.requested_quantity
                FROM supplier_requests r JOIN orders o ON o.id=r.order_id
                    JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                WHERE r.id=$request AND o.id=$order AND b.id=$brand AND b.bot_id=$bot
                    AND b.active=1 AND o.revision=$revision AND o.payment_status IN ('paid','not_required')
                    AND o.fulfillment_status='requires_admin' AND o.review_code='partial_acquisition'
                    AND o.release_authorized_at IS NULL AND o.delivery_generation IS NULL
                    AND r.status='succeeded' AND r.review_code IS NULL
                    AND r.remote_ref IS NOT NULL AND r.evidence_ref IS NOT NULL
                    AND (SELECT count(*) FROM fulfillments f WHERE f.order_id=$order)=o.quantity
                    AND (SELECT count(*) FROM fulfillments f
                        WHERE f.supplier_request_id=$request AND f.kind='supplier'
                            AND f.status='acquired')=r.requested_quantity
                    AND EXISTS(SELECT 1 FROM supplier_request_attempts a
                        WHERE a.supplier_request_id=$request AND a.outcome='succeeded'
                        AND a.error_code IS NULL AND a.evidence_ref=r.evidence_ref
                        AND a.remote_ref IS NOT NULL AND a.finished_at IS NOT NULL)
                """, ("$request", request), ("$order", order), ("$brand", context.BrandId),
                ("$bot", context.BotId), ("$revision", revision));
            int quantity;
            await using (var row = await query.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token))
                    throw new InvalidOperationException("Complete verified supplier acquisition requires current review.");
                quantity = row.GetInt32(0);
            }
            var slots = new HashSet<int>();
            using (var contents = SqliteWallet.Command(tx, """
                SELECT id,unit_no,delivered_content,delivered_content_key_id,content_kind
                FROM fulfillments WHERE order_id=$order
                """, ("$order", order)))
            await using (var row = await contents.ExecuteReaderAsync(token))
                while (await row.ReadAsync(token))
                {
                    if (!slots.Add(row.GetInt32(1)) || row.IsDBNull(2) || row.IsDBNull(3) || row.IsDBNull(4))
                        throw new InvalidOperationException("Incomplete verified supplier manifest.");
                    byte[] plain = new BrandCipher(secrets).Decrypt(context, "fulfillments", row.GetGuid(0),
                        "delivered_content", row.GetString(3), row.GetFieldValue<byte[]>(2));
                    try { if (plain.Length == 0) throw new InvalidOperationException("Empty supplier credential."); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
            if (slots.Count != quantity || Enumerable.Range(1, quantity).Any(n => !slots.Contains(n)))
                throw new InvalidOperationException("Incomplete supplier unit allocation.");
            await WriteAuditAsync(tx, actor, audit, token);
            using var update = SqliteWallet.Command(tx, """
                UPDATE orders SET fulfillment_status='acquiring',review_code=NULL,
                    revision=revision+1,updated_at=$now
                WHERE id=$order AND revision=$revision AND review_code='partial_acquisition'
                """, ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$order", order),
                ("$revision", revision));
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Supplier completion review was superseded.");
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
            using var query = SqliteWallet.Command(tx, """
                SELECT i.remote_item_ref,i.credential_ciphertext,i.credential_key_id
                FROM inventory_items i JOIN supplier_requests r ON r.id=i.supplier_request_id
                    JOIN orders o ON o.id=r.order_id JOIN users u ON u.id=o.user_id
                    JOIN brands b ON b.id=u.brand_id
                WHERE i.id=$inventory AND r.id=$request AND i.state='admin_held'
                    AND i.origin_kind='supplier_bonus' AND b.id=$brand AND b.bot_id=$bot
                """, ("$inventory", inventory), ("$request", request),
                ("$brand", context.BrandId), ("$bot", context.BotId));
            string reference, key;
            byte[] encrypted;
            await using (var row = await query.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token))
                    throw new UnauthorizedAccessException("Protected supplier bonus is unavailable.");
                reference = row.GetString(0);
                encrypted = row.GetFieldValue<byte[]>(1);
                key = row.GetString(2);
            }
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
