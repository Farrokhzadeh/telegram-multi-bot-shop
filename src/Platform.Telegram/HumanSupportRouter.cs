using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public sealed record HumanSupportRouteResult(Guid TicketId, bool Created, bool Busy, long FlowRevision = 0)
{
    public override string ToString() => "HumanSupportRouteResult [redacted]";
}

public sealed class HumanSupportRouter(DbSessionFactory sessions)
{
    public Task<HumanSupportRouteResult> RouteAsync(VerifiedTelegramUpdate update, CancellationToken ct = default) => RouteCoreAsync(update, null, ct);
    public Task<HumanSupportRouteResult> RouteForBrandAsync(VerifiedTelegramUpdate update, long expectedRevision, CancellationToken ct = default) => RouteCoreAsync(update, expectedRevision, ct);
    private async Task<HumanSupportRouteResult> RouteCoreAsync(VerifiedTelegramUpdate update, long? writerRevision, CancellationToken ct)
    {
        if (writerRevision is < 0) throw new ArgumentOutOfRangeException(nameof(writerRevision));
        ArgumentNullException.ThrowIfNull(update);
        writerRevision ??= sessions.ExpectedWriterRevision(update.Context);
        bool command = TelegramWebhookVerifier.IsSupportCommand(update.Value);
        if (update.IsCallback || (update.Value.StartsWith('/') && !command)) throw new TelegramIngressRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        if (!await BotConsumerOwnership.TryAcquireAsync(session, update.Context, ct)) return new(Guid.Empty, false, true);
        BrandUser member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, null, ct);
        await session.RunEfStepAsync(false, (data, token) => data.LockUserAsync(member.Id, update.Context.BrandId, token), ct);
        member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, member.Id, ct);
        if (member.Banned || member.Blocked) throw new TelegramIngressRejectedException();
        if (writerRevision is long revision) await BrandWriterStore.RequireAsync(session, update.Context, revision, ct);
        bool fresh = await TelegramReplayStore.TryClaimAsync(session, update.Context, update.UpdateId, ct);
        bool enabled = await session.RunEfStepAsync(false, async (data, token) => { await data.LockBrandAsync(update.Context.BrandId, token, true); return await data.Data.Set<Brand>().Where(b => b.Id == update.Context.BrandId && b.BotId == update.Context.BotId && b.Active).Select(b => b.SupportEnabled && data.Data.Set<Admin>().Any(a => a.Id == b.CustomerSupportAdminId && a.DisabledAt == null && a.TelegramUserId != null && a.PublicContactHandle != null)).SingleAsync(token); }, ct);
        if (!enabled) throw new TelegramIngressRejectedException();
        Guid? replay = await session.RunEfStepAsync(false, (data, token) => (from m in data.Data.Set<SupportMessage>() join t in data.Data.Set<SupportTicket>() on m.TicketId equals t.Id where t.UserId == member.Id && m.SenderKind == "customer" && m.TelegramMessageId == update.MessageId select (Guid?)t.Id).SingleOrDefaultAsync(token), ct);
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
            await SupportStore.RecordCustomerMessageAsync(session, update.Context, update.ActorId, ticketId, update.AssetRef, plain, ct, messageId, update.MessageId);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await session.RunEfStepAsync(true, async (data, token) => { await data.RequireMembershipAsync(update.Context, update.ActorId, false, false, token); string key = "telegram:" + update.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture); if (!await data.Data.Set<BotActivityEvent>().AnyAsync(row => row.UserId == member.Id && row.IdempotencyKey == key, token)) data.Data.Add(new BotActivityEvent { Id = Guid.NewGuid(), UserId = member.Id, EventType = "support", SourceKind = command ? "command" : "flow", ActionCode = command ? "support_open" : "support_reply", SupportTicketId = ticketId, FlowRevision = member.Flow.Revision, TelegramUpdateId = update.UpdateId, OutcomeCode = "accepted", IdempotencyKey = key }); return true; }, ct);
        await session.CommitAsync(ct);
        return new(ticketId, created, false, member.Flow.Revision);
    }
}
