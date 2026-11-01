using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed record HumanSupportRouteResult(Guid TicketId, bool Created, bool Busy)
{
    public override string ToString() => "HumanSupportRouteResult [redacted]";
}

public sealed class HumanSupportRouter(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<HumanSupportRouteResult> RouteAsync(VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        bool command = TelegramWebhookVerifier.IsSupportCommand(update.Value);
        if (update.IsCallback || (update.Value.StartsWith('/') && !command)) throw new TelegramIngressRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        if (!await BotConsumerOwnership.TryAcquireAsync(session, update.Context, ct)) return new(Guid.Empty, false, true);
        BrandUser member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, null, ct);
        if (member.Banned || member.Blocked) throw new TelegramIngressRejectedException();
        bool enabled = await session.QuerySingleAsync(new DbStatement("""
            SELECT b.support_enabled AND EXISTS(SELECT 1 FROM shop.admins a WHERE a.id=b.customer_support_admin_id
                AND a.disabled_at IS NULL AND a.telegram_user_id IS NOT NULL AND a.public_contact_handle IS NOT NULL)
            FROM shop.brands b WHERE b.id=$1 AND b.bot_id=$2 AND b.active FOR SHARE OF b
            """, DbValue.Uuid(update.Context.BrandId), DbValue.Bigint(update.Context.BotId)), row => row.GetBoolean(0), ct);
        if (!enabled) throw new TelegramIngressRejectedException();
        Guid? replay = await session.QuerySingleAsync(new DbStatement("""
            SELECT t.id FROM (SELECT 1) seed LEFT JOIN (SELECT t.id FROM shop.support_messages m JOIN shop.support_tickets t ON t.id=m.ticket_id
                WHERE t.user_id=$1 AND m.sender_kind='customer' AND m.telegram_message_id=$2) t ON true
            """, DbValue.Uuid(member.Id), DbValue.Bigint(update.MessageId)), row => row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0), ct);
        if (replay is Guid prior) return new(prior, false, false);
        if (!command && member.Flow.State != CustomerFlowState.Support) throw new TelegramIngressRejectedException();
        Guid? order = command ? (update.Value == "/support" ? null : Guid.ParseExact(update.Value[9..], "D")) : member.Flow.OrderId;
        var (ticketId, created) = await SupportStore.OpenTicketAsync(session, update.Context, update.ActorId, order, ct);
        if (command) await BrandUserStore.TransitionAsync(session, update.Context, update.ActorId, member.Id, member.Flow.Revision, CustomerFlowState.Support, orderId: order, ct: ct);
        Guid messageId = Guid.NewGuid();
        byte[] plain = Encoding.UTF8.GetBytes(update.Value);
        try
        {
            string keyId = secrets.CurrentBrandKeyId(update.Context);
            byte[] encrypted = new BrandCipher(secrets).Encrypt(update.Context, "support_messages", messageId, "body_ciphertext", plain);
            await SupportStore.RecordCustomerMessageAsync(session, update.Context, update.ActorId, ticketId, null, encrypted, keyId, ct, messageId, update.MessageId);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await session.CommitAsync(ct);
        return new(ticketId, created, false);
    }
}
