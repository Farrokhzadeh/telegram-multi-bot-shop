using System.Security.Claims;
using System.Security.Cryptography;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Testing;

public static class AiBoundaryQualification
{
    public static AiShopContext Context(Guid brand) => new(1, brand, "Public shop", "en", false, null, null);
    public static ClaimsPrincipal Principal(string subject, string scheme = AiScopeRegistry.Scheme) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], scheme));
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: ct);
        var order = f.Request();
        Require((await f.Store.PurchaseLocalAsync(f.Context(0), CheckoutFixture.Actor(0), order, ct)).Outcome == CheckoutOutcome.Purchased);
        var registry = new AiScopeRegistry([new("public-own", f.Context(0), CheckoutFixture.Actor(0), order.OrderId, DateTime.UtcNow.AddMinutes(5)), new("public-same-brand", f.Context(3), CheckoutFixture.Actor(3), order.OrderId, DateTime.UtcNow.AddMinutes(5)), new("public-foreign", f.Context(1), CheckoutFixture.Actor(1), order.OrderId, DateTime.UtcNow.AddMinutes(5))]);
        var scope = registry.Resolve(AiScopeRegistry.Scheme, Principal("public-own"), DateTime.UtcNow)!;
        string before = await Snapshot(f, ct);
        var store = new AiContextStore(f.Database.Data);
        var context = await store.ReadAsync(scope, ct);
        Require(context is { Version: 1, Order: not null } && context.BrandId == f.Brands[0] && context.Order.OrderId == order.OrderId && !context.HumanSupportAvailable && context.PublicSupportContact is null);
        Require(await store.ReadAsync(registry.Resolve(AiScopeRegistry.Scheme, Principal("public-foreign"), DateTime.UtcNow)!, ct) is null);
        Require(await store.ReadAsync(registry.Resolve(AiScopeRegistry.Scheme, Principal("public-same-brand"), DateTime.UtcNow)!, ct) is null);
        Require(before == await Snapshot(f, ct));
        var reads = new DbSessionFactory(f.Database.Data, new DbSessionOptions { ReadOnly = true });
        bool rejected = false;
        await using (var read = await reads.BeginAsync(ct))
        {
            try { await read.ExecuteAsync(new DbStatement("UPDATE shop.brands SET public_name='public forbidden'"), ct); }
            catch (DbOperationException error) when (error.Failure.SqlState == "25006") { rejected = true; }
        }
        Require(rejected && before == await Snapshot(f, ct));
        Require(registry.Revoke("public-own")); rejected = false;
        try { await store.ReadAsync(scope, ct); } catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected);
        int calls = 0;
        var optional = new AiOptionalIntegration((_, _) => { calls++; throw new InvalidOperationException(); }, TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1), 1);
        Require((await optional.SuggestAsync(f.Context(0), false, context!, "public support", ct)).Fallback == AiFallback.Disabled && calls == 0);
        await EnableSupport(f, ct);
        byte[] secret = SHA256.HashData("public synthetic AI-off support"u8);
        using var verifier = new TelegramWebhookVerifier(f.Context(0), secret);
        var support = await new HumanSupportRouter(f.Sessions, f.Secrets).RouteAsync(verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body("/support", updateId: 912, messageId: 912)), ct);
        Require(support.Created && calls == 0);
        Require(await f.CountAsync("SELECT count(*) FROM shop.orders WHERE payment_status='paid'", ct) == 1);
        Require(await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) == 1);
    }
    private static async Task EnableSupport(CheckoutFixture f, CancellationToken ct)
    {
        var actor = await f.AdminAsync(ct); AdminToken nonce;
        await using (var begin = await f.Sessions.BeginAsync(ct))
        { nonce = (await AdminBindingStore.BeginAsync(begin, actor, 7999, TimeSpan.FromMinutes(5), Guid.NewGuid(), Guid.NewGuid(), ct))!; await begin.CommitAsync(ct); }
        using (nonce)
        {
            await using (var proof = await f.Sessions.BeginAsync(ct))
            { Require(await AdminBindingStore.ObservePrivateProofAsync(proof, actor.AdminId, nonce, PrivateTelegramProof.FromVerifiedIngress(f.Context(0), 7999, 7999, false, nonce), ct)); await proof.CommitAsync(ct); }
            await using var complete = await f.Sessions.BeginAsync(ct);
            Require(await AdminBindingStore.CompleteAsync(complete, actor, 7999, actor.AuthVersion, nonce, Guid.NewGuid(), Guid.NewGuid(), ct));
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.admins SET public_contact_handle='public_support' WHERE id=$1", DbValue.Uuid(actor.AdminId)), ct);
            await complete.ExecuteAsync(new DbStatement("UPDATE shop.brands SET support_enabled=true,customer_support_admin_id=$1,policy_version=policy_version+1 WHERE id=$2", DbValue.Uuid(actor.AdminId), DbValue.Uuid(f.Brands[0])), ct);
            await complete.CommitAsync(ct);
        }
    }
    public static async Task<string> Snapshot(CheckoutFixture f, CancellationToken ct)
    {
        await using var read = await f.Sessions.BeginAsync(ct);
        var parts = new List<string>();
        foreach (string table in new[] { "brands", "brand_channels", "users", "admins", "admin_sessions", "products", "brand_products", "inventory_items", "wallets", "wallet_entries", "orders", "payments", "fulfillments", "supplier_requests", "jobs", "campaigns", "telegram_deliveries", "support_tickets", "support_messages", "legacy_id_map", "audit_events", "discounts", "discount_uses", "discount_revisions", "bot_activity_events", "supplier_request_attempts", "product_supplier_sources", "product_stock_subscriptions" })
            parts.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(row_text,$1 ORDER BY row_text),'')) FROM (SELECT t::text row_text FROM shop.{table} t) rows", DbValue.Text("\n")), r => r.GetString(0), ct));
        return string.Join(':', parts);
    }
    public static void Require(bool condition) { if (!condition) throw new InvalidOperationException("AI boundary qualification failed."); }
}
