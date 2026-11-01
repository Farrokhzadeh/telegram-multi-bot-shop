using System.Globalization;
using NpgsqlTypes;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public enum UpdateRouteCode { Started, Menu, Support, Discounts, Product, Order, Subscribed, Unsubscribed, Duplicate, Busy, StaffProofObserved }
public sealed record UpdateRouteResult(UpdateRouteCode Code, long FlowRevision, Guid? EntityId = null);
public sealed class UpdateRouter
{
    private readonly DbSessionFactory sessions;
    private readonly HumanSupportRouter? support;
    private readonly Func<BrandContext, Guid, CancellationToken, Task<RestockObservation>> observeRestock;
    public UpdateRouter(DbSessionFactory sessions, Func<BrandContext, Guid, CancellationToken, Task<RestockObservation>> observeRestock, HumanSupportRouter? support = null)
    { this.sessions = sessions; this.observeRestock = observeRestock; this.support = support; }

    public async Task<UpdateRouteResult> RouteAsync(VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (support is not null && !update.IsCallback && (TelegramWebhookVerifier.IsSupportCommand(update.Value) || !update.Value.StartsWith('/')))
        {
            HumanSupportRouteResult result = await support.RouteAsync(update, ct);
            return new(result.Busy ? UpdateRouteCode.Busy : UpdateRouteCode.Support, 0, result.TicketId);
        }
        Callback? callback = update.IsCallback ? ParseCallback(update.Value) : null;
        RestockObservation? observation = null;
        if (callback?.Action == 's')
        {
            await using (var preflight = await sessions.BeginAsync(ct))
            {
                BrandUser actor = await BrandUserStore.RequireAsync(preflight, update.Context, update.ActorId, null, ct);
                if (actor.Blocked || actor.Banned || actor.Flow.Revision != callback.Revision || actor.Flow.AnchorMessageId != update.MessageId ||
                    actor.Flow.ProductId != callback.Target || !await Published(preflight, update.Context, callback.Target, ct)) throw new TelegramIngressRejectedException();
            }
            observation = await observeRestock(update.Context, callback.Target, ct);
        }
        if (observation is not null && observation.ProductId != callback!.Target) throw new TelegramIngressRejectedException();
        await using var session = await sessions.BeginAsync(ct);
        if (!await BotConsumerOwnership.TryAcquireAsync(session, update.Context, ct)) return new(UpdateRouteCode.Busy, 0);
        if (!update.IsCallback && update.Value.StartsWith("/bind ", StringComparison.Ordinal))
        {
            string[] binding = update.Value.Split(' ');
            if (binding.Length != 3 || !Guid.TryParseExact(binding[1], "N", out Guid adminId) || adminId == Guid.Empty || adminId.ToString("N") != binding[1]) throw new TelegramIngressRejectedException();
            using var nonce = AdminToken.Parse(binding[2]);
            var proof = PrivateTelegramProof.FromVerifiedIngress(update.Context, update.ActorId, update.ActorId, false, nonce);
            if (!await AdminBindingStore.ObservePrivateProofAsync(session, adminId, nonce, proof, ct)) throw new TelegramIngressRejectedException();
            await session.CommitAsync(ct);
            return new(UpdateRouteCode.StaffProofObserved, 0);
        }
        BrandUser member = update.IsCallback
            ? await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, null, ct)
            : await BrandUserStore.ObservePrivateAsync(session, update.Context, update.ActorId, ct);
        if (member.Banned || (member.Blocked && update.Value != "/start")) throw new TelegramIngressRejectedException();
        if (member.Blocked && update.Value == "/start")
            await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=NULL WHERE id=$1 AND brand_id=$2 AND telegram_id=$3",
                DbValue.Uuid(member.Id), DbValue.Uuid(update.Context.BrandId), DbValue.Bigint(update.ActorId)), ct);
        if (callback is not null)
        {
            if (member.Flow.Revision != callback.Revision || member.Flow.AnchorMessageId != update.MessageId) throw new TelegramIngressRejectedException();
            if (callback.Action is 's' or 'u' && member.Flow.ProductId != callback.Target) throw new TelegramIngressRejectedException();
        }
        string key = "telegram:" + update.UpdateId.ToString(CultureInfo.InvariantCulture);
        var action = Action(update, callback);
        int recorded = await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,flow_revision,telegram_update_id,outcome_code,idempotency_key)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,'accepted',$10) ON CONFLICT (user_id,idempotency_key) DO NOTHING
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(member.Id), DbValue.Text(action.Event), DbValue.Text(update.IsCallback ? "callback" : "command"),
            DbValue.Text(action.Action), callback?.Action is 'p' or 's' or 'u' ? DbValue.Uuid(callback.Target) : DbValue.Null(NpgsqlDbType.Uuid),
            callback?.Action == 'o' ? DbValue.Uuid(callback.Target) : DbValue.Null(NpgsqlDbType.Uuid), DbValue.Bigint(member.Flow.Revision), DbValue.Bigint(update.UpdateId), DbValue.Text(key)), ct);
        if (recorded == 0) return new(UpdateRouteCode.Duplicate, member.Flow.Revision);
        Guid? entity = null;
        if (callback is null)
        {
            var state = update.Value switch
            {
                "/support" => CustomerFlowState.Support,
                "/discounts" => CustomerFlowState.Discounts,
                _ => CustomerFlowState.Catalog
            };
            member = await BrandUserStore.TransitionAsync(session, update.Context, update.ActorId, member.Id, member.Flow.Revision, state, ct: ct);
        }
        else
        {
            if (callback.Action == 'p')
            {
                bool published = await Published(session, update.Context, callback.Target, ct);
                if (!published) throw new TelegramIngressRejectedException();
            }
            member = await BrandUserStore.TransitionAsync(session, update.Context, update.ActorId, member.Id, member.Flow.Revision,
                callback.Action == 'o' ? CustomerFlowState.Payment : CustomerFlowState.Quantity,
                orderId: callback.Action == 'o' ? callback.Target : null, productId: callback.Action == 'o' ? null : callback.Target,
                anchorMessageId: update.MessageId, expiresAt: member.Flow.ExpiresAt, ct: ct);
            entity = callback.Action switch
            {
                's' => await StockSubscriptionStore.SubscribeAsync(session, update.Context, update.ActorId, member.Id, observation!, ct),
                'u' => await StockSubscriptionStore.UnsubscribeAsync(session, update.Context, update.ActorId, member.Id, callback.Target, ct),
                _ => callback.Target
            };
        }
        await session.CommitAsync(ct);
        return new(action.Code, member.Flow.Revision, entity);
    }

    private static Callback ParseCallback(string value)
    {
        string[] parts = value.Split('|');
        if (parts.Length != 4 || parts[0] != "v1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 0 ||
            revision.ToString(CultureInfo.InvariantCulture) != parts[1] || parts[2].Length != 1 || parts[2][0] is not ('p' or 'o' or 's' or 'u') ||
            !Guid.TryParseExact(parts[3], "N", out Guid target) || target == Guid.Empty || target.ToString("N") != parts[3]) throw new TelegramIngressRejectedException();
        return new(revision, parts[2][0], target);
    }
    private static Task<bool> Published(DbSession session, BrandContext context, Guid productId, CancellationToken ct) =>
        session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brand_products b JOIN shop.products p ON p.id=b.product_id WHERE b.brand_id=$1 AND p.id=$2 AND b.visible AND p.active)",
            DbValue.Uuid(context.BrandId), DbValue.Uuid(productId)), row => row.GetBoolean(0), ct);
    private static (string Event, string Action, UpdateRouteCode Code) Action(VerifiedTelegramUpdate update, Callback? callback) => callback?.Action switch
    {
        'p' => ("navigation", "product_open", UpdateRouteCode.Product),
        'o' => ("checkout", "checkout_open", UpdateRouteCode.Order),
        's' => ("subscription", "subscribe", UpdateRouteCode.Subscribed),
        'u' => ("subscription", "unsubscribe", UpdateRouteCode.Unsubscribed),
        _ => update.Value switch
        {
            "/start" => ("navigation", "start", UpdateRouteCode.Started),
            "/menu" => ("navigation", "catalog_open", UpdateRouteCode.Menu),
            "/support" => ("support", "support_open", UpdateRouteCode.Support),
            "/discounts" => ("discount", "discounts_open", UpdateRouteCode.Discounts),
            _ => throw new TelegramIngressRejectedException()
        }
    };
    private sealed record Callback(long Revision, char Action, Guid Target);
}
