using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public enum DeliveryOutcomeKind { Succeeded, Retry, Uneditable, Unknown }
public sealed record DeliveryOutcome(DeliveryOutcomeKind Kind, long? MessageId = null);
public sealed class DeliveryDispatch : IDisposable
{
    internal DeliveryDispatch(Guid id, Guid order, Guid generation, long bot, long chat, long? target, bool file, byte[] content)
    { Id = id; Order = order; Generation = generation; BotId = bot; ChatId = chat; TargetMessageId = target; IsFile = file; this.content = content; }
    private readonly byte[] content;
    public Guid Id { get; }
    public Guid Order { get; }
    public Guid Generation { get; }
    public long BotId { get; }
    public long ChatId { get; }
    public long? TargetMessageId { get; }
    public bool IsFile { get; }
    public ReadOnlyMemory<byte> Content => content;
    public void Dispose() => CryptographicOperations.ZeroMemory(content);
    public override string ToString() => "DeliveryDispatch [redacted]";
}

public sealed class DeliveryStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<Guid?> PrepareAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        if (lease.Intent.Kind != JobKind.PrepareManifest || lease.Intent.BrandId != context.BrandId || lease.Intent.OriginId is not Guid order) throw new UnauthorizedAccessException();
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            State state = await LockAsync(session, context, order, ct);
            await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
            if (state.Kind != "product_purchase" || state.Payment is not ("paid" or "not_required") || state.Held || state.Review) Reject(session);
            if (state.Generation is null)
            {
                using Manifest manifest = await BuildAsync(session, context, order, state.Quantity, false, ct);
                bool unresolved = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.supplier_requests WHERE order_id=$1 AND status NOT IN ('succeeded','resolved'))", DbValue.Uuid(order)), r => r.GetBoolean(0), ct);
                if (!manifest.Complete || unresolved)
                {
                    await OutboxStore.RetryAsync(session, lease, TimeSpan.FromSeconds(5), 100, JobError.Busy, ct);
                    await session.CommitAsync(ct);
                    return null;
                }
                Guid generation = Guid.CreateVersion7();
                await session.ExecuteAsync(new DbStatement("""
                    UPDATE shop.orders SET delivery_generation=$1,manifest_asset_ref=$2,manifest_digest=$3,manifest_prepared_at=clock_timestamp(),
                        release_authorized_at=clock_timestamp(),fulfillment_status='ready',revision=revision+1,updated_at=clock_timestamp() WHERE id=$4
                    """, DbValue.Uuid(generation), DbValue.Text(Asset(order, generation)), DbValue.Binary(SHA256.HashData(manifest.Bytes)), DbValue.Uuid(order)), ct);
                await session.ExecuteAsync(new DbStatement("UPDATE shop.fulfillments SET status='ready',manifest_generation=$1 WHERE order_id=$2 AND status='acquired'", DbValue.Uuid(generation), DbValue.Uuid(order)), ct);
                Guid? coupon = await session.QuerySingleAsync(new DbStatement("SELECT discount_id FROM (SELECT 1) seed LEFT JOIN shop.discount_uses ON order_id=$1 AND status='held'", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
                if (coupon is Guid id) await new PricingStore(sessions, secrets).TransitionClaimAsync(session, context, id, order, true, ct);
                state = await LockAsync(session, context, order, ct);
            }
            using Manifest frozen = await BuildAsync(session, context, order, state.Quantity, true, ct);
            Verify(state, order, frozen);
            long? anchor = await session.QuerySingleAsync(new DbStatement("""
                SELECT telegram_message_id FROM (SELECT 1) seed LEFT JOIN LATERAL (SELECT telegram_message_id FROM shop.telegram_deliveries
                    WHERE order_id=$1 AND user_id=$2 AND brand_id=$3 AND purpose='order_status' AND status='succeeded'
                    ORDER BY created_at,id LIMIT 1) a ON true
                """, DbValue.Uuid(order), DbValue.Uuid(state.Member), DbValue.Uuid(context.BrandId)), r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ct);
            Guid? delivery = null;
            if (anchor is long target && !frozen.IsFile) delivery = await EnqueueAsync(session, context, state, order, target, null, ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            await session.CommitAsync(ct);
            return delivery;
        }
        catch (ManifestNeedsRecoveryException)
        {
            await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='requires_admin',review_code='credential_recovery',revision=revision+1 WHERE id=$1 AND release_authorized_at IS NULL", DbValue.Uuid(order)), ct);
            await OutboxStore.FailAsync(session, lease, JobError.CredentialRecovery, ct);
            await session.CommitAsync(ct);
            return null;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }

    internal static async Task RecordAnchorAsync(DbSession session, BrandContext context, long actor, Guid order, long message, CancellationToken ct)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(context.BrandId), DbValue.Bigint(actor)), r => r.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        bool valid = await session.QuerySingleAsync(new DbStatement("""
            SELECT EXISTS(SELECT 1 FROM shop.users u JOIN shop.orders o ON o.id=u.flow_order_id WHERE u.id=$1 AND o.id=$2 AND o.user_id=u.id
                AND u.flow_anchor_message_id=$3 AND u.flow_expires_at>clock_timestamp())
            """, DbValue.Uuid(member.Id), DbValue.Uuid(order), DbValue.Bigint(message)), r => r.GetBoolean(0), ct);
        if (!valid || message <= 0) Reject(session);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,status,telegram_message_id,sent_at,order_id)
            VALUES (gen_random_uuid(),$1,'customer_dm',$2,'order_status','send',$3,'succeeded',$4,clock_timestamp(),$5) ON CONFLICT(dedupe_key) DO NOTHING
            """, DbValue.Uuid(context.BrandId), DbValue.Uuid(member.Id), DbValue.Text("order-anchor:" + order.ToString("D") + ":" + message.ToString(System.Globalization.CultureInfo.InvariantCulture)), DbValue.Bigint(message), DbValue.Uuid(order)), ct);
    }

    public async Task<DeliveryDispatch?> BeginAsync(BrandContext context, JobLease lease, CancellationToken ct = default)
    {
        Guid delivery = Origin(context, lease);
        await using var session = await sessions.BeginAsync(ct);
        var intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        await OutboxStore.RenewAsync(session, lease, TimeSpan.FromSeconds(30), ct);
        intent = await ReadIntentAsync(session, delivery, ct);
        if (intent.Status == "succeeded")
        { await OutboxStore.CompleteAsync(session, lease, ct); await session.CommitAsync(ct); return null; }
        bool alreadySent = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_credentials' AND delivery_generation=$2 AND status='succeeded' AND id<>$3)",
            DbValue.Uuid(intent.Order), DbValue.Uuid(intent.Generation), DbValue.Uuid(delivery)), r => r.GetBoolean(0), ct);
        if (alreadySent)
        {
            await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='cancelled',last_error_code='known_success' WHERE id=$1 AND status IN ('pending','retry')", DbValue.Uuid(delivery)), ct);
            await OutboxStore.CompleteAsync(session, lease, ct);
            await session.CommitAsync(ct);
            return null;
        }
        if (intent.Status is not ("pending" or "retry") || intent.Generation != state.Generation || state.Status is not ("ready" or "delivering" or "completed") || !state.Released || state.Held || state.Review || state.Blocked) Reject(session);
        if (intent.Target is long statusTarget)
        {
            var metadata = await session.QuerySingleAsync(new DbStatement("SELECT COALESCE(bool_or(d.status='leased'),false),COALESCE(bool_or(d.last_error_code='remote_outcome_unknown' OR d.status='uncertain' OR (d.status='leased' AND EXISTS(SELECT 1 FROM shop.jobs j WHERE j.telegram_delivery_id=d.id AND j.status='leased' AND j.lease_until<=clock_timestamp()))),false) FROM shop.telegram_deliveries d WHERE d.order_id=$1 AND d.purpose='order_status' AND d.target_message_id=$2", DbValue.Uuid(intent.Order), DbValue.Bigint(statusTarget)), r => (Active: r.GetBoolean(0), Unknown: r.GetBoolean(1)), ct);
            if (metadata.Unknown)
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='failed',last_error_code='credential_recovery' WHERE id=$1", DbValue.Uuid(delivery)), ct);
                await OutboxStore.FailAsync(session, lease, JobError.CredentialRecovery, ct); await session.CommitAsync(ct); return null;
            }
            if (metadata.Active)
            {
                await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='retry',locked_by=NULL,lease_until=NULL,available_at=clock_timestamp()+INTERVAL '1 second',last_error_code='metadata_in_flight' WHERE id=$1", DbValue.Uuid(lease.Id)), ct);
                await session.CommitAsync(ct); return null;
            }
        }
        using Manifest manifest = await BuildAsync(session, context, intent.Order, state.Quantity, true, ct);
        Verify(state, intent.Order, manifest);
        if (intent.Target is not null && manifest.IsFile) Reject(session);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='leased',attempts=$1,last_attempt_at=clock_timestamp() WHERE id=$2", DbValue.Bigint(lease.Attempt), DbValue.Uuid(delivery)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='delivering',revision=revision+1 WHERE id=$1 AND fulfillment_status='ready'", DbValue.Uuid(intent.Order)), ct);
        var result = new DeliveryDispatch(delivery, intent.Order, state.Generation!.Value, context.BotId, state.Telegram, intent.Target, manifest.IsFile, manifest.Payload.ToArray());
        await session.CommitAsync(ct);
        return result;
    }

    public async Task RecordOutcomeAsync(BrandContext context, JobLease lease, DeliveryOutcome outcome, CancellationToken ct = default)
    {
        Guid delivery = Origin(context, lease);
        if (!Enum.IsDefined(outcome.Kind)) throw new ArgumentException("Invalid delivery outcome.");
        await using var session = await sessions.BeginAsync(ct);
        Intent intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        intent = await ReadIntentAsync(session, delivery, ct);
        if (intent.Status == "succeeded") { await session.CommitAsync(ct); return; }
        if ((outcome.Kind != DeliveryOutcomeKind.Succeeded && intent.Status != "leased") || intent.Attempt != lease.Attempt || intent.Generation != state.Generation || !state.Released || state.Held || state.Review) Reject(session);
        bool success = outcome.Kind == DeliveryOutcomeKind.Succeeded && outcome.MessageId is > 0 && (intent.Target is null || intent.Target == outcome.MessageId);
        bool retry = intent.Target is not null && outcome.Kind is DeliveryOutcomeKind.Unknown or DeliveryOutcomeKind.Retry;
        string status = success ? "succeeded" : retry ? "retry" : outcome.Kind == DeliveryOutcomeKind.Unknown ? "uncertain" : "failed";
        int fenced = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,available_at=clock_timestamp()+INTERVAL '5 seconds',
                completed_at=CASE WHEN $1='retry' THEN NULL ELSE clock_timestamp() END,last_error_code=$2
            WHERE id=$3 AND telegram_delivery_id=$4 AND attempts=$5 AND status IN ('leased','retry','failed')
            """, DbValue.Text(success ? "succeeded" : retry ? "retry" : "failed"), DbValue.Text(success ? "known_success" : status == "uncertain" ? "remote_outcome_unknown" : "credential_recovery"), DbValue.Uuid(lease.Id), DbValue.Uuid(delivery), DbValue.Bigint(lease.Attempt)), ct);
        if (fenced != 1) Reject(session);
        await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.telegram_deliveries SET status=$1,telegram_message_id=$2,sent_at=CASE WHEN $1='succeeded' THEN clock_timestamp() ELSE NULL END,
                last_error_code=$3,next_attempt_at=clock_timestamp()+INTERVAL '5 seconds' WHERE id=$4
            """, DbValue.Text(status), success ? DbValue.Bigint(outcome.MessageId!.Value) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Text(success ? "known_success" : "credential_recovery"), DbValue.Uuid(delivery)), ct);
        if (success)
        {
            await session.ExecuteAsync(new DbStatement("UPDATE shop.fulfillments SET status='released',completed_at=clock_timestamp() WHERE order_id=$1 AND status='ready' AND manifest_generation=$2", DbValue.Uuid(intent.Order), DbValue.Uuid(intent.Generation)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.orders SET fulfillment_status='completed',revision=revision+1,updated_at=clock_timestamp() WHERE id=$1", DbValue.Uuid(intent.Order)), ct);
        }
        await session.CommitAsync(ct);
    }

    public async Task RecoverExpiredAsync(BrandContext context, Guid delivery, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        Intent intent = await ReadIntentAsync(session, delivery, ct);
        State state = await LockAsync(session, context, intent.Order, ct);
        if (!state.Released || state.Generation != intent.Generation || state.Held || state.Review) Reject(session);
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.jobs SET status=$1,locked_by=NULL,lease_until=NULL,available_at=clock_timestamp(),completed_at=CASE WHEN $1='failed' THEN clock_timestamp() ELSE NULL END,
                last_error_code='remote_outcome_unknown' WHERE telegram_delivery_id=$2 AND status='leased' AND lease_until<=clock_timestamp()
            """, DbValue.Text(intent.Target is null ? "failed" : "retry"), DbValue.Uuid(delivery)), ct);
        if (changed == 1) await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status=$1,last_error_code='remote_outcome_unknown' WHERE id=$2 AND status='leased'", DbValue.Text(intent.Target is null ? "uncertain" : "retry"), DbValue.Uuid(delivery)), ct);
        await session.CommitAsync(ct);
    }

    public async Task<Guid> AuthorizeSendRecoveryAsync(BrandContext context, AuthenticatedAdmin actor, Guid order, Guid command, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        var audit = new AuditCommand(command, AuditAction.DeliveryRecovery, AuditEntity.Order, order, context.BrandId, AuditReason.Reconciliation,
            Guid.NewGuid(), SHA256.HashData(Encoding.ASCII.GetBytes("credential-send:" + order.ToString("D"))));
        bool fresh = await AdminAuditStore.AppendAsync(session, actor, audit, ct);
        State state = await LockAsync(session, context, order, ct);
        if (!state.Released || state.Held || state.Review) Reject(session);
        Guid? existing = await session.QuerySingleAsync(new DbStatement("SELECT id FROM (SELECT 1) seed LEFT JOIN LATERAL (SELECT id FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_credentials' ORDER BY part_no DESC LIMIT 1) d ON true", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
        Guid result;
        if (existing is Guid id)
        {
            Intent intent = await ReadIntentAsync(session, id, ct);
            if (!fresh || intent.Status == "succeeded") { await session.CommitAsync(ct); return id; }
            if (intent.Status is not ("uncertain" or "failed")) Reject(session);
            if (intent.Target is not null)
            {
                result = await EnqueueAsync(session, context, state, order, null, command, ct, true);
                await session.CommitAsync(ct);
                return result;
            }
            await session.ExecuteAsync(new DbStatement("UPDATE shop.telegram_deliveries SET status='retry',audit_event_id=$1,next_attempt_at=clock_timestamp() WHERE id=$2", DbValue.Uuid(command), DbValue.Uuid(id)), ct);
            await session.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET status='retry',completed_at=NULL,locked_by=NULL,lease_until=NULL,available_at=clock_timestamp() WHERE telegram_delivery_id=$1 AND status='failed'", DbValue.Uuid(id)), ct);
            result = id;
        }
        else result = await EnqueueAsync(session, context, state, order, null, command, ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<byte[]> ReadManifestAsync(BrandContext context, long actor, Guid order, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, context, actor, null, ct);
        State state = await LockAsync(session, context, order, ct);
        if (member.Id != state.Member || !state.Released || state.Held || state.Review) Reject(session);
        using Manifest manifest = await BuildAsync(session, context, order, state.Quantity, true, ct);
        Verify(state, order, manifest);
        return manifest.Bytes.ToArray();
    }

    private static Guid Origin(BrandContext context, JobLease lease) => lease.Intent.Kind == JobKind.SendTelegram && lease.Intent.BrandId == context.BrandId && lease.Intent.OriginId is Guid id ? id : throw new UnauthorizedAccessException();
    private static string Asset(Guid order, Guid generation) => "manifest:" + order.ToString("D") + ":" + generation.ToString("D");
    private static void Verify(State state, Guid order, Manifest manifest)
    {
        if (state.Status is not ("ready" or "delivering" or "completed") || !state.Released || state.Generation is not Guid generation || !manifest.Complete || state.Asset != Asset(order, generation) || state.Digest is null ||
            !CryptographicOperations.FixedTimeEquals(state.Digest, SHA256.HashData(manifest.Bytes))) throw new UnauthorizedAccessException("Manifest is unavailable.");
    }
    private static async Task<Guid> EnqueueAsync(DbSession session, BrandContext context, State state, Guid order, long? target, Guid? audit, CancellationToken ct, bool recovery = false)
    {
        Guid? prior = await session.QuerySingleAsync(new DbStatement("SELECT id FROM (SELECT 1) seed LEFT JOIN LATERAL (SELECT id FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_credentials' ORDER BY part_no DESC LIMIT 1) d ON true", DbValue.Uuid(order)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
        if (prior is Guid old && !recovery) return old;
        int part = await session.QuerySingleAsync(new DbStatement("SELECT COALESCE(max(part_no),0)+1 FROM shop.telegram_deliveries WHERE order_id=$1 AND purpose='order_credentials'", DbValue.Uuid(order)), r => r.GetInt32(0), ct);
        Guid id = Guid.CreateVersion7();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,delivery_generation,dedupe_key,order_id,audit_event_id,part_no)
            VALUES ($1,$2,'customer_dm',$3,'order_credentials',$4,$5,$6,$7,$8,$9,$10)
            """, DbValue.Uuid(id), DbValue.Uuid(context.BrandId), DbValue.Uuid(state.Member), DbValue.Text(target is null ? "send" : "edit"), target is long t ? DbValue.Bigint(t) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint),
            DbValue.Uuid(state.Generation!.Value), DbValue.Text("credentials:" + order.ToString("D") + ":" + state.Generation.Value.ToString("D") + (recovery ? ":" + audit!.Value.ToString("D") : "")), DbValue.Uuid(order), audit is Guid a ? DbValue.Uuid(a) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid), DbValue.Integer(part)), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }
    private static Task<Intent> ReadIntentAsync(DbSession session, Guid id, CancellationToken ct) => session.QuerySingleAsync(new DbStatement("SELECT order_id,delivery_generation,target_message_id,status,attempts FROM shop.telegram_deliveries WHERE id=$1 AND purpose='order_credentials'", DbValue.Uuid(id)),
        r => new Intent(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), r.GetInt64(4)), ct);
    private static async Task<State> LockAsync(DbSession session, BrandContext context, Guid order, CancellationToken ct)
    {
        Identity? identity = await session.QuerySingleAsync<Identity?>(new DbStatement("SELECT o.user_id,o.product_id FROM (SELECT 1) seed LEFT JOIN (SELECT o.user_id,o.product_id FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND u.brand_id=$2) o ON true", DbValue.Uuid(order), DbValue.Uuid(context.BrandId)), r => r.IsDBNull(0) ? null : new Identity(r.GetGuid(0), r.GetGuid(1)), ct);
        if (identity is null) Reject(session);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE id=$1 FOR UPDATE) u", DbValue.Uuid(identity.User)), r => r.GetInt64(0), ct);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.products WHERE id=$1 FOR UPDATE) p", DbValue.Uuid(identity.Product)), r => r.GetInt64(0), ct);
        long registered = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.brands WHERE id=$1 AND bot_id=$2 AND active FOR UPDATE) b", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetInt64(0), ct);
        if (registered != 1) Reject(session);
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT d.id FROM shop.discounts d JOIN shop.discount_uses c ON c.discount_id=d.id WHERE c.order_id=$1 ORDER BY d.id FOR UPDATE OF d) coupons", DbValue.Uuid(order)), r => r.GetInt64(0), ct);
        return await session.QuerySingleAsync(new DbStatement("""
            SELECT o.user_id,o.quantity,o.order_kind,o.payment_status,o.fulfillment_status,o.delivery_generation,o.manifest_asset_ref,o.manifest_digest,o.release_authorized_at IS NOT NULL,
                o.hold_at IS NOT NULL,o.review_code IS NOT NULL,u.telegram_id,u.blocked_at IS NOT NULL OR u.banned_at IS NOT NULL FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 FOR UPDATE OF o
            """, DbValue.Uuid(order)), r => new State(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetBinary(7), r.GetBoolean(8), r.GetBoolean(9), r.GetBoolean(10), r.GetInt64(11), r.GetBoolean(12)), ct);
    }
    private async Task<Manifest> BuildAsync(DbSession session, BrandContext context, Guid order, int quantity, bool frozen, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var text = new StringBuilder();
        bool file = false;
        var cipher = new BrandCipher(secrets);
        int count = 0;
        try
        {
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            writer.Write(1); writer.Write(quantity);
            while (count < quantity)
            {
                var rows = await session.QueryManyAsync(new DbStatement("SELECT id,unit_no,content_kind,delivered_content,delivered_content_key_id,status,manifest_generation FROM shop.fulfillments WHERE order_id=$1 ORDER BY unit_no LIMIT 1000 OFFSET $2 FOR UPDATE", DbValue.Uuid(order), DbValue.Integer(count)),
                    r => new Part(r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetBinary(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6)), 1000, ct);
                if (rows.Count == 0) break;
                foreach (Part part in rows)
                {
                    if (part.Number != count + 1 || part.Content is null || part.Key is null || part.Kind is null || (frozen ? part.Status is not ("ready" or "released") || part.Generation is null : part.Status != "acquired"))
                        return new([], [], false, true);
                    byte[] plain = cipher.Decrypt(context, "fulfillments", part.Id, "delivered_content", part.Key, part.Content);
                    try
                    {
                        writer.Write(part.Number); writer.Write(part.Kind); writer.Write(plain.Length); writer.Write(plain);
                        if (bytes.Length > 16777216) throw new ManifestNeedsRecoveryException();
                        if (part.Kind == "file") file = true;
                        if (!file)
                        {
                            try { text.Append(part.Number).Append(". ").Append(new UTF8Encoding(false, true).GetString(plain)).Append('\n'); }
                            catch (DecoderFallbackException) { file = true; }
                            if (text.Length > 4096) file = true;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    count++;
                }
            }
            byte[] content = bytes.ToArray();
            return new(content, file ? content.ToArray() : Encoding.UTF8.GetBytes(text.ToString()), count == quantity, file);
        }
        finally { CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
    }
    private sealed class Manifest(byte[] bytes, byte[] payload, bool complete, bool file) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public byte[] Payload { get; } = payload;
        public bool Complete { get; } = complete;
        public bool IsFile { get; } = file;
        public void Dispose() { CryptographicOperations.ZeroMemory(Bytes); CryptographicOperations.ZeroMemory(Payload); }
    }
    private sealed class ManifestNeedsRecoveryException : Exception { }
    private sealed record Identity(Guid User, Guid Product);
    private sealed record State(Guid Member, int Quantity, string Kind, string Payment, string Status, Guid? Generation, string? Asset, byte[]? Digest, bool Released, bool Held, bool Review, long Telegram, bool Blocked);
    private sealed record Intent(Guid Order, Guid Generation, long? Target, string Status, long Attempt);
    private sealed record Part(Guid Id, int Number, string? Kind, byte[]? Content, string? Key, string Status, Guid? Generation);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(DbSession session) { session.RejectApplication(); throw new UnauthorizedAccessException("Credential delivery requires current complete-order authority."); }
}
