using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;
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
            bool sameMember = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Order>().AnyAsync(row => row.Id == order && row.UserId == member.Id, token), ct);
            if (!sameMember) Deny(session);
            bool paidPreorder = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Order>().AnyAsync(row => row.Id == order && row.IsPreorder && row.PaymentStatus == "paid", token), ct);
            category = paidPreorder ? "preorder_cancellation" : "order";
        }
        else category = "general";
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)",
            DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(
                    "support-ticket:" + member.Id.ToString("D") + ":" + category + ":" + (orderId?.ToString("D") ?? "null")))))), ct);
        Guid? existing = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupportTicket>().Where(row => row.UserId == member.Id && row.Category == category && row.OrderId == orderId && row.Status != "closed").Select(row => (Guid?)row.Id).SingleOrDefaultAsync(token), ct);
        if (existing.HasValue) return (existing.Value, false);
        Guid id = Guid.NewGuid();
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireBrandCommandAsync(brand.BrandId, token); data.Data.Add(new Entities.SupportTicket { Id = id, UserId = member.Id, OrderId = orderId, Category = category, Source = "customer" }); return true; }, ct);
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
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireBrandCommandAsync(brand.BrandId, token); data.Data.Add(new Entities.SupportMessage { Id = id, TicketId = ticketId, SenderKind = "customer", SenderTelegramId = verifiedActorId, AssetRef = assetRef, BodyCiphertext = bodyCiphertext, BodyKeyId = bodyKeyId, TelegramMessageId = telegramMessageId }); return true; }, ct);
        await ChangeTicketAsync(session, t, row => true, (row, now) => { row.Status = row.AssignedAdminId == null ? "open" : "assigned"; row.UpdatedAt = now; }, ct);
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
            await ChangeTicketAsync(session, t, row => row.Status != "closed" && (row.AssignedAdminId == null || row.AssignedAdminId == actor.AdminId), (row, now) => { row.Status = "assigned"; row.AssignedAdminId = actor.AdminId; row.UpdatedAt = now; }, ct);
            await AuditAsync(session, actor, t, "support_assign", ct, AdminAuditStore.ReasonCode(reason));
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
        Reply? prior = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.SupportMessage>().Where(row => row.Id == messageId).Select(row => new Reply(row.TicketId, row.SenderAdminId, row.AssetRef, row.BodyCiphertext, row.BodyKeyId)).SingleOrDefaultAsync(token), ct);
        if (prior is not null)
        {
            if (prior.Ticket != ticketId || prior.Admin != actor.AdminId || prior.Asset != assetRef || prior.Key != bodyKeyId ||
                (prior.Body is null) != (bodyCiphertext is null) || (prior.Body is not null && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(prior.Body, bodyCiphertext!))) Deny(session);
            return messageId;
        }
        if (t.Status == "closed") Deny(session);
        ValidateContent(assetRef, bodyCiphertext, bodyKeyId);
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireAdminAsync(actor, null, token); data.Data.Add(new Entities.SupportMessage { Id = messageId, TicketId = ticketId, SenderKind = "admin", SenderAdminId = actor.AdminId, AssetRef = assetRef, BodyCiphertext = bodyCiphertext, BodyKeyId = bodyKeyId }); return true; }, ct);
        await ChangeTicketAsync(session, t, row => row.Status != "closed", (row, now) => { row.Status = "waiting_customer"; row.AssignedAdminId = actor.AdminId; row.UpdatedAt = now; }, ct);
        await AuditAsync(session, actor, t, "update", ct);
        await EnqueueCustomerNoticeAsync(session, t, messageId, ct);
        return messageId;
    }

    public static async Task CloseTicketAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.Status == "closed") return;
        await ChangeTicketAsync(session, t, row => row.Status != "closed", (row, now) => { row.Status = "closed"; row.ClosedAt = now; row.UpdatedAt = now; }, ct);
        await AuditAsync(session, actor, t, "update", ct);
    }

    public static async Task ReopenTicketAsync(DbSession session, AuthenticatedAdmin actor, Guid ticketId, CancellationToken ct = default)
    {
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        Ticket t = await RequireTicketAsync(session, ticketId, ct);
        if (t.Status != "closed") return;
        await ChangeTicketAsync(session, t, row => row.Status == "closed", (row, now) => { row.Status = "open"; row.ClosedAt = null; row.UpdatedAt = now; }, ct);
        await AuditAsync(session, actor, t, "update", ct);
    }

    private static async Task EnqueueCustomerNoticeAsync(DbSession session, Ticket t, Guid messageId, CancellationToken ct)
    {
        Guid deliveryId = Guid.NewGuid();
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireBrandCommandAsync(t.BrandId, token); data.Data.Add(new Entities.TelegramDelivery { Id = deliveryId, BrandId = t.BrandId, RecipientKind = "customer_dm", UserId = t.UserId, Purpose = "support_notice", Action = "send", DedupeKey = "support:" + messageId.ToString("D"), SupportMessageId = messageId }); return true; }, ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(t.BrandId, deliveryId), messageId, DateTime.UtcNow, ct);
    }

    private static Task<Ticket?> ReadAsync(DbSession session, Guid ticketId, CancellationToken ct) => session.RunEfStepAsync(false, async (data, token) => { await data.LockSupportTicketAsync(ticketId, token); return await (from t in data.Data.Set<Entities.SupportTicket>() join u in data.Data.Set<Entities.User>() on t.UserId equals u.Id where t.Id == ticketId select new Ticket(t.Id, t.UserId, u.BrandId, t.Category, t.Status, t.OrderId, t.AssignedAdminId)).SingleOrDefaultAsync(token); }, ct);

    private static async Task<Ticket> RequireTicketAsync(DbSession session, Guid ticketId, CancellationToken ct)
    {
        if (session.IsWriterScoped)
        {
            var owner = await session.RunEfStepAsync(false, (data, token) => (from t in data.Data.Set<Entities.SupportTicket>() join u in data.Data.Set<Entities.User>() on t.UserId equals u.Id where t.Id == ticketId select new { User = u.Id, Brand = u.BrandId }).SingleAsync(token), ct);
            await session.RunEfStepAsync(false, (data, token) => data.LockUserAsync(owner.User, owner.Brand, token), ct);
            await session.AdmitBrandWriterAsync(owner.Brand, ct);
        }
        Ticket? t = await ReadAsync(session, ticketId, ct);
        if (t is null) Deny(session);
        return t!;
    }

    private static async Task<BrandUser> RequireCustomerAsync(DbSession session, BrandContext brand, long actorId, CancellationToken ct)
    {
        await session.RunEfStepAsync(false, (data, token) => data.LockMemberAsync(brand.BrandId, actorId, token), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, brand, actorId, null, ct);
        if (member.Blocked || member.Banned) Deny(session);
        await session.AdmitWriterAsync(brand, ct);
        return member;
    }

    private static Task<int> AuditAsync(DbSession session, AuthenticatedAdmin actor, Ticket ticket, string action, CancellationToken ct, string reason = "operator_review") => session.RunEfStepAsync(true, async (data, token) => { await data.RequireAdminAsync(actor, null, token); data.Data.Add(new Entities.AuditEvent { Id = Guid.NewGuid(), ActorKind = "admin", ActorAdminId = actor.AdminId, BrandId = ticket.BrandId, Action = action, EntityKind = "support_ticket", EntityId = ticket.Id, Reason = reason, CorrelationId = Guid.NewGuid() }); return 1; }, ct);
    private static Task<bool> ChangeTicketAsync(DbSession session, Ticket ticket, Func<Entities.SupportTicket, bool> predicate, Action<Entities.SupportTicket, DateTime> change, CancellationToken ct) => session.RunEfStepAsync(true, async (data, token) => { await data.RequireBrandCommandAsync(ticket.BrandId, token); var row = await data.Data.Set<Entities.SupportTicket>().SingleAsync(row => row.Id == ticket.Id, token); if (!predicate(row)) Deny(session); DateTime now = await data.Data.Set<Entities.SupportTicket>().Where(row => row.Id == ticket.Id).Select(row => EfValues.TransactionTime()).SingleAsync(token); change(row, now); return true; }, ct);

    private static void ValidateContent(string? assetRef, byte[]? bodyCiphertext, string? bodyKeyId)
    {
        bool hasBody = bodyCiphertext is not null;
        if (hasBody != (bodyKeyId is not null)) throw new ArgumentException("Message requires both an encrypted body and its key id.");
        if (!hasBody && assetRef is null) throw new ArgumentException("Message requires an asset reference or an encrypted body.");
        if (assetRef is not null && (assetRef.Length < 1 || assetRef.Length > 1024)) throw new ArgumentException("Invalid asset reference.");
        if (bodyCiphertext is not null && bodyCiphertext.Length == 0) throw new ArgumentException("Encrypted body cannot be empty.");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny(DbSession session)
    {
        session.RejectApplication();
        throw new UnauthorizedAccessException("Support ticket or message is unavailable.");
    }

    private sealed record Reply(Guid Ticket, Guid? Admin, string? Asset, byte[]? Body, string? Key);
    private sealed record Ticket(Guid Id, Guid UserId, Guid BrandId, string Category, string Status, Guid? OrderId, Guid? AssignedAdminId);
}
