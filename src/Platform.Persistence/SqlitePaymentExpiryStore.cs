using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public enum SqlitePaymentExpiryOutcome { Expired, Protected, NotDue, Terminal }

public sealed class SqlitePaymentExpiryStore(SqliteSessionFactory sessions)
{
    public Task<JobLease?> ClaimAsync(BrandContext context, Guid worker, CancellationToken ct = default) =>
        sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx, context.BrandId,
            JobKind.ExpireOrderPayment, worker, TimeSpan.FromSeconds(60), token), ct);

    public Task<SqlitePaymentExpiryOutcome> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.ExpireOrderPayment || lease.Intent.BrandId != context.BrandId ||
            lease.Intent.OriginId is not Guid order || order == Guid.Empty)
            throw new UnauthorizedAccessException("Payment expiry lease is unavailable.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(60), token);
            using var query = SqliteWallet.Command(tx, """
                SELECT p.id,p.status,p.first_payment_observed_at,p.receipt_asset_ref,
                    o.payment_status,o.payment_due_at,o.user_id
                FROM orders o JOIN payments p ON p.order_id=o.id
                    JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                WHERE o.id=$order AND b.id=$brand AND b.bot_id=$bot AND b.active=1
                """, ("$order", order), ("$brand", context.BrandId), ("$bot", context.BotId));
            await using var row = await query.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new UnauthorizedAccessException("Payment expiry target is unavailable.");
            string status = row.GetString(1), orderStatus = row.GetString(4);
            bool evidence = !row.IsDBNull(2) || !row.IsDBNull(3) ||
                status is not ("created" or "awaiting");
            string due = row.IsDBNull(5) ? "" : row.GetString(5);
            Guid member = row.GetGuid(6);
            await row.CloseAsync();
            if (orderStatus is not ("awaiting_payment" or "under_review" or "confirmation_pending"))
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return SqlitePaymentExpiryOutcome.Terminal;
            }
            if (orderStatus != "awaiting_payment" || evidence)
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return SqlitePaymentExpiryOutcome.Protected;
            }
            DateTime now = DateTime.UtcNow;
            DateTime deadline = DateTime.ParseExact(due, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            if (now <= deadline)
            {
                int delay = Math.Clamp(checked((int)Math.Ceiling((deadline - now).TotalSeconds)) + 1, 1, 300);
                await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(delay),
                    int.MaxValue, JobError.Busy, token);
                return SqlitePaymentExpiryOutcome.NotDue;
            }
            using (var held = SqliteWallet.Command(tx, """
                SELECT 1 FROM discount_uses WHERE order_id=$order AND status='held' LIMIT 1
                """, ("$order", order)))
                if (await held.ExecuteScalarAsync(token) is not null)
                    throw new InvalidOperationException("A discount hold requires qualified release before expiry.");
            string expired = SqliteSchema.UtcText(now);
            using (var change = SqliteWallet.Command(tx, """
                UPDATE orders SET payment_status='expired',fulfillment_status='cancelled',
                    resolution_kind='unpaid_expiry',resolution_at=$now,revision=revision+1,updated_at=$now
                WHERE id=$order AND payment_status='awaiting_payment'
                """, ("$now", expired), ("$order", order)))
                if (await change.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("Expiry state was superseded.");
            Guid delivery = Guid.CreateVersion7();
            using (var intent = SqliteWallet.Command(tx, """
                INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,
                    target_message_id,dedupe_key,order_id)
                SELECT $id,$brand,'customer_dm',$member,'order_status',
                    CASE WHEN a.message IS NULL THEN 'send' ELSE 'edit' END,
                    a.message,$dedupe,$order
                FROM (SELECT 1) seed LEFT JOIN (
                    SELECT telegram_message_id message FROM telegram_deliveries
                    WHERE order_id=$order AND purpose='order_status' AND status='succeeded'
                        AND telegram_message_id IS NOT NULL
                    ORDER BY created_at,id LIMIT 1
                ) a ON true WHERE true
                ON CONFLICT(dedupe_key) DO NOTHING
                """, ("$id", delivery), ("$brand", context.BrandId), ("$member", member),
                ("$dedupe", "instruction-expired:" + order.ToString("D")), ("$order", order)))
                await intent.ExecuteNonQueryAsync(token);
            using (var lookup = SqliteWallet.Command(tx, """
                SELECT id FROM telegram_deliveries WHERE brand_id=$brand AND order_id=$order
                    AND dedupe_key=$dedupe
                """, ("$brand", context.BrandId), ("$order", order),
                ("$dedupe", "instruction-expired:" + order.ToString("D"))))
            {
                if (await lookup.ExecuteScalarAsync(token) is not string id)
                    throw new InvalidOperationException("Expiry notification was not persisted.");
                Guid actual = Guid.Parse(id);
                await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, actual),
                    actual, now, token);
            }
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return SqlitePaymentExpiryOutcome.Expired;
        }, ct);
    }
}
