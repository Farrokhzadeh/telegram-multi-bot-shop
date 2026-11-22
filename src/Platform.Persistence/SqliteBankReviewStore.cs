using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public enum SqliteBankDecisionOutcome { ConfirmedPaid, DeniedUnverified, Replayed }

public sealed class SqliteBankReviewStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<SqliteBankDecisionOutcome> DecideAsync(BrandContext context, AuthenticatedAdmin actor, Guid payment,
        bool approve, long expectedOrderRevision, Guid command, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        if (payment == Guid.Empty || command == Guid.Empty || expectedOrderRevision < 0 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || reason.Any(char.IsControl))
            throw new ArgumentException("Invalid protected bank decision.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"receipt-decision-v1:{payment:D}:{approve}:{expectedOrderRevision}:{reason}"));
        var audit = new AuditCommand(command, AuditAction.Update, AuditEntity.Payment, payment,
            context.BrandId, AuditReason.OperatorReview, command, hash);
        byte[] commitment = audit.Fingerprint(actor.AdminId);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, token);
            using (var previous = SqliteWallet.Command(tx,
                "SELECT command_sha256 FROM audit_events WHERE id=$command", ("$command", command)))
            {
                object? value = await previous.ExecuteScalarAsync(token);
                if (value is byte[] prior)
                {
                    if (!CryptographicOperations.FixedTimeEquals(prior, commitment))
                        throw new UnauthorizedAccessException("Administrator command identity conflict.");
                    return SqliteBankDecisionOutcome.Replayed;
                }
            }
            State state = await ReadAsync(tx, context, payment, token);
            if (state.Method != "bank" || state.Status != "observed" ||
                state.OrderStatus != "under_review" || state.Revision != expectedOrderRevision ||
                state.Receipt is null || state.First is null || state.Due is null ||
                string.CompareOrdinal(state.First, state.Due) > 0 ||
                state.Held || state.Review || state.Kind is not ("local" or "manual") || state.Preorder)
                throw new InvalidOperationException("Bank receipt is not eligible for approval.");
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            await Change(tx, """
                INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,
                    reason,correlation_id,command_sha256)
                VALUES ($command,'admin',$admin,$brand,'update','payment',$payment,'operator_review',$command,$hash)
                """, token, ("$command", command), ("$admin", actor.AdminId),
                ("$brand", context.BrandId), ("$payment", payment), ("$hash", commitment));
            await Change(tx, """
                UPDATE payments SET status=$status,review_admin_id=$admin,reviewed_at=$now,
                    review_reason=$reason,amount_received=CASE WHEN $approve THEN amount_due ELSE amount_received END,
                    confirmed_at=CASE WHEN $approve THEN $now ELSE confirmed_at END
                WHERE id=$payment AND status='observed' AND receipt_asset_ref IS NOT NULL
                """, token, ("$status", approve ? "settled" : "rejected"), ("$admin", actor.AdminId),
                ("$now", now), ("$reason", reason), ("$approve", approve), ("$payment", payment));
            if (!approve) return SqliteBankDecisionOutcome.DeniedUnverified;
            await Change(tx, """
                UPDATE orders SET payment_status='paid',payment_confirmed_at=$now,revision=revision+1,
                    updated_at=$now WHERE id=$order AND payment_status='under_review' AND revision=$revision
                """, token, ("$now", now), ("$order", state.Order), ("$revision", expectedOrderRevision));
            if (state.Kind == "local")
            {
                bool queued;
                using (var backlog = SqliteWallet.Command(tx, """
                    SELECT EXISTS(SELECT 1 FROM orders WHERE product_id=$product AND id<>$order
                        AND is_preorder=1 AND payment_status IN ('paid','not_required')
                        AND fulfillment_status IN ('waiting_stock','requires_admin'))
                    """, ("$product", state.Product), ("$order", state.Order)))
                    queued = (long)(await backlog.ExecuteScalarAsync(token))! != 0;
                List<SqliteOrderPaymentSettlementStore.Stock> stock = queued ? [] :
                    await SqliteOrderPaymentSettlementStore.ReadStockAsync(
                    tx, state.Product, state.Quantity, token);
                if (!queued && stock.Count == state.Quantity)
                {
                    await new SqliteOrderPaymentSettlementStore(sessions, secrets).AcquireAsync(
                        tx, context, state.Order, stock, now, token);
                    await FulfillmentAsync(tx, state.Order, "acquiring", null, now, token);
                }
                else await FulfillmentAsync(tx, state.Order, "requires_admin", "stock_shortage", now, token);
            }
            else await FulfillmentAsync(tx, state.Order, "requires_admin", null, now, token);
            using (var expire = SqliteWallet.Command(tx, """
                UPDATE jobs SET status='cancelled',completed_at=$now
                WHERE order_id=$order AND job_kind='expire_order_payment' AND status IN ('pending','retry')
                """, ("$now", now), ("$order", state.Order)))
                await expire.ExecuteNonQueryAsync(token);
            await StatusAsync(tx, context, state, token);
            return SqliteBankDecisionOutcome.ConfirmedPaid;
        }, ct);
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid payment,
        CancellationToken ct)
    {
        using var command = SqliteWallet.Command(tx, """
            SELECT p.payment_method,p.status,p.receipt_asset_ref,p.first_payment_observed_at,
                o.payment_due_at,o.payment_status,o.revision,o.product_id,o.quantity,o.fulfillment_kind_snapshot,
                o.is_preorder,o.user_id,o.id,o.review_code IS NOT NULL,o.hold_at IS NOT NULL
            FROM payments p JOIN orders o ON o.id=p.order_id JOIN users u ON u.id=o.user_id
                JOIN brands b ON b.id=u.brand_id
            WHERE p.id=$payment AND b.id=$brand AND b.bot_id=$bot AND b.active=1
            """, ("$payment", payment), ("$brand", context.BrandId), ("$bot", context.BotId));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Bank payment is unavailable.");
        return new(row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4),
            row.GetString(5), row.GetInt64(6), row.GetGuid(7), row.GetInt32(8), row.GetString(9),
            row.GetBoolean(10), row.GetGuid(11), row.GetGuid(12), row.GetBoolean(13), row.GetBoolean(14));
    }

    private static Task FulfillmentAsync(SqliteTransaction tx, Guid order, string status, string? review,
        string now, CancellationToken ct) => Change(tx, """
            UPDATE orders SET fulfillment_status=$status,review_code=$review,
                revision=revision+1,updated_at=$now WHERE id=$order
            """, ct, ("$status", status), ("$review", review), ("$now", now), ("$order", order));

    private static async Task StatusAsync(SqliteTransaction tx, BrandContext context, State state, CancellationToken ct)
    {
        long? anchor;
        using (var lookup = SqliteWallet.Command(tx, """
            SELECT telegram_message_id FROM telegram_deliveries WHERE order_id=$order
                AND purpose='order_status' AND status='succeeded' AND telegram_message_id IS NOT NULL
            ORDER BY created_at,id LIMIT 1
            """, ("$order", state.Order)))
            anchor = await lookup.ExecuteScalarAsync(ct) as long?;
        Guid delivery = Guid.CreateVersion7();
        await Change(tx, """
            INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,
                target_message_id,dedupe_key,order_id)
            VALUES ($id,$brand,'customer_dm',$member,'order_status',
                CASE WHEN $anchor IS NULL THEN 'send' ELSE 'edit' END,$anchor,$dedupe,$order)
            """, ct, ("$id", delivery), ("$brand", context.BrandId), ("$member", state.Member),
            ("$anchor", anchor), ("$dedupe", "receipt-status:" + state.Order.ToString("D")),
            ("$order", state.Order));
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, delivery),
            delivery, DateTime.UtcNow, ct);
    }

    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] values)
    {
        using var command = SqliteWallet.Command(tx, sql, values);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Bank decision lost its authorized target.");
    }

    private sealed record State(string Method, string Status, string? Receipt, string? First,
        string? Due, string OrderStatus, long Revision, Guid Product, int Quantity, string Kind,
        bool Preorder, Guid Member, Guid Order, bool Review, bool Held);
}
