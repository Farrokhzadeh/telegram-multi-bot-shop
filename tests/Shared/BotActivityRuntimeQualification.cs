using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Testing;

public static class BotActivityRuntimeQualification
{
    public static async Task ReplayAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: ct);
        var router = new UpdateRouter(f.Sessions, (_, _, _) => throw new InvalidOperationException());
        using var verifier = new TelegramWebhookVerifier(f.Context(0), SHA256.HashData("public replay checkpoint"u8));
        var headers = new[] { TelegramFixture.Header(SHA256.HashData("public replay checkpoint"u8)) };
        async Task<UpdateRouteResult> Route(long id) => await router.RouteAsync(verifier.Verify(headers, TelegramFixture.Body("/menu", updateId: id)), ct);
        Require((await Route(100)).Code == UpdateRouteCode.Menu, "first route");
        Require((await Route(98)).Code == UpdateRouteCode.Menu, "out-of-order unseen update");
        for (int i = 0; i < 100; i++) Require((await Route(98)).Code == UpdateRouteCode.Duplicate, "hundred update replays");
        await using (var claim = await f.Sessions.BeginAsync(ct))
        {
            Require(await TelegramReplayStore.TryClaimAsync(claim, f.Context(0), 99, ct), "original expired update checkpoint");
            await Insert(claim, f.Users[0], "public-expired-original", 99, "CURRENT_TIMESTAMP-INTERVAL '181 days'", ct);
            await claim.CommitAsync(ct);
        }
        var cleaned = await new BotActivityRetentionStore(f.Sessions).CleanAsync(ct: ct);
        Require(cleaned.Deleted == 1 && cleaned.Backlog.Expired == 0, "delete expired analytics independently");
        Require((await Route(99)).Code == UpdateRouteCode.Duplicate, "deleted activity cannot reopen navigation or retimestamp");
        Require(await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events WHERE telegram_update_id=99", ct) == 0, "no replay-created activity");
        await using (var failed = await f.Sessions.BeginAsync(ct)) Require(await TelegramReplayStore.TryClaimAsync(failed, f.Context(0), 101, ct), "uncommitted checkpoint");
        Require((await Route(101)).Code == UpdateRouteCode.Menu, "failed transaction does not consume update");
        Require((await Route(5000)).Code == UpdateRouteCode.Menu && (await Route(1)).Code == UpdateRouteCode.Duplicate, "bounded stale/reset fail-closed fence");
        await using (var other = await f.Sessions.BeginAsync(ct))
        { Require(await TelegramReplayStore.TryClaimAsync(other, f.Context(1), 1, ct), "separate bot checkpoint"); await other.CommitAsync(ct); }
        bool rewind = false, clear = false;
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.brands SET telegram_update_highwater=4999 WHERE id=$1", DbValue.Uuid(f.Brands[0])), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { rewind = true; } }
        await using (var write = await f.Sessions.BeginAsync(ct))
        { try { await write.ExecuteAsync(new DbStatement("UPDATE shop.brands SET telegram_update_window=set_byte(telegram_update_window,0,0) WHERE id=$1", DbValue.Uuid(f.Brands[0])), ct); } catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { clear = true; } }
        Require(rewind && clear && await f.CountAsync("SELECT count(*) FROM shop.orders", ct) == 0 && await f.CountAsync("SELECT count(*) FROM shop.wallet_entries", ct) == 6, "checkpoint guards and domain isolation");
    }
    public static async Task RetentionAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(ct: ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            foreach (long id in new long[] { 10, 11, 12 })
            {
                Require(await TelegramReplayStore.TryClaimAsync(seed, f.Context(0), id, ct), "owned update checkpoint");
                await Insert(seed, f.Users[0], "public-expired-" + id, id, "CURRENT_TIMESTAMP-INTERVAL '181 days'", ct);
            }
            await Insert(seed, f.Users[0], "public-no-independent-fence", null, "CURRENT_TIMESTAMP-INTERVAL '181 days'", ct);
            await Insert(seed, f.Users[0], "public-uncovered-update", 1000, "CURRENT_TIMESTAMP-INTERVAL '181 days'", ct);
            await Insert(seed, f.Users[0], "public-recent", 12, "CURRENT_TIMESTAMP", ct);
            await seed.CommitAsync(ct);
        }
        string before = await Domain(f, ct);
        await using (var setup = f.Database.Data.CreateCommand("CREATE FUNCTION shop.synthetic_cleanup_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='synthetic commit failure'; END $$; CREATE CONSTRAINT TRIGGER synthetic_cleanup_failure AFTER DELETE ON shop.bot_activity_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION shop.synthetic_cleanup_failure()")) await setup.ExecuteNonQueryAsync(ct);
        bool failed = false;
        var maintenance = new BotActivityRetentionStore(f.Sessions);
        try { await maintenance.CleanAsync(1, ct); }
        catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { failed = true; }
        Require(failed && maintenance.Metrics == new BotActivityMaintenanceMetrics(0, 1, 0) && await f.CountAsync("SELECT count(*) FROM shop.bot_activity_events", ct) == 6, "failed cleanup rolls back and reports only aggregate error counts");
        await using (var setup = f.Database.Data.CreateCommand("DROP TRIGGER synthetic_cleanup_failure ON shop.bot_activity_events; DROP FUNCTION shop.synthetic_cleanup_failure()")) await setup.ExecuteNonQueryAsync(ct);
        var first = await maintenance.CleanAsync(1, ct);
        Require(first.Deleted == 1 && first.Backlog.Expired == 4 && first.Backlog.Blocked == 2, "bounded restart and blocked backlog");
        Require(maintenance.Metrics == new BotActivityMaintenanceMetrics(1, 1, 1), "retry exposes aggregate success failure and deleted counts without event bodies");
        var remaining = await Task.WhenAll(new BotActivityRetentionStore(f.Sessions).CleanAsync(1, ct), new BotActivityRetentionStore(f.Sessions).CleanAsync(1, ct));
        Require(remaining.Sum(r => r.Deleted) == 2, "concurrent cleaners partition rows");
        var final = await new BotActivityRetentionStore(f.Sessions).CleanAsync(500, ct);
        Require(final.Deleted == 0 && final.Backlog.Expired == 2 && final.Backlog.Blocked == 2 && before == await Domain(f, ct), "no domain cascade or unsafe unqualified purge");
        await using (var boundary = await f.Sessions.BeginAsync(ct))
        {
            await Insert(boundary, f.Users[0], "public-exact-cutoff", 12, "CURRENT_TIMESTAMP-INTERVAL '4320 hours'", ct);
            Require(await boundary.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key='public-exact-cutoff' AND occurred_at>=CURRENT_TIMESTAMP-INTERVAL '4320 hours'"), r => r.GetInt64(0), ct) == 1, "equality retained at original UTC cutoff");
            bool equality = false;
            try { await boundary.ExecuteAsync(new DbStatement("DELETE FROM shop.bot_activity_events WHERE idempotency_key='public-exact-cutoff'"), ct); }
            catch (DbOperationException e) when (e.Failure.Kind == DbFailureKind.Constraint) { equality = true; }
            Require(equality, "database independently rejects equality deletion");
        }
    }
    public static async Task TimelineAsync(CancellationToken ct = default)
    {
        await using var f = await CheckoutFixture.CreateAsync(users: 6, ct: ct);
        var actor = await f.AdminAsync(ct);
        await using (var seed = await f.Sessions.BeginAsync(ct))
        {
            for (int i = 0; i < 5; i++) await Insert(seed, f.Users[0], "public-timeline-" + i, i, "CURRENT_TIMESTAMP-INTERVAL '1 hour'", ct);
            await Insert(seed, f.Users[1], "public-foreign", 1, "CURRENT_TIMESTAMP", ct);
            await Insert(seed, f.Users[3], "public-same-brand-other-member", 1, "CURRENT_TIMESTAMP", ct);
            await Insert(seed, f.Users[0], "public-old", 1, "CURRENT_TIMESTAMP-INTERVAL '181 days'", ct);
            await Insert(seed, f.Users[0], "public-future", 1, "CURRENT_TIMESTAMP+INTERVAL '1 hour'", ct);
            await seed.CommitAsync(ct);
        }
        using var store = new BotActivityStore(f.Sessions, SHA256.HashData("public timeline cursor fixture"u8));
        var filter = new BotActivityFilter(f.Brands[0], f.Users[0], Action: "catalog_open");
        var first = await store.ReadAsync(actor, filter, limit: 2, ct: ct);
        Require(first.Items.Length == 2 && first.Next is not null && first.Items.All(e => e.MemberId == f.Users[0] && e.BrandId == f.Brands[0]), "membership scoped timeline");
        var second = await store.ReadAsync(actor, filter, first.Next, 2, ct);
        var third = await store.ReadAsync(actor, filter, second.Next, 2, ct);
        Require(second.Items.Length == 2 && third.Items.Length == 1 && third.Next is null && first.Items.Concat(second.Items).Concat(third.Items).Select(e => e.Id).Distinct().Count() == 5 && first.AsOf == third.AsOf, "stable tuple pages hide expired and future rows");
        Require((await store.ReadAsync(actor, filter with { Brand = f.Brands[1] }, ct: ct)).Items.Length == 0, "foreign brand filter cannot expose member");
        bool tampered = false, rebound = false;
        try { await store.ReadAsync(actor, filter, first.Next![..100] + (first.Next[100] == 'A' ? 'B' : 'A') + first.Next[101..], 2, ct); } catch (ArgumentException) { tampered = true; }
        try { await store.ReadAsync(actor, filter with { Member = f.Users[3] }, first.Next, 2, ct); } catch (ArgumentException) { rebound = true; }
        Require(tampered && rebound, "cursor signature and scope binding");
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); app.UseRouting();
        app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
        app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, tokenCt) =>
        { await using var auth = await f.Sessions.BeginAsync(tokenCt); return await AdminSessionStore.AuthenticateAsync(auth, token, tokenCt); }));
        app.MapBotActivityAdministration(store); await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string route = $"/v1/admin/bot-activity?brandId={f.Brands[0]:D}&memberId={f.Users[0]:D}&limit=2";
        Require((await client.GetAsync(route, ct)).StatusCode == HttpStatusCode.Unauthorized, "HTTP requires independent administrator");
        IssuedAdminSession issued;
        await using (var auth = await f.Sessions.BeginAsync(ct))
        { issued = await AdminSessionStore.LoginAsync(auth, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), ct); await auth.CommitAsync(ct); }
        using (issued)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token.ExportForTransport());
            string body = await client.GetStringAsync(route, ct);
            Require(body.Contains("catalog_open", StringComparison.Ordinal) && !body.Contains("idempotency", StringComparison.OrdinalIgnoreCase) && !body.Contains("telegram", StringComparison.OrdinalIgnoreCase), "HTTP safe typed projection");
            Require((await client.GetAsync(route + "&action=raw-secret-text", ct)).StatusCode == HttpStatusCode.BadRequest, "unknown action has no read authority");
            await using (var revoke = await f.Sessions.BeginAsync(ct))
            { await revoke.ExecuteAsync(new DbStatement("UPDATE shop.admin_sessions SET revoked_at=clock_timestamp()"), ct); await revoke.CommitAsync(ct); }
            Require((await client.GetAsync(route, ct)).StatusCode == HttpStatusCode.Unauthorized, "HTTP revalidates revocation");
        }
        bool denied = false;
        try { await store.ReadAsync(actor, filter, first.Next, 2, ct); } catch (AdminAuthenticationException) { denied = true; }
        Require(denied, "every cursor page revalidates session");
        await app.StopAsync(ct);
    }
    private static Task<int> Insert(DbSession session, Guid member, string key, long? update, string time, CancellationToken ct) => session.ExecuteAsync(new DbStatement($"""
        INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,telegram_update_id,outcome_code,idempotency_key,occurred_at)
        VALUES(gen_random_uuid(),$1,'navigation','command','catalog_open',$2,'accepted',$3,{time})
        """, DbValue.Uuid(member), update is long id ? DbValue.Bigint(id) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Bigint), DbValue.Text(key)), ct);
    private static async Task<string> Domain(CheckoutFixture f, CancellationToken ct)
    {
        await using var read = await f.Sessions.BeginAsync(ct);
        var tables = await read.QueryManyAsync(new DbStatement("SELECT tablename::text FROM pg_tables WHERE schemaname='shop' AND tablename<>'bot_activity_events' ORDER BY tablename"), r => r.GetString(0), 28, ct);
        var hashes = new List<string>();
        foreach (string table in tables) hashes.Add(await read.QuerySingleAsync(new DbStatement($"SELECT md5(COALESCE(string_agg(t::text,'|' ORDER BY t::text),'')) FROM shop.{table} t"), r => r.GetString(0), ct));
        return string.Join(':', hashes);
    }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Activity runtime qualification failed: " + stage + "."); }
}
