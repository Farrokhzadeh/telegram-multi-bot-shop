using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteCouponDispatch(long ChatId, string Text);

public sealed class SqliteCouponDeliveryStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    public Task<JobLease?> ClaimNoticeAsync(BrandContext context, Guid worker, CancellationToken ct = default) =>
        sessions.RunAsync(true, (_, tx, token) => OutboxStore.ClaimSqliteAsync(tx,
            context.BrandId, JobKind.CouponNotice, worker, TimeSpan.FromSeconds(90), token), ct);

    public Task PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.CouponNotice || lease.Intent.BrandId != context.BrandId ||
            lease.Intent.OriginId is not Guid coupon || coupon == Guid.Empty)
            throw new UnauthorizedAccessException();
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var scoped = SqliteWallet.Command(tx, """
                SELECT d.eligible_user_id FROM discounts d JOIN orders o ON o.id=d.source_order_id
                    JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=d.brand_id
                WHERE d.id=$coupon AND d.brand_id=$brand AND b.bot_id=$bot AND b.active=1
                    AND d.kind='prepaid_entitlement' AND d.eligible_user_id=u.id
                    AND u.brand_id=d.brand_id AND o.order_kind='prepaid_units'
                    AND o.payment_status='paid' AND o.fulfillment_status='completed'
                    AND u.blocked_at IS NULL AND u.banned_at IS NULL
                """, ("$coupon", coupon), ("$brand", context.BrandId), ("$bot", context.BotId));
            if (await scoped.ExecuteScalarAsync(token) is not string id)
                throw new UnauthorizedAccessException("The original paid prepaid entitlement is unavailable.");
            Guid member = Guid.Parse(id);
            string dedupe = "prepaid-notice:" + coupon.ToString("N");
            using (var insert = SqliteWallet.Command(tx, """
                INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,
                    dedupe_key,discount_id)
                VALUES ($id,$brand,'customer_dm',$member,'discount_notice','send',$dedupe,$coupon)
                ON CONFLICT(dedupe_key) DO NOTHING
                """, ("$id", Guid.CreateVersion7()), ("$brand", context.BrandId),
                ("$member", member), ("$dedupe", dedupe), ("$coupon", coupon)))
                await insert.ExecuteNonQueryAsync(token);
            using var lookup = SqliteWallet.Command(tx, """
                SELECT id FROM telegram_deliveries
                WHERE dedupe_key=$dedupe AND brand_id=$brand AND discount_id=$coupon
                    AND user_id=$member AND purpose='discount_notice' AND action='send'
                """, ("$dedupe", dedupe), ("$brand", context.BrandId),
                ("$coupon", coupon), ("$member", member));
            if (await lookup.ExecuteScalarAsync(token) is not string delivery)
                throw new UnauthorizedAccessException("Coupon notice identity conflict.");
            Guid target = Guid.Parse(delivery);
            await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, target),
                target, DateTime.UtcNow, token);
            await OutboxStore.CompleteSqliteAsync(tx, lease, token);
            return 0;
        }, ct);
    }

    public Task<SqliteCouponDispatch?> BeginAsync(BrandContext context, JobLease lease,
        CancellationToken ct = default)
    {
        Guid delivery = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            using var lookup = SqliteWallet.Command(tx, """
                SELECT d.status,d.attempts,u.telegram_id,d.discount_id,c.code_ciphertext,
                    c.code_key_id,c.source_order_id,c.eligible_user_id
                FROM telegram_deliveries d JOIN users u ON u.id=d.user_id
                    JOIN discounts c ON c.id=d.discount_id
                    JOIN orders o ON o.id=c.source_order_id
                    JOIN brands b ON b.id=d.brand_id
                WHERE d.id=$delivery AND d.brand_id=$brand AND b.bot_id=$bot AND b.active=1
                    AND u.brand_id=$brand AND u.id=c.eligible_user_id AND o.user_id=u.id
                    AND o.order_kind='prepaid_units' AND o.payment_status='paid'
                    AND o.fulfillment_status='completed' AND c.kind='prepaid_entitlement'
                    AND c.brand_id=$brand AND d.recipient_kind='customer_dm'
                    AND d.purpose='discount_notice' AND d.action='send'
                    AND d.target_message_id IS NULL AND u.blocked_at IS NULL AND u.banned_at IS NULL
                """, ("$delivery", delivery), ("$brand", context.BrandId), ("$bot", context.BotId));
            string status, key;
            long actor;
            Guid coupon;
            byte[] ciphertext;
            await using (var row = await lookup.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token)) throw new UnauthorizedAccessException("Prepaid delivery is unavailable.");
                status = row.GetString(0);
                actor = row.GetInt64(2);
                coupon = row.GetGuid(3);
                ciphertext = row.GetFieldValue<byte[]>(4);
                key = row.GetString(5);
            }
            JobLease renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            if (status == "succeeded")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (SqliteCouponDispatch?)null;
            }
            if (status is not ("pending" or "retry"))
                throw new UnauthorizedAccessException("Prepaid delivery is held for review.");
            byte[] plain = new BrandCipher(secrets).Decrypt(context, "discounts", coupon,
                "code_ciphertext", key, ciphertext);
            string code;
            try { code = new UTF8Encoding(false, true).GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (code.Length != 48 || !code.All(char.IsAsciiHexDigit))
                throw new UnauthorizedAccessException("Prepaid entitlement code is invalid.");
            using var attempt = SqliteWallet.Command(tx, """
                UPDATE telegram_deliveries SET status='leased',attempts=$attempt,last_attempt_at=$now
                WHERE id=$delivery AND status IN ('pending','retry')
                """, ("$attempt", renewed.Attempt), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
                ("$delivery", delivery));
            if (await attempt.ExecuteNonQueryAsync(token) != 1) throw new UnauthorizedAccessException();
            return new SqliteCouponDispatch(actor, "Akanti staging prepaid entitlement\nCode: " + code);
        }, ct);
    }

    public Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome result,
        CancellationToken ct = default)
    {
        Guid delivery = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            bool success = result.Kind == DeliveryOutcomeKind.Succeeded && result.MessageId is > 0;
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            using (var job = SqliteWallet.Command(tx, """
                UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,completed_at=$now,
                    last_error_code=$error
                WHERE id=$job AND brand_id=$brand AND telegram_delivery_id=$delivery
                    AND status='leased' AND attempts=$attempt AND locked_by=$worker AND lease_until>$now
                """, ("$status", success ? "succeeded" : "failed"), ("$now", now),
                ("$error", success ? "known_success" : "remote_outcome_unknown"),
                ("$job", lease.Id), ("$brand", context.BrandId), ("$delivery", delivery),
                ("$attempt", lease.Attempt), ("$worker", lease.Worker.ToString("D"))))
                if (await job.ExecuteNonQueryAsync(token) != 1)
                    throw new UnauthorizedAccessException("Prepaid delivery lease expired.");
            using var update = SqliteWallet.Command(tx, """
                UPDATE telegram_deliveries SET status=$status,telegram_message_id=$message,
                    sent_at=CASE WHEN $status='succeeded' THEN $now ELSE NULL END,
                    last_error_code=$error
                WHERE id=$delivery AND brand_id=$brand AND purpose='discount_notice'
                    AND status='leased' AND attempts=$attempt
                """, ("$status", success ? "succeeded" : "uncertain"),
                ("$message", success ? result.MessageId : null), ("$now", now),
                ("$error", success ? "known_success" : "remote_outcome_unknown"),
                ("$delivery", delivery), ("$brand", context.BrandId), ("$attempt", lease.Attempt));
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new UnauthorizedAccessException("Prepaid delivery state was superseded.");
            return 0;
        }, ct);
    }

    public Task<int> RecoverExpiredAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(true, async (_, tx, token) =>
        {
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            using var query = SqliteWallet.Command(tx, """
                SELECT d.id FROM telegram_deliveries d JOIN jobs j ON j.telegram_delivery_id=d.id
                WHERE d.brand_id=$brand AND d.purpose='discount_notice' AND d.status='leased'
                    AND j.status='leased' AND j.lease_until<=$now LIMIT 50
                """, ("$brand", context.BrandId), ("$now", now));
            var ids = new List<Guid>();
            await using (var rows = await query.ExecuteReaderAsync(token))
                while (await rows.ReadAsync(token)) ids.Add(rows.GetGuid(0));
            foreach (Guid delivery in ids)
            {
                using var stop = SqliteWallet.Command(tx, """
                    UPDATE jobs SET status='failed',locked_by=NULL,lease_until=NULL,completed_at=$now,
                        last_error_code='remote_outcome_unknown'
                    WHERE telegram_delivery_id=$delivery AND status='leased' AND lease_until<=$now
                    """, ("$delivery", delivery), ("$now", now));
                if (await stop.ExecuteNonQueryAsync(token) != 1) continue;
                using var hold = SqliteWallet.Command(tx, """
                    UPDATE telegram_deliveries SET status='uncertain',last_error_code='remote_outcome_unknown'
                    WHERE id=$delivery AND status='leased' AND purpose='discount_notice'
                    """, ("$delivery", delivery));
                if (await hold.ExecuteNonQueryAsync(token) != 1)
                    throw new UnauthorizedAccessException("Prepaid delivery recovery was superseded.");
            }
            return ids.Count;
        }, ct);

    private static Guid Require(BrandContext context, JobLease lease) =>
        lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId &&
        lease.Intent.OriginId is Guid id && id != Guid.Empty ? id :
        throw new UnauthorizedAccessException("Prepaid Telegram job scope is unavailable.");
}
