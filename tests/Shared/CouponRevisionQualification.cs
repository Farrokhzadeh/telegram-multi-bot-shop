using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Testing;

public static class CouponRevisionQualification
{
    public static async Task OverrideAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, balance: 0, stock: 40, ct: ct);
        var actor = await f.AdminAsync(ct); Guid coupon = await Issue(f, actor, 100, ct);
        Guid spent = await Buy(f, 30, ct); await Prepare(f, ct); await Preorder(f, ct);
        Guid held = await Buy(f, 10, ct); string frozen = await Quotes(f, ct);
        var store = new PricingStore(f.Sessions, f.Secrets);
        var edit = new CouponOwnerEdit(12.5m, 0, 0, 2, null, DateTime.UtcNow.AddMinutes(-1), false, f.Product, f.Users[3]);
        Guid command = Guid.NewGuid();
        Require(await store.EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, ct) == 1, "owner override");
        Require(await store.EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, ct) == 1, "same command replay");
        bool conflict = false;
        try { await store.EditAsync(f.Context(0), actor, coupon, 0, edit with { Percent = 15 }, AuditReason.OperatorReview, command, ct); }
        catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.InvalidData) { conflict = true; }
        Require(conflict, "changed command rejected");
        Require(frozen == await Quotes(f, ct), "owner edit never rewrites accepted orders");
        await using (var read = await f.Sessions.BeginAsync(ct))
        {
            var facts = await read.QuerySingleAsync(new DbStatement("SELECT d.revision,d.redeemed_count,d.usage_adjustment_units,r.redeemed_count_at_revision,r.held_units_at_revision,r.percent_off::text,r.max_total_uses,r.max_uses_per_user,r.active,r.eligible_user_id,a.actor_admin_id,a.new_revision FROM shop.discounts d JOIN shop.discount_revisions r ON r.discount_id=d.id AND r.revision_no=d.revision JOIN shop.audit_events a ON a.id=r.audit_event_id WHERE d.id=$1", DbValue.Uuid(coupon)), r => (Version: r.GetInt64(0), Redeemed: r.GetInt64(1), Adjustment: r.GetInt64(2), AtEdit: r.GetInt64(3), Held: r.GetInt64(4), Percent: ExactDecimal.Parse(r.GetString(5)), Cap: r.GetInt64(6), Per: r.GetInt64(7), Active: r.GetBoolean(8), Member: r.GetGuid(9), Actor: r.GetGuid(10), AuditVersion: r.GetInt64(11)), ct);
            Require(facts == (1L, 30L, -28L, 30L, 10L, 12.5m, 0L, 0L, false, f.Users[3], actor.AdminId, 1L), "typed actual at-edit context");
        }
        var operations = new OperationsStore(f.Sessions);
        var first = await operations.RevisionsAsync(actor, f.Brands[0], coupon, null, 1, ct);
        var second = await operations.RevisionsAsync(actor, f.Brands[0], coupon, first.Next, 1, ct);
        Require(first.Items.Length == 1 && first.Items[0].Revision == 0 && first.Items[0].Redeemed == 0 && first.Items[0].Held == 0 && first.Next == 0, "initial revision and cursor");
        Require(second.Items.Length == 1 && second.Items[0].Revision == 1 && second.Items[0].Redeemed == 30 && second.Items[0].Held == 10 && second.Next is null, "audited history cursor");
        Require((await operations.RevisionsAsync(actor, f.Brands[1], coupon, null, 10, ct)).Items.Length == 0, "foreign history stays scoped");
        await Prepare(f, ct);
        Require(await f.CountAsync("SELECT redeemed_count FROM shop.discounts", ct) == 40 && await f.CountAsync("SELECT usage_adjustment_units FROM shop.discounts", ct) == -28, "actual use stays distinct from owner adjustment");
        Require(await f.CountAsync("SELECT count(*) FROM shop.discount_revisions", ct) == 2 && await f.CountAsync("SELECT count(*) FROM shop.discount_uses WHERE status='redeemed' AND discount_revision_id=(SELECT id FROM shop.discount_revisions WHERE revision_no=0)", ct) == 2, "redemption never invents edit history or replaces grants");
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.orders WHERE discount_pct_snapshot=100 AND release_authorized_at IS NOT NULL", ct) == 2, "old whole free claims complete without financial mutation");
        bool immutable = false;
        await using (var mutation = await f.Sessions.BeginAsync(ct))
        { try { await mutation.ExecuteAsync(new DbStatement("UPDATE shop.discount_revisions SET held_units_at_revision=0 WHERE revision_no=1"), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { immutable = true; } }
        Require(immutable, "historical at-edit context is immutable");
        _ = spent; _ = held;
    }
    public static async Task RaceAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, stock: 3, ct: ct);
        var actor = await f.AdminAsync(ct); Guid coupon = await Issue(f, actor, 10, ct); await Preorder(f, ct); await Buy(f, 3, ct);
        string quote = await Quotes(f, ct); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redeem = Task.Run(async () => { await gate.Task; await Prepare(f, ct); }, ct);
        var edit = Task.Run(async () => { await gate.Task; await new PricingStore(f.Sessions, f.Secrets).EditAsync(f.Context(0), actor, coupon, 0, new(50, 0, 0, 0, null, null, false, f.Product, f.Users[0]), AuditReason.OperatorReview, Guid.NewGuid(), ct); }, ct);
        gate.SetResult(); await Task.WhenAll(redeem, edit);
        await using var read = await f.Sessions.BeginAsync(ct);
        var context = await read.QuerySingleAsync(new DbStatement("SELECT redeemed_count_at_revision,held_units_at_revision,usage_adjustment_units FROM shop.discount_revisions WHERE discount_id=$1 AND revision_no=1", DbValue.Uuid(coupon)), r => (Redeemed: r.GetInt64(0), Held: r.GetInt64(1), Adjustment: r.GetInt64(2)), ct);
        Require(context is (0, 3, 0) or (3, 0, -3), "one serialized before-or-after redemption snapshot");
        Require(await f.CountAsync("SELECT redeemed_count FROM shop.discounts", ct) == 3 && await f.CountAsync("SELECT count(*) FROM shop.discount_revisions", ct) == 2 && quote == await Quotes(f, ct), "race preserves actual use revision count and frozen quote");
    }
    public static async Task RollbackAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(balance: 0, ct: ct);
        var actor = await f.AdminAsync(ct); Guid coupon = await Issue(f, actor, 10, ct); Guid command = Guid.NewGuid();
        await using (var fail = f.Database.Data.CreateCommand("CREATE FUNCTION shop.public_test_revision_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'public synthetic revision failure' USING ERRCODE='23514'; END $$; CREATE CONSTRAINT TRIGGER public_test_revision_failure AFTER INSERT ON shop.discount_revisions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.revision_no>0) EXECUTE FUNCTION shop.public_test_revision_failure()")) await fail.ExecuteNonQueryAsync(ct);
        bool rejected = false;
        var edit = new CouponOwnerEdit(50, 1, 1, 0, null, null, true, f.Product, f.Users[0]);
        try { await new PricingStore(f.Sessions, f.Secrets).EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, ct); }
        catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { rejected = true; }
        Require(rejected && await f.CountAsync("SELECT revision FROM shop.discounts", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.discount_revisions", ct) == 1 && await f.CountAsync("SELECT count(*) FROM shop.audit_events WHERE action='coupon_edit'", ct) == 0, "deferred failure rolls back settings revision and audit together");
        await using (var recover = f.Database.Data.CreateCommand("DROP TRIGGER public_test_revision_failure ON shop.discount_revisions")) await recover.ExecuteNonQueryAsync(ct);
        Require(await new PricingStore(f.Sessions, f.Secrets).EditAsync(f.Context(0), actor, coupon, 0, edit, AuditReason.OperatorReview, command, ct) == 1, "same command resumes without a history gap");
        Require(await f.CountAsync("SELECT count(*) FROM shop.discount_revisions", ct) == 2, "one exact successful edit revision");
        bool missing = false;
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.discounts SET percent_off=25,revision=revision+1 WHERE id=$1", DbValue.Uuid(coupon)), ct); await write.CommitAsync(ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { missing = true; } }
        Require(missing && await f.CountAsync("SELECT revision FROM shop.discounts", ct) == 1, "settings cannot commit without matching history");
    }
    private static async Task Preorder(CheckoutFixture f, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder' WHERE id=$1", DbValue.Uuid(f.Product)), ct); await session.CommitAsync(ct); }
    private static async Task<Guid> Issue(CheckoutFixture f, AuthenticatedAdmin actor, long cap, CancellationToken ct)
    {
        Guid coupon = Guid.NewGuid(); await using var session = await f.Sessions.BeginAsync(ct);
        await new PricingStore(f.Sessions, f.Secrets).IssueAsync(session, f.Context(0), actor, new(coupon, "PUBLIC-REVISION", CouponKind.Goodwill, 100, f.Product, f.Users[0], MaximumUses: cap), Guid.NewGuid(), ct);
        await session.CommitAsync(ct); return coupon;
    }
    private static async Task<Guid> Buy(CheckoutFixture f, int quantity, CancellationToken ct)
    {
        var service = new WalletCheckoutService(f.Sessions, f.Store, new(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Unexpected synthetic supplier call.")));
        var request = new CheckoutRequest(OrderIds.New(), f.Product, quantity);
        Require((await service.PurchaseAsync(f.Context(0), CheckoutFixture.Actor(0), request, (_, _, _) => Task.FromResult(ChannelMembership.Member), ct)).Outcome == CheckoutOutcome.Purchased, "whole synthetic free purchase"); return request.OrderId;
    }
    private static async Task Prepare(CheckoutFixture f, CancellationToken ct)
    { await using var session = await f.Sessions.BeginAsync(ct); var lease = await OutboxStore.ClaimAsync(session, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct) ?? throw new InvalidOperationException(); await session.CommitAsync(ct); await new DeliveryStore(f.Sessions, f.Secrets).PrepareAsync(f.Context(0), lease, ct); }
    private static async Task<string> Quotes(CheckoutFixture f, CancellationToken ct)
    { await using var read = await f.Sessions.BeginAsync(ct); return await read.QuerySingleAsync(new DbStatement("SELECT md5(COALESCE(string_agg(ROW(id,user_id,product_id,quantity,payment_method,currency,unit_price_usdt,discount_pct_snapshot,discount_units_snapshot,pricing_rounding_step_snapshot,price_calculation_version,created_at)::text,'|' ORDER BY id),'')) FROM shop.orders"), r => r.GetString(0), ct); }
    private static void Require(bool condition, string stage) { if (!condition) throw new InvalidOperationException("Coupon history qualification failed: " + stage + "."); }
}
