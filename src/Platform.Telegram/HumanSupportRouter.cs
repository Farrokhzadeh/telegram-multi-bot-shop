using System.Globalization;
using Platform.Persistence;

namespace Platform.Telegram;

public sealed record HumanSupportRouteResult(Guid TicketId, bool Created, bool Busy)
{
    public override string ToString() => "HumanSupportRouteResult [redacted]";
}

/// <summary>Verified-ingress routing for human support. AI-independent; no order/queue/coupon/money mutation.</summary>
public sealed class HumanSupportRouter
{
    private readonly DbSessionFactory sessions;
    public HumanSupportRouter(DbSessionFactory sessions) { this.sessions = sessions; }

    public async Task<HumanSupportRouteResult> RouteAsync(VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsCallback) throw new TelegramIngressRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        if (!await BotConsumerOwnership.TryAcquireAsync(session, update.Context, ct)) return new(Guid.Empty, false, true);
        BrandUser member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, null, ct);
        if (member.Banned || member.Blocked) throw new TelegramIngressRejectedException();
        var (ticketId, created) = await SupportStore.OpenTicketAsync(session, update.Context, update.ActorId, null, ct);
        if (created)
            await SupportStore.RecordCustomerMessageAsync(session, update.Context, update.ActorId, ticketId,
                assetRef: "support-touch-" + update.UpdateId.ToString(CultureInfo.InvariantCulture), ct: ct);
        await session.CommitAsync(ct);
        return new(ticketId, created, false);
    }
}
