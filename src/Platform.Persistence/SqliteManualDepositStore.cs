using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public enum SqliteReceiptOutcome { Submitted, LateReview, Replayed }

public sealed class SqliteManualDepositStore(SqliteSessionFactory sessions)
{
    public Task<SqliteReceiptOutcome> SubmitAsync(BrandContext context, long revision, long actor,
        Guid payment, string protectedReceipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (revision < 0 || actor <= 0 || payment == Guid.Empty || protectedReceipt is null ||
            !protectedReceipt.StartsWith("protected:", StringComparison.Ordinal) ||
            protectedReceipt.Length is < 12 or > 1024 || protectedReceipt.Any(char.IsControl))
            throw new ArgumentException("The protected bank receipt is unavailable.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var query = SqliteWallet.Command(tx, """
                SELECT p.receipt_asset_ref,p.status,p.payment_method,o.payment_status,o.payment_due_at,
                    u.telegram_id
                FROM payments p JOIN orders o ON o.id=p.order_id
                    JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
                WHERE p.id=$payment AND b.id=$brand AND b.bot_id=$bot AND b.active=1
                    AND b.policy_version=$revision AND u.telegram_id=$actor
                    AND u.blocked_at IS NULL AND u.banned_at IS NULL
                """, ("$payment", payment), ("$brand", context.BrandId), ("$bot", context.BotId),
                ("$revision", revision), ("$actor", actor));
            await using var row = await query.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetString(2) != "bank" || row.IsDBNull(4))
                throw new UnauthorizedAccessException("The receipt is unavailable.");
            if (!row.IsDBNull(0))
            {
                if (row.GetString(0) != protectedReceipt)
                    throw new InvalidOperationException("The receipt identity was already used.");
                return SqliteReceiptOutcome.Replayed;
            }
            if (row.GetString(1) == "settled" || row.GetString(3) is "paid" or "not_required")
                throw new InvalidOperationException("The receipt was already accepted.");
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            bool timely = row.GetString(3) == "awaiting_payment" &&
                string.CompareOrdinal(now, row.GetString(4)) <= 0;
            await row.CloseAsync();
            using (var paymentUpdate = SqliteWallet.Command(tx, """
                UPDATE payments SET receipt_asset_ref=$receipt,first_payment_observed_at=coalesce(first_payment_observed_at,$now),
                    status=$status WHERE id=$payment AND receipt_asset_ref IS NULL AND status<>'settled'
                """, ("$receipt", protectedReceipt), ("$now", now),
                ("$status", timely ? "observed" : "review"), ("$payment", payment)))
                if (await paymentUpdate.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("The receipt origin changed.");
            if (timely)
            {
                using var orderUpdate = SqliteWallet.Command(tx, """
                    UPDATE orders SET payment_status='under_review',revision=revision+1,updated_at=$now
                    WHERE id=(SELECT order_id FROM payments WHERE id=$payment) AND payment_status='awaiting_payment'
                    """, ("$now", now), ("$payment", payment));
                if (await orderUpdate.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidOperationException("The original order changed.");
            }
            return timely ? SqliteReceiptOutcome.Submitted : SqliteReceiptOutcome.LateReview;
        }, ct);
    }
}
