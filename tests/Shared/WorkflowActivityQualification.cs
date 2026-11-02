using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Testing;

public static class WorkflowActivityQualification
{
    public static async Task QuantityAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            for (int i = 0; i < 4; i++) await BrandUserStore.TransitionAsync(seed, f.Context(i), CheckoutFixture.Actor(i), f.Users[i], 0,
                CustomerFlowState.Quantity, productId: f.Product, anchorMessageId: 77, expiresAt: DateTime.UtcNow.AddHours(1), ct: ct);
            await seed.CommitAsync(ct);
        }
        var router = new UpdateRouter(f.Sessions, (_, _, _) => throw new InvalidOperationException(), new HumanSupportRouter(f.Sessions, f.Secrets));
        string[] digits = ["2", "۲", "٢"];
        for (int i = 0; i < digits.Length; i++)
        {
            var result = await router.RouteAsync(QuantityUpdate(f, i, digits[i], 100), ct);
            Require(result.Code == UpdateRouteCode.Quantity && result.Quantity == 2 && result.FlowRevision == 2 && result.EntityId == f.Product, "verified three-brand numeric ingress and localized digits");
            for (int j = 0; j < 100; j++) Require((await router.RouteAsync(QuantityUpdate(f, i, "9", 100), ct)).Code == UpdateRouteCode.Duplicate, "changed-text replay preserves original observation");
        }
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='quantity_enter' AND quantity_selected=2 AND flow_revision=1 AND telegram_update_id=100 AND idempotency_key='telegram:100'", ct) == 3, "one typed observation per membership with original revision");
        Require(await f.CountAsync("SELECT count(*) FROM shop.support_messages", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 0, "quantity is UI progress without support content or financial effects");
        bool reordered = false;
        try { await router.RouteAsync(QuantityUpdate(f, 3, "2", 99), ct); } catch (TelegramIngressRejectedException) { reordered = true; }
        Require(reordered && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 3, "unseen reordered quantity cannot attach to a newer flow");
    }
    public static async Task QuantityErrorsAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await BrandUserStore.TransitionAsync(seed, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], 0, CustomerFlowState.Quantity,
                productId: f.Product, anchorMessageId: 77, expiresAt: DateTime.UtcNow.AddHours(1), ct: ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET min_quantity=2,max_quantity=5 WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), ct);
            await seed.CommitAsync(ct);
        }
        var router = new UpdateRouter(f.Sessions, (_, _, _) => throw new InvalidOperationException(), new HumanSupportRouter(f.Sessions, f.Secrets));
        string[] invalid = ["0", "-1", "1", "6", "10001", "2147483648", "1.5", "1e1", "+2", " 2", "public private message"];
        for (int i = 0; i < invalid.Length; i++)
        {
            var result = await router.RouteAsync(QuantityUpdate(f, 0, invalid[i], 100 + i), ct);
            Require(result.Code == UpdateRouteCode.QuantityRejected && result.Quantity is null && result.FlowRevision == 1, "bounded invalid quantity has a finite private-data-free outcome");
        }
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='quantity_rejected' AND outcome_code='rejected' AND quantity_selected IS NULL AND flow_revision=1", ct) == invalid.Length, "one rejection per authenticated input without raw content");
        async Task Reject(VerifiedTelegramUpdate update)
        {
            bool rejected = false;
            try { await router.RouteAsync(update, ct); } catch (Exception e) when (e is TelegramIngressRejectedException or UnauthorizedAccessException) { rejected = true; }
            Require(rejected, "stale expired unpublished blocked and unknown quantity origins fail closed");
        }
        await Reject(QuantityUpdate(f, 0, "2", 120, 77));
        await Reject(QuantityUpdate(f, 9, "2", 120));
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_expires_at=CURRENT_TIMESTAMP-INTERVAL '1 second',flow_revision=flow_revision+1 WHERE id=$1", DbValue.Uuid(f.Users[0])), ct); await seed.CommitAsync(ct); }
        await Reject(QuantityUpdate(f, 0, "2", 120));
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_expires_at=CURRENT_TIMESTAMP+INTERVAL '1 hour',flow_revision=flow_revision+1,blocked_at=CURRENT_TIMESTAMP WHERE id=$1", DbValue.Uuid(f.Users[0])), ct);
            await seed.CommitAsync(ct);
        }
        await Reject(QuantityUpdate(f, 0, "2", 120));
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET blocked_at=NULL WHERE id=$1", DbValue.Uuid(f.Users[0])), ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=false WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), ct);
            await seed.CommitAsync(ct);
        }
        await Reject(QuantityUpdate(f, 0, "2", 120));
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == invalid.Length && await f.CountAsync("SELECT telegram_update_highwater FROM shop.brands WHERE bot_id=1001", ct) == 110, "unauthorized inputs cannot consume replay claims or append observations");
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await seed.ExecuteAsync(new DbStatement("UPDATE shop.brand_products SET visible=true WHERE brand_id=$1", DbValue.Uuid(f.Brands[0])), ct); await seed.CommitAsync(ct); }
        Require((await router.RouteAsync(QuantityUpdate(f, 0, "5", 120), ct)).Code == UpdateRouteCode.Quantity, "inclusive maximum quantity accepts without purchase");
    }
    public static async Task QuantityRollbackAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await BrandUserStore.TransitionAsync(seed, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], 0, CustomerFlowState.Quantity, productId: f.Product, anchorMessageId: 77, ct: ct); await seed.CommitAsync(ct); }
        var router = new UpdateRouter(f.Sessions, (_, _, _) => throw new InvalidOperationException());
        await using (var fail = f.Database.Data.CreateCommand("CREATE FUNCTION shop.synthetic_quantity_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='synthetic quantity failure'; END $$; CREATE CONSTRAINT TRIGGER synthetic_quantity_failure AFTER INSERT ON shop.bot_activity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION shop.synthetic_quantity_failure()")) await fail.ExecuteNonQueryAsync(ct);
        bool failed = false;
        try { await router.RouteAsync(QuantityUpdate(f, 0, "2", 100), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { failed = true; }
        Require(failed && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 0 && await f.CountAsync("SELECT flow_revision FROM shop.users WHERE flow_state='quantity'", ct) == 1 && await f.CountAsync("SELECT telegram_update_highwater FROM shop.brands WHERE bot_id=1001", ct) == -1, "deferred event failure rolls back flow and independent replay claim");
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER synthetic_quantity_failure ON shop.bot_activity_events; DROP FUNCTION shop.synthetic_quantity_failure()")) await repair.ExecuteNonQueryAsync(ct);
        Require((await router.RouteAsync(QuantityUpdate(f, 0, "2", 100), ct)).Code == UpdateRouteCode.Quantity, "rolled-back update retries successfully");
        Require((await router.RouteAsync(QuantityUpdate(f, 0, "2", 100), ct)).Code == UpdateRouteCode.Duplicate && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 1, "committed numeric update cannot repeat");
    }
    private static VerifiedTelegramUpdate QuantityUpdate(CheckoutFixture f, int user, string value, long update, long message = 100)
    {
        byte[] secret = System.Security.Cryptography.SHA256.HashData("public numeric ingress qualification"u8);
        using var verifier = new TelegramWebhookVerifier(f.Context(user), secret);
        return verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body(value, updateId: update, actorId: CheckoutFixture.Actor(user), chatId: CheckoutFixture.Actor(user), botId: f.Context(user).BotId, messageId: message));
    }
    public static async Task CheckoutAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, stock: 8, ct: ct);
        var local = f.Request();
        Require((await f.Store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), local, ct)).Outcome == CheckoutOutcome.Purchased, "direct local checkout");
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException());
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 2);
        Require((await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, ct)).Outcome == CheckoutOutcome.Purchased, "prepared checkout");
        var external = new PaymentSettlementFixture(f); await external.EnableAsync(ct);
        var unpaid = new CheckoutRequest(OrderIds.New(), f.Product, 1, CheckoutPayment.Crypto);
        Require((await service.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), unpaid, Member, ct)).Outcome == CheckoutOutcome.AwaitingPayment, "unpaid creation is not settlement");
        for (int i = 0; i < 100; i++)
        {
            Require((await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, ct)).Outcome == CheckoutOutcome.Replayed, "prepared replay");
            Require((await service.PurchaseAsync(f.Context(1), CheckoutFixture.Actor(1), unpaid, Member, ct)).Outcome == CheckoutOutcome.Replayed, "external replay");
        }
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='checkout_created'", ct) == 3, "one event per actual created order");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='quantity_enter'", ct) == 2, "one observation per fresh successful preparation");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events e JOIN shop.orders o ON o.id=e.order_id WHERE e.action_code='checkout_created' AND e.user_id=o.user_id AND e.product_id=o.product_id AND e.quantity_selected=o.quantity AND e.occurred_at=o.created_at AND e.telegram_update_id IS NULL", ct) == 3, "typed context and immutable original order clock");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 2 && await f.CountAsync("SELECT balance::bigint FROM shop.wallets WHERE user_id=(SELECT user_id FROM shop.orders WHERE id='" + request.OrderId.ToString("D") + "')", ct) == 70, "exact original wallet effects");
        Require(await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='awaiting_payment'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.payments WHERE status='settled'", ct) == 0, "creation does not fabricate settlement");
        bool changed = false;
        try { await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request with { Quantity = 3 }, Member, ct); } catch (UnauthorizedAccessException) { changed = true; }
        Require(changed && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 3, "changed command remains rejected");
    }
    public static async Task AtomicAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 4, ct: ct);
        await using (var fail = f.Database.Data.CreateCommand("CREATE FUNCTION shop.synthetic_workflow_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='synthetic workflow failure'; END $$; CREATE CONSTRAINT TRIGGER synthetic_workflow_failure AFTER INSERT ON shop.bot_activity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.action_code='checkout_created') EXECUTE FUNCTION shop.synthetic_workflow_failure()")) await fail.ExecuteNonQueryAsync(ct);
        var catalog = new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException());
        var service = new WalletCheckoutService(f.Sessions, f.Store, catalog);
        var request = new CheckoutRequest(OrderIds.New(), f.Product, 1);
        bool failed = false;
        try { await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, ct); }
        catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { failed = true; }
        Require(failed && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='available'", ct) == 4, "event commit failure rolls back order debit and allocation");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='checkout_created'", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='quantity_enter'", ct) == 1, "actual earlier preparation survives without a false order event");
        await using (var repair = f.Database.Data.CreateCommand("DROP TRIGGER synthetic_workflow_failure ON shop.bot_activity_events; DROP FUNCTION shop.synthetic_workflow_failure()")) await repair.ExecuteNonQueryAsync(ct);
        Require((await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, Member, ct)).Outcome == CheckoutOutcome.Purchased, "same order identity can retry rolled-back acceptance");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE action_code='checkout_created'", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 1, "retry commits exactly one order event and debit");
    }
    public static async Task FlowAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: ct);
        DateTime original;
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='quantity',flow_product_id=$1,flow_revision=1,flow_expires_at=CURRENT_TIMESTAMP-INTERVAL '5 minutes' WHERE id IN ($2,$3)", DbValue.Uuid(f.Product), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Users[3])), ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='quantity',flow_product_id=$1,flow_revision=1,flow_expires_at=CURRENT_TIMESTAMP+INTERVAL '1 hour' WHERE id=$2", DbValue.Uuid(f.Product), DbValue.Uuid(f.Users[1])), ct);
            original = await seed.QuerySingleAsync(new DbStatement("SELECT flow_expires_at FROM shop.users WHERE id=$1", DbValue.Uuid(f.Users[0])), r => r.GetUtc(0), ct);
            await seed.CommitAsync(ct);
        }
        string before = await DomainAsync(f.Sessions, false, ct);
        var store = new CustomerFlowExpiryStore(f.Sessions);
        Require(!await store.ExpireAsync(f.Context(1), f.Users[3], 1, ct) && !await store.ExpireAsync(f.Context(0), f.Users[0], 0, ct) && !await store.ExpireAsync(f.Context(1), f.Users[1], 1, ct), "wrong scope stale revision and future flow stay unchanged");
        var results = await Task.WhenAll(store.ExpireAsync(f.Context(0), f.Users[0], 1, ct), new CustomerFlowExpiryStore(f.Sessions).ExpireAsync(f.Context(0), f.Users[0], 1, ct));
        Require(results.Count(x => x) == 1, "competing expiry workers transition once");
        for (int i = 0; i < 100; i++) Require(!await store.ExpireAsync(f.Context(0), f.Users[0], 1, ct), "timeout replay has independent revision fence");
        await using (var read = await f.Sessions.BeginAsync(ct))
        {
            Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE user_id=$1 AND action_code='flow_expired' AND source_kind='system' AND outcome_code='cancelled' AND flow_revision=1 AND occurred_at=$2", DbValue.Uuid(f.Users[0]), DbValue.Utc(original)), r => r.GetInt64(0), ct) == 1, "real expired transition uses original deadline");
            var member = await BrandUserStore.RequireAsync(read, f.Context(0), CheckoutFixture.Actor(0), f.Users[0], ct);
            Require(member.Flow.State == CustomerFlowState.Idle && member.Flow.Revision == 2 && member.Flow.ProductId is null && member.Flow.ExpiresAt is null, "clear only expired UI context");
        }
        Require(before == await DomainAsync(f.Sessions, false, ct), "no money order stock coupon support or other domain changes");
        await using (var seed = await f.Sessions.BeginAsync(ct))
        { await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='quantity',flow_product_id=$1,flow_revision=3,flow_expires_at=CURRENT_TIMESTAMP-INTERVAL '181 days' WHERE id=$2", DbValue.Uuid(f.Product), DbValue.Uuid(f.Users[0])), ct); await seed.CommitAsync(ct); }
        Require(await store.ExpireAsync(f.Context(0), f.Users[0], 3, ct) && !await store.ExpireAsync(f.Context(0), f.Users[0], 3, ct), "already-old UI flow can expire without replay");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE flow_revision=3", ct) == 0, "expired original event is discarded instead of stamped with recovery time");
    }
    public static async Task RetentionAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        Guid order = Guid.NewGuid();
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.orders(id,user_id,product_id,quantity,order_kind,payment_status,currency,payment_method,unit_price_usdt,auto_price_toman_snapshot,
                    pricing_rounding_step_snapshot,price_calculation_version,brand_public_name_snapshot,product_title_snapshot,payment_terms_snapshot,fulfillment_kind_snapshot,local_first_snapshot,is_preorder,created_at,payment_due_at)
                VALUES($1,$2,$3,1,'product_purchase','awaiting_payment','USDT','crypto',10,false,5000,'public-test-v1','public brand','public item','public terms','local',true,false,CURRENT_TIMESTAMP-INTERVAL '181 days',CURRENT_TIMESTAMP-INTERVAL '181 days'+INTERVAL '30 minutes')
                """, DbValue.Uuid(order), DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product)), ct);
            await seed.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_revision=5 WHERE id=$1", DbValue.Uuid(f.Users[0])), ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,quantity_selected,outcome_code,idempotency_key,occurred_at)
                SELECT gen_random_uuid(),user_id,'checkout','flow','checkout_created',product_id,id,quantity,'accepted','public-original-order-event',created_at FROM shop.orders WHERE id=$1
                """, DbValue.Uuid(order)), ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,flow_revision,outcome_code,idempotency_key,occurred_at)
                VALUES(gen_random_uuid(),$1,'flow','system','flow_expired',4,'cancelled','public-original-flow-event',CURRENT_TIMESTAMP-INTERVAL '181 days'),
                    (gen_random_uuid(),$1,'flow','system','flow_expired',5,'cancelled','public-unproven-flow-event',CURRENT_TIMESTAMP-INTERVAL '181 days')
                """, DbValue.Uuid(f.Users[0])), ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,quantity_selected,outcome_code,idempotency_key,occurred_at,flow_revision)
                VALUES(gen_random_uuid(),$1,'quantity','flow','quantity_enter',$2,1,'accepted','workflow:quantity_enter:11111111111111111111111111111111',CURRENT_TIMESTAMP-INTERVAL '181 days',NULL),
                    (gen_random_uuid(),$1,'quantity','flow','quantity_enter',$2,1,'accepted','workflow:quantity_enter:22222222222222222222222222222222',CURRENT_TIMESTAMP-INTERVAL '181 days',5),
                    (gen_random_uuid(),$1,'quantity','flow','quantity_enter',$2,1,'accepted','workflow:quantity_enter:public-malformed-key',CURRENT_TIMESTAMP-INTERVAL '181 days',NULL)
                """, DbValue.Uuid(f.Users[0]), DbValue.Uuid(f.Product)), ct);
            await seed.CommitAsync(ct);
        }
        string before = await DomainAsync(f.Sessions, true, ct);
        var result = await new BotActivityRetentionStore(f.Sessions).CleanAsync(10, ct);
        Require(result.Deleted == 3 && result.Backlog.Expired == 3 && result.Backlog.Blocked == 3, "only independently covered domain activity and retired ephemeral observations are eligible; malformed or unproven contexts stay blocked");
        Require(before == await DomainAsync(f.Sessions, true, ct) && !await new CustomerFlowExpiryStore(f.Sessions).ExpireAsync(f.Context(0), f.Users[0], 4, ct), "purge and late timeout cannot change domain facts or revive old event");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key IN ('public-original-order-event','public-original-flow-event')", ct) == 0, "domain-origin rows remain deleted");
    }
    private static Task<ChannelMembership> Member(long channel, long actor, CancellationToken ct) => Task.FromResult(ChannelMembership.Member);
    internal static async Task<string> DomainAsync(DbSessionFactory sessions, bool includeUsers, CancellationToken ct)
    {
        await using var read = await sessions.BeginAsync(ct);
        var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' AND ($1 OR tablename<>'users') ORDER BY tablename", DbValue.Boolean(includeUsers)), r => r.GetString(0), 28, ct);
        var hashes = new List<string>();
        foreach (string table in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{table} t"), r => r.GetString(0), ct));
        return string.Join(':', hashes);
    }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Workflow activity qualification failed: " + stage + "."); }
}
