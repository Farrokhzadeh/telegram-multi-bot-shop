using System.Security.Cryptography;
using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class BotActivityQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 0, ct: ct);
        var payments = new PaymentSettlementFixture(f); await payments.EnableAsync(ct);
        var own = await payments.OrderAsync(ct: ct); var foreign = await payments.OrderAsync(user: 1, ct: ct);
        Guid ticket;
        await using (var setup = await f.Sessions.BeginAsync(ct))
        {
            ticket = (await SupportStore.OpenTicketAsync(setup, f.Context(0), CheckoutFixture.Actor(0), null, ct)).Id;
            await BrandUserStore.TransitionAsync(setup, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], 0, CustomerFlowState.Payment, orderId: own.Order, anchorMessageId: 777, expiresAt: DateTime.UtcNow.AddHours(1), ct: ct);
            await setup.CommitAsync(ct);
        }
        byte[] secret = SHA256.HashData("public synthetic activity registry"u8);
        using var verifier = new TelegramWebhookVerifier(f.Context(0), secret);
        var update = verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body("v1|1|o|" + own.Order.ToString("N"), callback: true, updateId: 900, messageId: 777));
        var result = await new UpdateRouter(f.Sessions, (_, _, _) => throw new InvalidOperationException("Unexpected synthetic supplier call.")).RouteAsync(update, ct);
        Require(result.Code == UpdateRouteCode.Order && result.EntityId == own.Order, "real checkout callback derives its same-member product");
        await using (var read = await f.Sessions.BeginAsync(ct))
        {
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE order_id=$1 AND product_id=$2 AND user_id=$3 AND action_code='checkout_open'", DbValue.Uuid(own.Order), DbValue.Uuid(f.Product), DbValue.Uuid(f.Users[0])), r => r.GetInt64(0), ct) == 1, "checked order/product event context");
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM information_schema.columns WHERE table_schema='shop' AND table_name='bot_activity_events'"), r => r.GetInt64(0), ct) == 14, "only canonical typed event fields");
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM pg_indexes WHERE schemaname='shop' AND tablename='bot_activity_events' AND (indexname='activity_retention' OR indexdef LIKE '%UNIQUE%user_id, idempotency_key%')"), r => r.GetInt64(0), ct) == 2, "retention and membership replay indexes");
        }
        string before = await Domain(f, ct);
        foreach (var action in Enum.GetValues<BotActivityAction>())
        {
            var definition = BotActivityRegistry.Get(action);
            var context = new BotActivityContext(definition.ProductRequired ? f.Product : null, action == BotActivityAction.CheckoutOpen ? own.Order : null,
                definition.TicketRequired ? ticket : null, definition.QuantityRequired ? 2 : null, 2, 1000 + (int)action);
            var source = action is BotActivityAction.QuantityEnter or BotActivityAction.SupportReply ? BotActivitySource.Flow : action == BotActivityAction.PolicyCheck ? BotActivitySource.System : BotActivitySource.Callback;
            await Insert(f, 0, definition, source, context, ct);
        }
        for (int i = 0; i < 100; i++) await Insert(f, 0, BotActivityRegistry.Get(BotActivityAction.Start), BotActivitySource.Command, new(TelegramUpdate: 1000), ct);
        await Insert(f, 1, BotActivityRegistry.Get(BotActivityAction.Start), BotActivitySource.Command, new(TelegramUpdate: 1000), ct);
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 13, "same-member hundred replay dedupe and separate brand membership");
        bool ownership = false;
        try { await Insert(f, 0, BotActivityRegistry.Get(BotActivityAction.CheckoutOpen), BotActivitySource.Callback, new(f.Product, foreign.Order, TelegramUpdate: 9999), ct); }
        catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { ownership = true; }
        Require(ownership, "foreign order cannot enter activity context");
        bool immutable = false;
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.bot_activity_events SET outcome_code='duplicate'"), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { immutable = true; } }
        bool unexpired = false;
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("DELETE FROM shop.bot_activity_events"), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { unexpired = true; } }
        Require(immutable && unexpired && before == await Domain(f, ct), "immutable recent analytics cannot change protected domain facts");
    }
    private static async Task Insert(CheckoutFixture f, int user, BotActivityDefinition definition, BotActivitySource source, BotActivityContext context, CancellationToken ct)
    {
        BotActivityRegistry.Validate(definition.Action, source, BotActivityOutcome.Accepted, context);
        await using var session = await f.Sessions.BeginAsync(ct);
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,support_ticket_id,quantity_selected,flow_revision,telegram_update_id,outcome_code,idempotency_key)
            VALUES (gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT(user_id,idempotency_key) DO NOTHING
            """, DbValue.Uuid(f.Users[user]), DbValue.Text(definition.EventType), DbValue.Text(BotActivityRegistry.Source(source)), DbValue.Text(definition.Code), Id(context.Product), Id(context.Order), Id(context.Ticket),
            context.Quantity is int quantity ? DbValue.Integer(quantity) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Integer), context.FlowRevision is long revision ? DbValue.Bigint(revision) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint),
            context.TelegramUpdate is long update ? DbValue.Bigint(update) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Text(BotActivityRegistry.Outcome(BotActivityOutcome.Accepted)), DbValue.Text(BotActivityRegistry.TelegramIdentity(context.TelegramUpdate!.Value))), ct);
        await session.CommitAsync(ct);
    }
    private static DbValue Id(Guid? value) => value is Guid id ? DbValue.Uuid(id) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Uuid);
    private static async Task<string> Domain(CheckoutFixture f, CancellationToken ct)
    {
        await using var read = await f.Sessions.BeginAsync(ct);
        var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' ORDER BY tablename"), r => r.GetString(0), 28, ct);
        var hashes = new List<string>();
        foreach (string table in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{table} t"), r => r.GetString(0), ct));
        return string.Join(':', hashes);
    }
    private static void Require(bool condition, string stage) { if (!condition) throw new InvalidOperationException("Activity registry qualification failed: " + stage + "."); }
}
