using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed record HumanSupportRouteResult(Guid TicketId, bool Created, bool Busy, long FlowRevision = 0)
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
        await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(member.Id)), r => r.GetGuid(0), ct);
        member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, member.Id, ct);
        if (member.Banned || member.Blocked) throw new TelegramIngressRejectedException();
        bool fresh = await TelegramReplayStore.TryClaimAsync(session, update.Context, update.UpdateId, ct);
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
        if (replay is Guid prior)
        { await session.CommitAsync(ct); return new(prior, false, false, member.Flow.Revision); }
        if (!fresh) return new(Guid.Empty, false, false, member.Flow.Revision);
        if (!command && member.Flow.State != CustomerFlowState.Support) throw new TelegramIngressRejectedException();
        Guid? order = command ? (update.Value == "/support" ? null : Guid.ParseExact(update.Value[9..], "D")) : member.Flow.OrderId;
        var (ticketId, created) = await SupportStore.OpenTicketAsync(session, update.Context, update.ActorId, order, ct);
        BrandUser previous = member;
        if (command) member = await BrandUserStore.TransitionAsync(session, update.Context, update.ActorId, member.Id, member.Flow.Revision, CustomerFlowState.Support, orderId: order, ct: ct);
        if (command) await BotActivityWriter.FlowLeftAsync(session, update.Context, update.ActorId, previous, member, ct);
        Guid messageId = Guid.NewGuid();
        byte[] plain = Encoding.UTF8.GetBytes(update.Value);
        try
        {
            string keyId = secrets.CurrentBrandKeyId(update.Context);
            byte[] encrypted = new BrandCipher(secrets).Encrypt(update.Context, "support_messages", messageId, "body_ciphertext", plain);
            await SupportStore.RecordCustomerMessageAsync(session, update.Context, update.ActorId, ticketId, update.AssetRef, encrypted, keyId, ct, messageId, update.MessageId);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,support_ticket_id,flow_revision,telegram_update_id,outcome_code,idempotency_key)
            VALUES (gen_random_uuid(),$1,'support',$2,$3,$4,$5,$6,'accepted',$7) ON CONFLICT (user_id,idempotency_key) DO NOTHING
            """, DbValue.Uuid(member.Id), DbValue.Text(command ? "command" : "flow"), DbValue.Text(command ? "support_open" : "support_reply"),
            DbValue.Uuid(ticketId), DbValue.Bigint(member.Flow.Revision), DbValue.Bigint(update.UpdateId), DbValue.Text("telegram:" + update.UpdateId.ToString(CultureInfo.InvariantCulture))), ct);
        await session.CommitAsync(ct);
        return new(ticketId, created, false, member.Flow.Revision);
    }
}
