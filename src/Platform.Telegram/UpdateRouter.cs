using System.Globalization;
using NpgsqlTypes;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Telegram;

public enum UpdateRouteCode { Started, Menu, Support, Discounts, Product, Order, Subscribed, Unsubscribed, Duplicate, Busy, StaffProofObserved, Quantity, QuantityRejected, History }
public sealed record UpdateRouteResult(UpdateRouteCode Code, long FlowRevision, Guid? EntityId = null, int? Quantity = null, CustomerHistoricalPage? History = null);
public sealed class UpdateRouter
{
    private readonly DbSessionFactory sessions;
    private readonly HumanSupportRouter? support;
    private readonly CustomerHistoryCommandRouter? history;
    private readonly Func<BrandContext, Guid, CancellationToken, Task<RestockObservation>> observeRestock;
    public UpdateRouter(DbSessionFactory sessions, Func<BrandContext, Guid, CancellationToken, Task<RestockObservation>> observeRestock, HumanSupportRouter? support = null, CustomerHistoryCommandRouter? history = null)
    { this.sessions = sessions; this.observeRestock = observeRestock; this.support = support; this.history = history; }

    public async Task<UpdateRouteResult> RouteAsync(VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.IsCallback && CustomerHistoryCommandRouter.TryParsePage(update.Value, out _))
        {
            if (history is null) throw new TelegramIngressRejectedException();
            return new(UpdateRouteCode.History, 0, History: await history.ReadAsync(update, ct));
        }
        if (!update.IsCallback && !update.Value.StartsWith('/') && update.AssetRef is null)
        {
            BrandUser current;
            await using (var read = await sessions.BeginAsync(ct)) current = await BrandUserStore.RequireAsync(read, update.Context, update.ActorId, null, ct);
            if (current.Flow.State == CustomerFlowState.Quantity || (current.Flow.State != CustomerFlowState.Support && ParseQuantity(update.Value) is not null))
                return await QuantityAsync(update, current, ct);
        }
        if (support is not null && !update.IsCallback && (TelegramWebhookVerifier.IsSupportCommand(update.Value) || !update.Value.StartsWith('/')))
        {
            HumanSupportRouteResult result = await support.RouteAsync(update, ct);
            return new(result.Busy ? UpdateRouteCode.Busy : UpdateRouteCode.Support, result.FlowRevision, result.TicketId);
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
        await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(member.Id)), r => r.GetGuid(0), ct);
        member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, member.Id, ct);
        if (member.Banned || (member.Blocked && update.Value != "/start")) throw new TelegramIngressRejectedException();
        if (!await TelegramReplayStore.TryClaimAsync(session, update.Context, update.UpdateId, ct)) return new(UpdateRouteCode.Duplicate, member.Flow.Revision);
        if (member.Blocked && update.Value == "/start")
            await session.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=NULL WHERE id=$1 AND brand_id=$2 AND telegram_id=$3",
                DbValue.Uuid(member.Id), DbValue.Uuid(update.Context.BrandId), DbValue.Bigint(update.ActorId)), ct);
        if (callback is not null)
        {
            if (member.Flow.Revision != callback.Revision || member.Flow.AnchorMessageId != update.MessageId) throw new TelegramIngressRejectedException();
            if (callback.Action is 's' or 'u' && member.Flow.ProductId != callback.Target) throw new TelegramIngressRejectedException();
        }
        if (callback?.Action == 'p' && !await Published(session, update.Context, callback.Target, ct)) throw new TelegramIngressRejectedException();
        string key = BotActivityRegistry.TelegramIdentity(update.UpdateId);
        var action = Action(update, callback);
        var definition = BotActivityRegistry.FromCode(action.Action);
        Guid? activityProduct = callback?.Action is 'p' or 's' or 'u' ? callback.Target : null;
        if (callback?.Action == 'o')
        {
            activityProduct = await session.QuerySingleAsync(new DbStatement("SELECT product_id FROM (SELECT 1) seed LEFT JOIN shop.orders ON id=$1 AND user_id=$2", DbValue.Uuid(callback.Target), DbValue.Uuid(member.Id)), r => r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), ct);
            if (activityProduct is null) throw new TelegramIngressRejectedException();
        }
        BotActivityRegistry.Validate(definition.Action, update.IsCallback ? BotActivitySource.Callback : BotActivitySource.Command, BotActivityOutcome.Accepted,
            new(activityProduct, callback?.Action == 'o' ? callback.Target : null, FlowRevision: member.Flow.Revision, TelegramUpdate: update.UpdateId));
        int recorded = await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,flow_revision,telegram_update_id,outcome_code,idempotency_key)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,'accepted',$10) ON CONFLICT (user_id,idempotency_key) DO NOTHING
            """, DbValue.Uuid(Guid.NewGuid()), DbValue.Uuid(member.Id), DbValue.Text(definition.EventType), DbValue.Text(BotActivityRegistry.Source(update.IsCallback ? BotActivitySource.Callback : BotActivitySource.Command)),
            DbValue.Text(action.Action), activityProduct is Guid observedProduct ? DbValue.Uuid(observedProduct) : DbValue.Null(NpgsqlDbType.Uuid),
            callback?.Action == 'o' ? DbValue.Uuid(callback.Target) : DbValue.Null(NpgsqlDbType.Uuid), DbValue.Bigint(member.Flow.Revision), DbValue.Bigint(update.UpdateId), DbValue.Text(key)), ct);
        if (recorded == 0)
        { await session.CommitAsync(ct); return new(UpdateRouteCode.Duplicate, member.Flow.Revision); }
        BrandUser previous = member;
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
        await BotActivityWriter.FlowLeftAsync(session, update.Context, update.ActorId, previous, member, ct);
        await session.CommitAsync(ct);
        return new(action.Code, member.Flow.Revision, entity);
    }

    private async Task<UpdateRouteResult> QuantityAsync(VerifiedTelegramUpdate update, BrandUser expected, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct);
        if (!await BotConsumerOwnership.TryAcquireAsync(session, update.Context, ct)) return new(UpdateRouteCode.Busy, expected.Flow.Revision);
        await session.QuerySingleAsync(new DbStatement("SELECT id FROM shop.users WHERE id=$1 FOR UPDATE", DbValue.Uuid(expected.Id)), r => r.GetGuid(0), ct);
        BrandUser member = await BrandUserStore.RequireAsync(session, update.Context, update.ActorId, expected.Id, ct);
        if (member.Blocked || member.Banned) throw new TelegramIngressRejectedException();
        if (!await TelegramReplayStore.TryClaimAsync(session, update.Context, update.UpdateId, ct)) return new(UpdateRouteCode.Duplicate, member.Flow.Revision);
        long highwater = await session.QuerySingleAsync(new DbStatement("SELECT telegram_update_highwater FROM shop.brands WHERE id=$1", DbValue.Uuid(update.Context.BrandId)), r => r.GetInt64(0), ct);
        if (update.UpdateId != highwater) throw new TelegramIngressRejectedException();
        if (member.Flow.Revision != expected.Flow.Revision || member.Flow.State != CustomerFlowState.Quantity || member.Flow.ProductId is not Guid product ||
            member.Flow.AnchorMessageId is not long anchor || update.MessageId <= anchor) throw new TelegramIngressRejectedException();
        var listing = await session.QuerySingleAsync(new DbStatement("""
            SELECT l.min_quantity,l.max_quantity,clock_timestamp() FROM (SELECT 1) seed LEFT JOIN
                (SELECT l.min_quantity,l.max_quantity FROM shop.brand_products l JOIN shop.products p ON p.id=l.product_id
                WHERE l.brand_id=$1 AND l.product_id=$2 AND l.visible AND p.active FOR SHARE OF l,p) l ON true
            """, DbValue.Uuid(update.Context.BrandId), DbValue.Uuid(product)), r => (Min: r.IsDBNull(0) ? 0 : r.GetInt32(0), Max: r.IsDBNull(1) ? 0 : r.GetInt32(1), Now: r.GetUtc(2)), ct);
        if (listing.Min == 0) throw new TelegramIngressRejectedException();
        if (member.Flow.ExpiresAt is DateTime expiry && expiry <= listing.Now) throw new TelegramIngressRejectedException();
        int? quantity = ParseQuantity(update.Value);
        bool accepted = quantity >= listing.Min && quantity <= Math.Min(10000, listing.Max);
        await BotActivityWriter.RecordAsync(session, update.Context, update.ActorId, member.Id,
            accepted ? BotActivityAction.QuantityEnter : BotActivityAction.QuantityRejected, BotActivitySource.Flow,
            accepted ? BotActivityOutcome.Accepted : BotActivityOutcome.Rejected,
            new(product, Quantity: accepted ? quantity : null, FlowRevision: member.Flow.Revision, TelegramUpdate: update.UpdateId),
            Guid.NewGuid(), listing.Now, ct);
        if (accepted) member = await BrandUserStore.TransitionAsync(session, update.Context, update.ActorId, member.Id, member.Flow.Revision,
            CustomerFlowState.Payment, productId: product, anchorMessageId: anchor, expiresAt: member.Flow.ExpiresAt, ct: ct);
        await session.CommitAsync(ct);
        return new(accepted ? UpdateRouteCode.Quantity : UpdateRouteCode.QuantityRejected, member.Flow.Revision, product, accepted ? quantity : null);
    }
    private static int? ParseQuantity(string value)
    {
        if (value.Length is < 1 or > 5) return null;
        Span<char> digits = stackalloc char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            digits[i] = c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') : c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c;
            if (!char.IsAsciiDigit(digits[i])) return null;
        }
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int quantity) && quantity is > 0 and <= 10000 ? quantity : null;
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
