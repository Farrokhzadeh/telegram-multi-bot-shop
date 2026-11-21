using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Platform.Security;

namespace Platform.Persistence;

internal sealed class SqliteDeliveryStore(SqliteSessionFactory sessions, SecretSnapshot secrets)
{
    internal Task<Guid?> PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid order = Require(lease, JobKind.PrepareManifest, context);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, order, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            if (state.Kind!="product_purchase" || state.Payment is not ("paid" or "not_required") || state.Held || state.Review)
                throw new UnauthorizedAccessException("Order is unavailable for manifest preparation.");
            if (state.Generation is null)
            {
                using var manifest = await BuildAsync(tx, context, order, state.Quantity, false, token);
                using var supplier = SqliteWallet.Command(tx, """
                    SELECT 1 FROM supplier_requests WHERE order_id=$order AND status NOT IN ('succeeded','resolved') LIMIT 1
                    """, ("$order", order));
                bool unresolved = await supplier.ExecuteScalarAsync(token) is not null;
                using var coupon = SqliteWallet.Command(tx, "SELECT 1 FROM discount_uses WHERE order_id=$order AND status='held' LIMIT 1",
                    ("$order", order));
                bool heldCoupon = await coupon.ExecuteScalarAsync(token) is not null;
                if (!manifest.Complete || unresolved || heldCoupon)
                {
                    await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(5), 100, JobError.Busy, token);
                    return (Guid?)null;
                }
                Guid generation = Guid.CreateVersion7();
                string now = SqliteSchema.UtcText(DateTime.UtcNow);
                string asset = Asset(order, generation);
                byte[] digest = SHA256.HashData(manifest.Bytes);
                await Change(tx, """
                    UPDATE orders SET delivery_generation=$generation,manifest_asset_ref=$asset,manifest_digest=$digest,
                        manifest_prepared_at=$now,release_authorized_at=$now,fulfillment_status='ready',
                        revision=revision+1,updated_at=$now WHERE id=$order AND release_authorized_at IS NULL
                    """, token, ("$generation", generation), ("$asset", asset), ("$digest", digest), ("$now", now), ("$order", order));
                await Change(tx, """
                    UPDATE fulfillments SET status='ready',manifest_generation=$generation
                    WHERE order_id=$order AND status='acquired'
                    """, token, false, ("$generation", generation), ("$order", order));
                state = await ReadAsync(tx, context, order, token);
            }
            using var frozen = await BuildAsync(tx, context, order, state.Quantity, true, token);
            Verify(state, order, frozen);
            long? target = null;
            using (var anchor = SqliteWallet.Command(tx, """
                SELECT telegram_message_id FROM telegram_deliveries
                WHERE order_id=$order AND user_id=$member AND brand_id=$brand AND purpose='order_status'
                    AND status='succeeded'
                ORDER BY created_at,id LIMIT 1
                """, ("$order", order), ("$member", state.Member), ("$brand", context.BrandId)))
            {
                object? result = await anchor.ExecuteScalarAsync(token);
                if (result is long id) target=id;
            }
            Guid? delivery = null;
            if (target is long id && !frozen.IsFile)
                delivery = await EnqueueAsync(tx, context, state, order, id, token);
            await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
            return delivery;
        }, ct);
    }

    internal Task<DeliveryDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid delivery = Require(lease, JobKind.SendTelegram, context);
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            var renewed = await OutboxStore.RenewSqliteAsync(tx, lease, TimeSpan.FromSeconds(30), token);
            if (intent.Status=="succeeded")
            {
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (DeliveryDispatch?)null;
            }
            using (var prior = SqliteWallet.Command(tx, """
                SELECT 1 FROM telegram_deliveries WHERE order_id=$order AND purpose='order_credentials'
                AND delivery_generation=$generation AND status='succeeded' AND id<>$id LIMIT 1
                """, ("$order", intent.Order), ("$generation", intent.Generation), ("$id", delivery)))
            if (await prior.ExecuteScalarAsync(token) is not null)
            {
                await Change(tx, "UPDATE telegram_deliveries SET status='cancelled',last_error_code='known_success' WHERE id=$id",
                    token, ("$id", delivery));
                await OutboxStore.CompleteSqliteAsync(tx, renewed, token);
                return (DeliveryDispatch?)null;
            }
            if (intent.Status is not ("pending" or "retry") || intent.Generation != state.Generation ||
                state.Status is not ("ready" or "delivering" or "completed") || !state.Released ||
                state.Held || state.Review || state.Blocked)
                throw new UnauthorizedAccessException("Credential delivery authority changed.");
            if (intent.Target is long target)
            {
                using var metadata = SqliteWallet.Command(tx, """
                    SELECT status,last_error_code FROM telegram_deliveries
                    WHERE order_id=$order AND purpose='order_status' AND target_message_id=$target
                    """, ("$order", intent.Order), ("$target", target));
                await using var rows = await metadata.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    if (rows.GetString(0)=="uncertain" || (!rows.IsDBNull(1) && rows.GetString(1)=="remote_outcome_unknown"))
                    {
                        await OutboxStore.FailSqliteAsync(tx, renewed, JobError.CredentialRecovery, token);
                        await Change(tx, """
                            UPDATE telegram_deliveries SET status='failed',last_error_code='credential_recovery' WHERE id=$id
                            """, token, ("$id", delivery));
                        return (DeliveryDispatch?)null;
                    }
                    if (rows.GetString(0)=="leased")
                    {
                        await OutboxStore.RetrySqliteAsync(tx, renewed, TimeSpan.FromSeconds(1), 100, JobError.Busy, token);
                        return (DeliveryDispatch?)null;
                    }
                }
            }
            using var frozen = await BuildAsync(tx, context, intent.Order, state.Quantity, true, token);
            Verify(state, intent.Order, frozen);
            if (intent.Target is not null && frozen.IsFile)
            {
                await OutboxStore.FailSqliteAsync(tx, renewed, JobError.CredentialRecovery, token);
                await Change(tx, "UPDATE telegram_deliveries SET status='failed',last_error_code='credential_recovery' WHERE id=$id",
                    token, ("$id", delivery));
                return (DeliveryDispatch?)null;
            }
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            await Change(tx, """
                UPDATE telegram_deliveries SET status='leased',attempts=$attempt,last_attempt_at=$now
                WHERE id=$id AND status IN ('pending','retry')
                """, token, ("$attempt", renewed.Attempt), ("$now", now), ("$id", delivery));
            await Change(tx, """
                UPDATE orders SET fulfillment_status='delivering',revision=revision+1,updated_at=$now
                WHERE id=$order AND fulfillment_status='ready'
                """, token, false, ("$now", now), ("$order", intent.Order));
            return new DeliveryDispatch(delivery, intent.Order, intent.Generation, context.BotId,
                state.Telegram, intent.Target, frozen.IsFile, frozen.Payload.ToArray());
        }, ct);
    }

    internal Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome, CancellationToken ct = default)
    {
        Guid delivery = Require(lease, JobKind.SendTelegram, context);
        if (!Enum.IsDefined(outcome.Kind)) throw new ArgumentException("Invalid delivery outcome.");
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            if (intent.Status=="succeeded") return 0;
            if (intent.Status!="leased" || intent.Attempt!=lease.Attempt ||
                intent.Generation!=state.Generation || !state.Released || state.Held || state.Review)
                throw new UnauthorizedAccessException("Delivery lease no longer owns this intent.");
            bool success = outcome.Kind==DeliveryOutcomeKind.Succeeded && outcome.MessageId is >0 &&
                (intent.Target is null || intent.Target==outcome.MessageId);
            bool retry = intent.Target is not null && outcome.Kind is (DeliveryOutcomeKind.Unknown or DeliveryOutcomeKind.Retry);
            string status = success ? "succeeded" : retry ? "retry" :
                outcome.Kind==DeliveryOutcomeKind.Unknown ? "uncertain" : "failed";
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            string available = SqliteSchema.UtcText(DateTime.UtcNow.AddSeconds(5));
            await Change(tx, """
                UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,available_at=$available,
                    completed_at=CASE WHEN $status='retry' THEN NULL ELSE $now END,last_error_code=$code
                WHERE id=$job AND telegram_delivery_id=$delivery AND attempts=$attempt
                    AND status='leased' AND locked_by=$worker AND lease_until>$now
                """, token, ("$status", success ? "succeeded" : retry ? "retry" : "failed"), ("$available", available),
                ("$now", now), ("$code", success ? "known_success" : status=="uncertain" ? "remote_outcome_unknown" : "credential_recovery"),
                ("$job", lease.Id), ("$delivery", delivery), ("$attempt", lease.Attempt), ("$worker", lease.Worker.ToString("D")));
            await Change(tx, """
                UPDATE telegram_deliveries SET status=$status,telegram_message_id=$message,
                    sent_at=CASE WHEN $status='succeeded' THEN $now ELSE NULL END,
                    last_error_code=$code,next_attempt_at=$available WHERE id=$delivery AND status='leased'
                """, token, ("$status", status), ("$message", success ? outcome.MessageId : null), ("$now", now),
                ("$code", success ? "known_success" : status=="uncertain" ? "remote_outcome_unknown" : "credential_recovery"),
                ("$available", available), ("$delivery", delivery));
            if (success)
            {
                await Change(tx, """
                    UPDATE fulfillments SET status='released',completed_at=$now
                    WHERE order_id=$order AND status='ready' AND manifest_generation=$generation
                    """, token, false, ("$now", now), ("$order", intent.Order), ("$generation", intent.Generation));
                await Change(tx, """
                    UPDATE orders SET fulfillment_status='completed',revision=revision+1,updated_at=$now WHERE id=$order
                    """, token, ("$now", now), ("$order", intent.Order));
            }
            return 0;
        }, ct);
    }

    internal Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default) =>
        sessions.RunAsync(true, async (_, tx, token) =>
        {
            var intent = await ReadIntentAsync(tx, delivery, token);
            var state = await ReadAsync(tx, context, intent.Order, token);
            if (!state.Released || state.Generation!=intent.Generation || state.Held || state.Review)
                throw new UnauthorizedAccessException("Recovery authority changed.");
            string now = SqliteSchema.UtcText(DateTime.UtcNow);
            string status = intent.Target is null ? "failed" : "retry";
            using var command = SqliteWallet.Command(tx, """
                UPDATE jobs SET status=$status,locked_by=NULL,lease_until=NULL,available_at=$now,
                    completed_at=CASE WHEN $status='failed' THEN $now ELSE NULL END,last_error_code='remote_outcome_unknown'
                WHERE telegram_delivery_id=$delivery AND status='leased' AND lease_until<=$now
                """, ("$status", status), ("$now", now), ("$delivery", delivery));
            if (await command.ExecuteNonQueryAsync(token)==1)
                await Change(tx, "UPDATE telegram_deliveries SET status=$status,last_error_code='remote_outcome_unknown' WHERE id=$id AND status='leased'",
                    token, ("$status", intent.Target is null ? "uncertain" : "retry"), ("$id", delivery));
            return 0;
        }, ct);

    internal Task<byte[]> ReadManifestAsync(BrandContext context, long actor, Guid order, CancellationToken ct = default) =>
        sessions.RunAsync(false, async (_, tx, token) =>
        {
            var state = await ReadAsync(tx, context, order, token);
            if (state.Telegram!=actor || !state.Released || state.Held || state.Review || state.Blocked)
                throw new UnauthorizedAccessException("Manifest is unavailable.");
            using var manifest = await BuildAsync(tx, context, order, state.Quantity, true, token);
            Verify(state, order, manifest);
            return manifest.Bytes.ToArray();
        }, ct);

    private static async Task<Guid> EnqueueAsync(SqliteTransaction tx, BrandContext context, State state, Guid order,
        long target, CancellationToken ct)
    {
        using (var lookup = SqliteWallet.Command(tx, """
            SELECT id FROM telegram_deliveries WHERE order_id=$order AND purpose='order_credentials'
            ORDER BY part_no DESC LIMIT 1
            """, ("$order", order)))
        if (await lookup.ExecuteScalarAsync(ct) is string existing) return Guid.Parse(existing);
        Guid id = Guid.CreateVersion7();
        await Change(tx, """
            INSERT INTO telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,
                delivery_generation,dedupe_key,order_id,part_no)
            VALUES ($id,$brand,'customer_dm',$member,'order_credentials','edit',$target,$generation,$key,$order,1)
            """, ct, ("$id", id), ("$brand", context.BrandId), ("$member", state.Member),
            ("$target", target), ("$generation", state.Generation), ("$key", "credentials:"+order.ToString("D")+":"+state.Generation),
            ("$order", order));
        await OutboxStore.EnqueueSqliteAsync(tx, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }

    private static async Task<State> ReadAsync(SqliteTransaction tx, BrandContext context, Guid order, CancellationToken ct)
    {
        using var query = SqliteWallet.Command(tx, """
            SELECT o.user_id,o.quantity,o.order_kind,o.payment_status,o.fulfillment_status,o.delivery_generation,
                o.manifest_asset_ref,o.manifest_digest,o.release_authorized_at IS NOT NULL,
                o.hold_at IS NOT NULL,o.review_code IS NOT NULL,u.telegram_id,
                u.blocked_at IS NOT NULL OR u.banned_at IS NOT NULL
            FROM orders o JOIN users u ON u.id=o.user_id JOIN brands b ON b.id=u.brand_id
            WHERE o.id=$order AND b.id=$brand AND b.bot_id=$bot AND b.active=1
            """, ("$order", order), ("$brand", context.BrandId), ("$bot", context.BotId));
        await using var row = await query.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw new UnauthorizedAccessException("Order is unavailable.");
        return new(row.GetGuid(0), row.GetInt32(1), row.GetString(2), row.GetString(3), row.GetString(4),
            row.IsDBNull(5) ? null : row.GetGuid(5), row.IsDBNull(6) ? null : row.GetString(6),
            row.IsDBNull(7) ? null : row.GetFieldValue<byte[]>(7), row.GetBoolean(8),
            row.GetBoolean(9), row.GetBoolean(10), row.GetInt64(11), row.GetBoolean(12));
    }

    private static async Task<Intent> ReadIntentAsync(SqliteTransaction tx, Guid id, CancellationToken ct)
    {
        using var query = SqliteWallet.Command(tx, """
            SELECT order_id,delivery_generation,target_message_id,status,attempts
            FROM telegram_deliveries WHERE id=$id AND purpose='order_credentials'
            """, ("$id", id));
        await using var row = await query.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.IsDBNull(1)) throw new UnauthorizedAccessException("Delivery is unavailable.");
        return new(row.GetGuid(0), row.GetGuid(1), row.IsDBNull(2) ? null : row.GetInt64(2), row.GetString(3), row.GetInt64(4));
    }

    private async Task<Manifest> BuildAsync(SqliteTransaction tx, BrandContext context, Guid order, int quantity, bool frozen,
        CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        var text = new StringBuilder();
        bool file = false;
        int count=0;
        var cipher = new BrandCipher(secrets);
        writer.Write(1);
        writer.Write(quantity);
        using var query = SqliteWallet.Command(tx, """
            SELECT id,unit_no,content_kind,delivered_content,delivered_content_key_id,status,manifest_generation
            FROM fulfillments WHERE order_id=$order ORDER BY unit_no
            """, ("$order", order));
        await using var row = await query.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct))
        {
            int number = row.GetInt32(1);
            string status = row.GetString(5);
            if (number!=count+1 || row.IsDBNull(2) || row.IsDBNull(3) || row.IsDBNull(4) ||
                (frozen ? status is not ("ready" or "released") || row.IsDBNull(6) : status!="acquired"))
                return new([],[],false,true);
            Guid id = row.GetGuid(0);
            byte[] plain = cipher.Decrypt(context, "fulfillments", id, "delivered_content", row.GetString(4),
                row.GetFieldValue<byte[]>(3));
            try
            {
                string kind = row.GetString(2);
                writer.Write(number);
                writer.Write(kind);
                writer.Write(plain.Length);
                writer.Write(plain);
                if (bytes.Length>16777216) throw new InvalidOperationException("Manifest exceeds delivery limit.");
                if (kind=="file") file=true;
                if (!file)
                {
                    try { text.Append(number).Append(". ").Append(new UTF8Encoding(false,true).GetString(plain)).Append('\n'); }
                    catch (DecoderFallbackException) { file=true; }
                    if (text.Length>4096) file=true;
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            count++;
        }
        byte[] content = bytes.ToArray();
        return new(content, file ? content.ToArray() : Encoding.UTF8.GetBytes(text.ToString()), count==quantity, file);
    }

    private static void Verify(State state, Guid order, Manifest manifest)
    {
        if (state.Status is not ("ready" or "delivering" or "completed") || !state.Released ||
            state.Generation is not Guid generation || !manifest.Complete || state.Asset!=Asset(order,generation) ||
            state.Digest is null || !CryptographicOperations.FixedTimeEquals(state.Digest,SHA256.HashData(manifest.Bytes)))
            throw new UnauthorizedAccessException("Manifest is unavailable.");
    }
    private static string Asset(Guid order, Guid generation) => "manifest:"+order.ToString("D")+":"+generation.ToString("D");
    private static Guid Require(JobLease lease, JobKind kind, BrandContext context) =>
        lease.Intent.Kind==kind && lease.Intent.BrandId==context.BrandId && lease.Intent.OriginId is Guid id
            ? id : throw new UnauthorizedAccessException("Job origin is unavailable.");

    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] values) => await Change(tx, sql, ct, true, values);
    private static async Task Change(SqliteTransaction tx, string sql, CancellationToken ct, bool exactlyOne,
        params (string Name, object? Value)[] values)
    {
        using var command = SqliteWallet.Command(tx, sql, values);
        int changed = await command.ExecuteNonQueryAsync(ct);
        if (exactlyOne && changed!=1) throw new UnauthorizedAccessException("Concurrent delivery modification.");
    }

    private sealed class Manifest(byte[] bytes, byte[] payload, bool complete, bool file) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        internal byte[] Payload { get; } = payload;
        internal bool Complete { get; } = complete;
        internal bool IsFile { get; } = file;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Bytes);
            CryptographicOperations.ZeroMemory(Payload);
        }
    }
    private sealed record State(Guid Member,int Quantity,string Kind,string Payment,string Status,Guid? Generation,
        string? Asset,byte[]? Digest,bool Released,bool Held,bool Review,long Telegram,bool Blocked);
    private sealed record Intent(Guid Order,Guid Generation,long? Target,string Status,long Attempt);
}
