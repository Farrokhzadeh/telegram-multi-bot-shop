using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SqliteStatusDispatch(Guid DeliveryId, Guid OrderId, long ChatId, long? TargetMessageId, string Text);

public sealed class SqliteStagingJobs(SqliteSessionFactory sessions)
{
    public Task<JobLease?> ClaimAsync(BrandContext context, JobKind kind, Guid worker, CancellationToken ct = default)
    {
        if (kind is not (JobKind.SendTelegram or JobKind.PrepareManifest))
            throw new UnauthorizedAccessException("The staging worker must not execute supplier or settlement jobs.");
        return sessions.RunAsync(true, (_, tx, token) =>
            OutboxStore.ClaimSqliteAsync(tx, context.BrandId, kind, worker, TimeSpan.FromSeconds(90), token), ct);
    }

    public Task<int> PendingAsync(BrandContext context, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            using var command = SqliteWallet.Command(tx, """
                SELECT count(*) FROM jobs WHERE brand_id=$brand AND status IN ('pending','retry')
                """, ("$brand", context.BrandId));
            return Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }, ct);
}

public sealed class SqliteStatusDeliveryStore(SqliteSessionFactory sessions)
{
    public Task<SqliteStatusDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid id = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, id, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(90), token);
            if (state.Status=="succeeded")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (SqliteStatusDispatch?)null;
            }
            if (state.Status is not ("pending" or "retry") ||
                (state.Action=="edit" && state.Target is null) ||
                state.Action is not ("send" or "edit") ||
                (state.Action=="send" && state.Target is not null))
                throw new UnauthorizedAccessException("Status dispatch is not authorized.");
            if (state.Action=="edit")
            {
                using var anchor=SqliteWallet.Command(tx,"""
                    SELECT 1 FROM telegram_deliveries
                    WHERE brand_id=$brand AND order_id=$order AND user_id=$member AND purpose='order_status'
                        AND telegram_message_id=$target AND status='succeeded' LIMIT 1
                    """,("$brand",context.BrandId),("$order",state.Order),("$member",state.Member),("$target",state.Target));
                if(await anchor.ExecuteScalarAsync(token) is null) throw new UnauthorizedAccessException("Status edit anchor is unavailable.");
            }
            string now=SqliteSchema.UtcText(DateTime.UtcNow);
            using var command=SqliteWallet.Command(tx,"""
                UPDATE telegram_deliveries SET status='leased',attempts=$attempt,last_attempt_at=$now
                WHERE id=$id AND status IN ('pending','retry')
                """,("$attempt",renewed.Attempt),("$now",now),("$id",id));
            if(await command.ExecuteNonQueryAsync(token)!=1) throw new UnauthorizedAccessException();
            string text="Akanti staging order\n"+state.Title+"\nPayment: "+state.Payment+
                "\nDelivery: "+state.Delivery+"\nOrder: "+state.Order.ToString("N")[..12];
            if(text.Length>4000) throw new UnauthorizedAccessException("Status message is too large.");
            return new SqliteStatusDispatch(id,state.Order,state.Telegram,state.Target,text);
        },ct);
    }

    public Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome,
        CancellationToken ct = default)
    {
        Guid id = Require(context, lease);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state=await ReadAsync(tx,context,id,token);
            if(state.Status=="succeeded") return 0;
            if(state.Status!="leased" || state.Attempts!=lease.Attempt)
                throw new UnauthorizedAccessException("Status delivery lease was superseded.");
            bool success=outcome.Kind==DeliveryOutcomeKind.Succeeded && outcome.MessageId is >0 &&
                (state.Target is null || state.Target==outcome.MessageId);
            bool retry=!success && state.Target is not null &&
                outcome.Kind is (DeliveryOutcomeKind.Retry or DeliveryOutcomeKind.Unknown);
            string now=SqliteSchema.UtcText(DateTime.UtcNow);
            string future=SqliteSchema.UtcText(DateTime.UtcNow.AddSeconds(5));
            string deliveryState=success?"succeeded":retry?"retry":
                outcome.Kind==DeliveryOutcomeKind.Unknown?"uncertain":"failed";
            string jobState=success?"succeeded":retry?"retry":"failed";
            using(var job=SqliteWallet.Command(tx,"""
                UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,
                    available_at=$available,completed_at=CASE WHEN $status='retry' THEN NULL ELSE $now END,
                    last_error_code=$code
                WHERE id=$job AND brand_id=$brand AND telegram_delivery_id=$delivery
                    AND status='leased' AND attempts=$attempt AND locked_by=$worker AND lease_until>$now
                """,("$status",jobState),("$available",future),("$now",now),
                ("$code",success?"known_success":retry?"idempotent_edit_retry":"remote_outcome_unknown"),
                ("$job",lease.Id),("$brand",context.BrandId),("$delivery",id),("$attempt",lease.Attempt),
                ("$worker",lease.Worker.ToString("D"))))
                if(await job.ExecuteNonQueryAsync(token)!=1) throw new UnauthorizedAccessException("Status job lease expired.");
            using(var update=SqliteWallet.Command(tx,"""
                UPDATE telegram_deliveries SET status=$status,telegram_message_id=$message,
                    sent_at=CASE WHEN $status='succeeded' THEN $now ELSE NULL END,
                    last_error_code=$code,next_attempt_at=$available
                WHERE id=$id AND status='leased' AND attempts=$attempt
                """,("$status",deliveryState),("$message",success?outcome.MessageId:null),("$now",now),
                ("$code",success?"known_success":retry?"idempotent_edit_retry":"remote_outcome_unknown"),
                ("$available",future),("$id",id),("$attempt",lease.Attempt)))
                if(await update.ExecuteNonQueryAsync(token)!=1) throw new UnauthorizedAccessException("Status dispatch state changed.");
            return 0;
        },ct);
    }

    public Task RecoverExpiredAsync(BrandContext context,CancellationToken ct=default)=>
        sessions.RunAsync(true,async(_,tx,token)=>
        {
            string now=SqliteSchema.UtcText(DateTime.UtcNow);
            using var query=SqliteWallet.Command(tx,"""
                SELECT d.id,d.target_message_id IS NOT NULL
                FROM telegram_deliveries d JOIN jobs j ON j.telegram_delivery_id=d.id
                WHERE d.brand_id=$brand AND d.purpose='order_status' AND d.status='leased'
                    AND j.status='leased' AND j.lease_until<=$now
                ORDER BY d.id LIMIT 200
                """,("$brand",context.BrandId),("$now",now));
            var expired=new List<(Guid Id,bool Edit)>();
            await using(var row=await query.ExecuteReaderAsync(token))
                while(await row.ReadAsync(token))expired.Add((row.GetGuid(0),row.GetBoolean(1)));
            foreach(var (id,edit) in expired)
            {
                using var fix=SqliteWallet.Command(tx,"""
                    UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,available_at=$now,
                        completed_at=CASE WHEN $status='failed' THEN $now ELSE NULL END,
                        last_error_code='remote_outcome_unknown'
                    WHERE telegram_delivery_id=$id AND status='leased' AND lease_until<=$now
                    """,("$status",edit?"retry":"failed"),("$id",id),("$now",now));
                if(await fix.ExecuteNonQueryAsync(token)==1)
                {
                    using var mark=SqliteWallet.Command(tx,"""
                        UPDATE telegram_deliveries SET status=$status,last_error_code='remote_outcome_unknown'
                        WHERE id=$id AND status='leased'
                        """,("$status",edit?"retry":"uncertain"),("$id",id));
                    if(await mark.ExecuteNonQueryAsync(token)!=1)throw new UnauthorizedAccessException();
                }
            }
            return expired.Count;
        },ct);

    private static Guid Require(BrandContext context, JobLease lease) =>
        lease.Intent.Kind==JobKind.SendTelegram && lease.Intent.BrandId==context.BrandId &&
        lease.Intent.OriginId is Guid id && id!=Guid.Empty ? id :
        throw new UnauthorizedAccessException("Telegram job scope is unavailable.");

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid id, CancellationToken ct)
    {
        using var query=SqliteWallet.Command(tx,"""
            SELECT d.order_id,d.user_id,u.telegram_id,d.target_message_id,d.action,d.status,d.attempts,
                o.product_title_snapshot,o.payment_status,o.fulfillment_status
            FROM telegram_deliveries d JOIN users u ON u.id=d.user_id
                JOIN orders o ON o.id=d.order_id JOIN brands b ON b.id=d.brand_id
            WHERE d.id=$id AND d.brand_id=$brand AND u.brand_id=$brand AND o.user_id=d.user_id
                AND d.purpose='order_status' AND d.recipient_kind='customer_dm'
                AND b.bot_id=$bot AND b.active=1 AND u.blocked_at IS NULL AND u.banned_at IS NULL
            """,("$id",id),("$brand",context.BrandId),("$bot",context.BotId));
        await using var row=await query.ExecuteReaderAsync(ct);
        if(!await row.ReadAsync(ct))throw new UnauthorizedAccessException("Telegram status delivery is unavailable.");
        return new(row.GetGuid(0),row.GetGuid(1),row.GetInt64(2),row.IsDBNull(3)?null:row.GetInt64(3),
            row.GetString(4),row.GetString(5),row.GetInt64(6),row.GetString(7),row.GetString(8),row.GetString(9));
    }

    private sealed record State(Guid Order,Guid Member,long Telegram,long? Target,string Action,string Status,
        long Attempts,string Title,string Payment,string Delivery);
}
