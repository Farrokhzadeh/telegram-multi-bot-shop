using NpgsqlTypes;
using Platform.Security;

namespace Platform.Persistence;

public static class SupportStore
{
    public static async Task<(Guid Id, bool Created)> OpenTicketAsync(DbSession session, BrandContext brand, long verifiedActorId, Guid? orderId, CancellationToken ct = default)
    {
        BrandUser member = await RequireCustomerAsync(session, brand, verifiedActorId, ct);
        string category;
        if (orderId is Guid order)
        {
            bool sameMember = await session.QuerySingleAsync(new DbStatement(
                "SELECT EXISTS(SELECT 1 FROM shop.orders WHERE id=$1 AND user_id=$2)", DbValue.Uuid(order), DbValue.Uuid(member.Id)), row => row.GetBoolean(0), ct);
            if (!sameMember) Deny(session);
            bool paidPreorder = await session.QuerySingleAsync(new DbStatement(
                "SELECT EXISTS(SELECT 1 FROM shop.orders WHERE id=$1 AND is_preorder AND payment_status='paid')", DbValue.Uuid(order)), row => row.GetBoolean(0), ct);
            category = paidPreorder ? "preorder_cancellation" : "order";
        }
        else category = "general";
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)",
            DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(
                    "support-ticket:" + member.Id.ToString("D") + ":" + category + ":" + (orderId?.ToString("D") ?? "null")))))), ct);
        Guid? existing = await session.QuerySingleAsync(new DbStatement("""
            SELECT t.id FROM (SELECT 1) seed
            LEFT JOIN shop.support_tickets t ON t.user_id=$1 AND t.category=$2 AND t.order_id IS NOT DISTINCT FROM $3 AND t.status<>'closed'
            """, DbValue.Uuid(member.Id), DbValue.Text(category), Uuid(orderId)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
        if (existing.HasValue) return (existing.Value, false);
        Guid id = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.support_tickets(id,user_id,order_id,category,source) VALUES ($1,$2,$3,$4,'customer')
            """, DbValue.Uuid(id), DbValue.Uuid(member.Id), Uuid(orderId), DbValue.Text(category)), ct);
        return (id, true);
    }

    public static async Task<Guid> RecordCustomerMessageAsync(DbSession session, BrandContext brand, long verifiedActorId, Guid ticketId,
        string? assetRef, byte[]? bodyCiphertext = null, string? bodyKeyId = null, CancellationToken ct = default, Guid? messageId = null, long? telegramMessageId = null)
    {
        BrandUser member = await RequireCustomerAsync(session, brand, verifiedActorId, ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.UserId != member.Id || t.Status == "closed") Deny(session);
        ValidateContent(assetRef, bodyCiphertext, bodyKeyId);
        Guid id = messageId ?? Guid.NewGuid();
        if (id == Guid.Empty || telegramMessageId <= 0) throw new ArgumentException("Invalid message identity.");
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,sender_telegram_id,asset_ref,body_ciphertext,body_key_id,telegram_message_id)
            VALUES ($1,$2,'customer',$3,$4,$5,$6,$7)
            """, DbValue.Uuid(id), DbValue.Uuid(ticketId), DbValue.Bigint(verifiedActorId), TextOrNull(assetRef), Binary(bodyCiphertext), TextOrNull(bodyKeyId), telegramMessageId is long external ? DbValue.Bigint(external) : DbValue.Null(NpgsqlDbType.Bigint)), ct);
        await session.ExecuteAsync(new DbStatement("UPDATE shop.support_tickets SET status=CASE WHEN assigned_admin_id IS NULL THEN 'open' ELSE 'assigned' END,updated_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(ticketId)), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Support(t.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }

    public static async Task<Guid> AssignTicketAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, AuditReason reason, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.Status == "closed") Deny(session);
        if (!(t.Status == "assigned" && t.AssignedAdminId == actor.AdminId))
        {
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.support_tickets SET status='assigned',assigned_admin_id=$1,updated_at=CURRENT_TIMESTAMP
                WHERE id=$2 AND status<>'closed' AND (assigned_admin_id IS NULL OR assigned_admin_id=$1)
                """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(ticketId)), ct);
            if (changed != 1) Deny(session);
            await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
                VALUES ($1,'admin',$2,$3,'support_assign','support_ticket',$4,$5,gen_random_uuid())
                """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(actor.AdminId), DbValue.Uuid(t.BrandId), DbValue.Uuid(ticketId), DbValue.Text(AdminAuditStore.ReasonCode(reason))), ct);
        }
        return ticketId;
    }

    public static async Task<Guid> RecordStaffReplyAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, string? assetRef,
        byte[]? bodyCiphertext = null, string? bodyKeyId = null, CancellationToken ct = default, Guid? requestId = null)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        Guid messageId = requestId ?? Guid.NewGuid();
        if (messageId == Guid.Empty) throw new ArgumentException("Invalid message identity.");
        Reply? prior = await session.QuerySingleAsync<Reply?>(new DbStatement("SELECT m.ticket_id,m.sender_admin_id,m.asset_ref,m.body_ciphertext,m.body_key_id FROM (SELECT 1) seed LEFT JOIN shop.support_messages m ON m.id=$1", DbValue.Uuid(messageId)),
            row => row.IsDBNull(0) ? null : new Reply(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.IsDBNull(2) ? null : row.GetString(2), row.IsDBNull(3) ? null : row.GetBinary(3), row.IsDBNull(4) ? null : row.GetString(4)), ct);
        if (prior is not null)
        {
            if (prior.Ticket != ticketId || prior.Admin != actor.AdminId || prior.Asset != assetRef || prior.Key != bodyKeyId ||
                (prior.Body is null) != (bodyCiphertext is null) || (prior.Body is not null && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(prior.Body, bodyCiphertext!))) Deny(session);
            return messageId;
        }
        if (t.Status == "closed") Deny(session);
        ValidateContent(assetRef, bodyCiphertext, bodyKeyId);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.support_messages(id,ticket_id,sender_kind,sender_admin_id,asset_ref,body_ciphertext,body_key_id)
            VALUES ($1,$2,'admin',$3,$4,$5,$6)
            """, DbValue.Uuid(messageId), DbValue.Uuid(ticketId), DbValue.Uuid(actor.AdminId), TextOrNull(assetRef), Binary(bodyCiphertext), TextOrNull(bodyKeyId)), ct);
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.support_tickets SET status='waiting_customer',assigned_admin_id=$1,updated_at=CURRENT_TIMESTAMP WHERE id=$2 AND status<>'closed'
            """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(ticketId)), ct);
        if (changed != 1) Deny(session);
        await AuditAsync(session, actor, t, "update", ct);
        await EnqueueCustomerNoticeAsync(session, t, messageId, ct);
        return messageId;
    }

    public static async Task CloseTicketAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.Status == "closed") return;
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.support_tickets SET status='closed',closed_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP WHERE id=$1 AND status<>'closed'
            """, DbValue.Uuid(ticketId)), ct);
        if (changed != 1) Deny(session);
        await AuditAsync(session, actor, t, "update", ct);
    }

    public static async Task ReopenTicketAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.Status != "closed") return;
        int changed = await session.ExecuteAsync(new DbStatement("""
            UPDATE shop.support_tickets SET status='open',closed_at=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$1 AND status='closed'
            """, DbValue.Uuid(ticketId)), ct);
        if (changed != 1) Deny(session);
        await AuditAsync(session, actor, t, "update", ct);
    }

    private static async Task EnqueueCustomerNoticeAsync(DbSession session, Ticket t, Guid messageId, CancellationToken ct)
    {
        Guid deliveryId = Guid.NewGuid();
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,dedupe_key,support_message_id)
            VALUES ($1,$2,'customer_dm',$3,'support_notice','send',$4,$5)
            """, DbValue.Uuid(deliveryId), DbValue.Uuid(t.BrandId), DbValue.Uuid(t.UserId), DbValue.Text("support:" + messageId.ToString("D")), DbValue.Uuid(messageId)), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(t.BrandId, deliveryId), messageId, DateTime.UtcNow, ct);
    }

    private static Task<Ticket?> ReadAsync(DbSession session, Guid ticketId, CancellationToken ct) =>
        session.QuerySingleAsync(new DbStatement("""
            SELECT t.id,t.user_id,u.brand_id,t.category,t.status,t.order_id,t.assigned_admin_id
            FROM (SELECT 1) seed LEFT JOIN (SELECT * FROM shop.support_tickets WHERE id=$1 FOR UPDATE) t ON true LEFT JOIN shop.users u ON u.id=t.user_id
            """, DbValue.Uuid(ticketId)), row => row.IsDBNull(0) ? null : new Ticket(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2),
                row.GetString(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetGuid(5), row.IsDBNull(6) ? null : row.GetGuid(6)), ct);

    private static async Task<Ticket> RequireTicketAsync(DbSession session, Guid ticketId, CancellationToken ct)
    {
        if (session.IsWriterScoped)
        {
            var owner = await session.QuerySingleAsync(new DbStatement("SELECT t.user_id,u.brand_id FROM shop.support_tickets t JOIN shop.users u ON u.id=t.user_id WHERE t.id=$1", DbValue.Uuid(ticketId)), r => (User: r.GetGuid(0), Brand: r.GetGuid(1)), ct);
            await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(owner.User)), r => r.GetGuid(0), ct);
            await session.AdmitBrandWriterAsync(owner.Brand, ct);
        }
        Ticket? t = await ReadAsync(session, ticketId, ct);
        if (t is null) Deny(session);
        return t!;
    }

    private static async Task<BrandUser> RequireCustomerAsync(DbSession session, BrandContext brand, long actorId, CancellationToken ct)
    {
        await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM (SELECT id FROM shop.users WHERE brand_id=$1 AND telegram_id=$2 FOR UPDATE) u", DbValue.Uuid(brand.BrandId), DbValue.Bigint(actorId)), row => row.GetInt64(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, brand, actorId, null, ct);
        if (member.Blocked || member.Banned) Deny(session);
        await session.AdmitWriterAsync(brand, ct);
        return member;
    }

    private static Task<int> AuditAsync(DbSession session, AuthenticatedAdmin actor, Ticket ticket, string action, CancellationToken ct) =>
        session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES (gen_random_uuid(),'admin',$1,$2,$3,'support_ticket',$4,'operator_review',gen_random_uuid())
            """, DbValue.Uuid(actor.AdminId), DbValue.Uuid(ticket.BrandId), DbValue.Text(action), DbValue.Uuid(ticket.Id)), ct);

    private static void ValidateContent(string? assetRef, byte[]? bodyCiphertext, string? bodyKeyId)
    {
        bool hasBody = bodyCiphertext is not null;
        if (hasBody != (bodyKeyId is not null)) throw new ArgumentException("Message requires both an encrypted body and its key id.");
        if (!hasBody && assetRef is null) throw new ArgumentException("Message requires an asset reference or an encrypted body.");
        if (assetRef is not null && (assetRef.Length < 1 || assetRef.Length > 1024)) throw new ArgumentException("Invalid asset reference.");
        if (bodyCiphertext is not null && bodyCiphertext.Length == 0) throw new ArgumentException("Encrypted body cannot be empty.");
    }

    private static DbValue Uuid(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
    private static DbValue TextOrNull(string? value) => value is string text ? DbValue.Text(text) : DbValue.Null(NpgsqlDbType.Text);
    private static DbValue Binary(byte[]? value) => value is byte[] bytes ? DbValue.Binary(bytes) : DbValue.Null(NpgsqlDbType.Bytea);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny(DbSession session)
    {
        session.RejectApplication();
        throw new UnauthorizedAccessException("Support ticket or message is unavailable.");
    }

    private sealed record Reply(Guid Ticket, Guid? Admin, string? Asset, byte[]? Body, string? Key);
    private sealed record Ticket(Guid Id, Guid UserId, Guid BrandId, string Category, string Status, Guid? OrderId, Guid? AssignedAdminId);
}
