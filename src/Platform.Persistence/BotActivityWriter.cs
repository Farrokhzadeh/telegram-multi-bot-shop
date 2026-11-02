using NpgsqlTypes;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

internal static class BotActivityWriter
{
    internal static async Task CheckoutAsync(DbSession session, BrandContext context, long actor, Guid order, CancellationToken ct)
    {
        var original = await session.QuerySingleAsync(new DbStatement("SELECT o.user_id,o.product_id,o.quantity,o.created_at FROM shop.orders o JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE o.id=$1 AND u.brand_id=$2 AND u.telegram_id=$3 AND b.bot_id=$4 AND b.active",
            DbValue.Uuid(order), DbValue.Uuid(context.BrandId), DbValue.Bigint(actor), DbValue.Bigint(context.BotId)), r => (Member: r.GetGuid(0), Product: r.GetGuid(1), Quantity: r.GetInt32(2), At: r.GetUtc(3)), ct);
        await RecordAsync(session, context, actor, original.Member, BotActivityAction.CheckoutCreated, BotActivitySource.Flow, BotActivityOutcome.Accepted,
            new(original.Product, order, Quantity: original.Quantity), order, original.At, ct);
    }
    internal static async Task<bool> RecordAsync(DbSession session, BrandContext context, long actor, Guid member, BotActivityAction action,
        BotActivitySource source, BotActivityOutcome outcome, BotActivityContext links, Guid identity, DateTime originalAt, CancellationToken ct)
    {
        BotActivityRegistry.Validate(action, source, outcome, links);
        if (identity == Guid.Empty || originalAt.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid original activity identity.");
        var owner = await BrandUserStore.RequireAsync(session, context, actor, member, ct);
        if (owner.Blocked || owner.Banned) throw new UnauthorizedAccessException("Activity origin is unavailable.");
        var clock = await session.QuerySingleAsync(new DbStatement("SELECT clock_timestamp(),CURRENT_TIMESTAMP"), r => (Now: r.GetUtc(0), AsOf: r.GetUtc(1)), ct);
        if (originalAt > clock.Now) throw new ArgumentException("Activity occurrence is in the future.");
        if (originalAt < clock.AsOf.AddHours(-4320)) return false;
        var definition = BotActivityRegistry.Get(action);
        string key = links.TelegramUpdate is long telegram ? BotActivityRegistry.TelegramIdentity(telegram) : "workflow:" + definition.Code + ":" + identity.ToString("N");
        var values = new[] { DbValue.Uuid(member), DbValue.Text(definition.EventType), DbValue.Text(BotActivityRegistry.Source(source)), DbValue.Text(definition.Code), Id(links.Product), Id(links.Order), Id(links.Ticket),
            links.Quantity is int quantity ? DbValue.Integer(quantity) : DbValue.Null(NpgsqlDbType.Integer), links.FlowRevision is long revision ? DbValue.Bigint(revision) : DbValue.Null(NpgsqlDbType.Bigint),
            links.TelegramUpdate is long update ? DbValue.Bigint(update) : DbValue.Null(NpgsqlDbType.Bigint), DbValue.Text(BotActivityRegistry.Outcome(outcome)), DbValue.Text(key), DbValue.Utc(originalAt) };
        int inserted = await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,support_ticket_id,quantity_selected,flow_revision,telegram_update_id,outcome_code,idempotency_key,occurred_at)
            VALUES (gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) ON CONFLICT(user_id,idempotency_key) DO NOTHING
            """, values), ct);
        if (inserted == 0)
        {
            bool same = await session.QuerySingleAsync(new DbStatement("""
                SELECT EXISTS(SELECT 1 FROM shop.bot_activity_events WHERE user_id=$1 AND idempotency_key=$12 AND
                    ROW(event_type,source_kind,action_code,product_id,order_id,support_ticket_id,quantity_selected,flow_revision,telegram_update_id,outcome_code,occurred_at)
                    IS NOT DISTINCT FROM ROW($2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$13))
                """, values), r => r.GetBoolean(0), ct);
            if (!same) { session.RejectApplication(); throw new InvalidOperationException("Activity identity content conflicts."); }
        }
        return inserted != 0;
    }
    private static DbValue Id(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlDbType.Uuid);
}
