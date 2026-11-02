using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Platform.Core;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class TargetRestoreQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        await using var source = await CheckoutFixture.CreateAsync(stock: 3, ct: ct);
        await using var target = await DisposablePostgres.CreateAsync(ct);
        var request = source.Request();
        Require((await source.Store.PurchaseLocalAsync(source.Context(0), CheckoutFixture.Actor(0), request, ct)).Outcome == CheckoutOutcome.Purchased, "original synthetic paid order");
        var actor = await source.AdminAsync(ct);
        string observationKey = "workflow:quantity_enter:" + Guid.NewGuid().ToString("N");
        string lookupKey = "coupon_lookup:" + Guid.NewGuid().ToString("N");
        byte[] secret = SHA256.HashData("public target restore replay"u8);
        using var verifier = new TelegramWebhookVerifier(source.Context(0), secret);
        var update = verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body("/menu", updateId: 998));
        await using (var seed = await source.Sessions.BeginAsync(ct))
        {
            Require(await TelegramReplayStore.TryClaimAsync(seed, source.Context(0), 998, ct), "original independent checkpoint");
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,telegram_update_id,outcome_code,idempotency_key,occurred_at)
                VALUES(gen_random_uuid(),$1,'navigation','command','catalog_open',998,'accepted','telegram:998',CURRENT_TIMESTAMP-INTERVAL '181 days'),
                    (gen_random_uuid(),$1,'navigation','system','catalog_open',NULL,'accepted','public-unproven-original',CURRENT_TIMESTAMP-INTERVAL '181 days')
                """, DbValue.Uuid(source.Users[0])), ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,quantity_selected,outcome_code,idempotency_key,occurred_at)
                VALUES(gen_random_uuid(),$1,'quantity','flow','quantity_enter',$2,2,'accepted',$3,CURRENT_TIMESTAMP-INTERVAL '181 days')
                """, DbValue.Uuid(source.Users[0]), DbValue.Uuid(source.Product), DbValue.Text(observationKey)), ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.bot_activity_events(id,user_id,event_type,source_kind,action_code,outcome_code,idempotency_key,occurred_at)
                VALUES(gen_random_uuid(),$1,'discount','flow','discounts_open','rejected',$2,CURRENT_TIMESTAMP-INTERVAL '181 days')
                """, DbValue.Uuid(source.Users[0]), DbValue.Text(lookupKey)), ct);
            await seed.CommitAsync(ct);
        }
        string original = await AiBoundaryQualification.Snapshot(source, ct);
        string directory = Path.Combine(Path.GetTempPath(), "shop-target-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory, "target.dump");
        try
        {
            await TransferAsync(source.Database, backup, false, ct);
            byte[] digest = SHA256.HashData(await File.ReadAllBytesAsync(backup, ct));
            await TransferAsync(target, backup, true, ct);
            var sessions = new DbSessionFactory(target.Data);
            Require(await WorkflowActivityQualification.DomainAsync(source.Sessions, true, ct) == await WorkflowActivityQualification.DomainAsync(sessions, true, ct), "full target backup restores all 27 domain relations including checkpoints and committed jobs");
            using var timeline = new BotActivityStore(sessions, SHA256.HashData("public target restore cursor key"u8));
            var page = await timeline.ReadAsync(actor, new(Member: source.Users[0]), ct: ct);
            Require(page.Items.Length == 2 && page.Items.Any(x => x.Action == "checkout_created") && page.Items.Any(x => x.Action == "checkout_funded"), "original-time cutoff hides expired restored rows before any exposure");
            string before = await WorkflowActivityQualification.DomainAsync(sessions, true, ct);
            var cleanup = await new BotActivityRetentionStore(sessions).CleanAsync(500, ct);
            Require(cleanup.Deleted == 3 && cleanup.Backlog.Expired == 1 && cleanup.Backlog.Blocked == 1, "restore cleanup discards covered ingress and retired ephemeral quote/lookup observations while reporting unresolved provenance");
            var router = new UpdateRouter(sessions, (_, _, _) => throw new InvalidOperationException());
            var checkout = new CheckoutStore(sessions, source.Secrets);
            for (int i = 0; i < 100; i++)
            {
                Require((await router.RouteAsync(update, ct)).Code == UpdateRouteCode.Duplicate, "restored checkpoint rejects deleted-event replay");
                Require((await checkout.PurchaseLocalAsync(source.Context(0), CheckoutFixture.Actor(0), request, ct)).Outcome == CheckoutOutcome.Replayed, "restored financial command remains independently idempotent");
            }
            Require(before == await WorkflowActivityQualification.DomainAsync(sessions, true, ct), "cleanup and replay preserve every domain fact");
            Require((await timeline.ReadAsync(actor, new(Member: source.Users[0]), ct: ct)).Items.Length == 2, "restored expired activity never acquires recovery time");
            await new CatalogStore(sessions, source.Secrets, [], (_, _) => throw new InvalidOperationException()).PrepareAsync(source.Context(0), CheckoutFixture.Actor(0), new(source.Product, 2, CheckoutPayment.Wallet), ct);
            Require((await new PricingStore(sessions, source.Secrets).LookupAsync(source.Context(0), CheckoutFixture.Actor(0), "PUBLIC-RESTORE-LOOKUP", ct)).Outcome == CouponLookupOutcome.NotFound, "expired lookup history cannot affect a fresh one-minute rate window");
            await using (var read = await sessions.BeginAsync(ct))
            {
                Require(await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE idempotency_key IN ($1,$2)", DbValue.Text(observationKey), DbValue.Text(lookupKey)), r => r.GetInt64(0), ct) == 0, "fresh preparation or lookup cannot replay an imported original observation identity");
            }
            byte[] restoredDigest = SHA256.HashData(await File.ReadAllBytesAsync(backup, ct));
            Require(CryptographicOperations.FixedTimeEquals(digest, restoredDigest) && original == await AiBoundaryQualification.Snapshot(source, ct), "target cleanup cannot mutate the original database or backup artifact");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task TransferAsync(DisposablePostgres database, string backup, bool restore, CancellationToken ct)
    {
        string container = Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_CONTAINER") ?? throw new InvalidOperationException("Synthetic PostgreSQL container identity is required.");
        var options = new NpgsqlConnectionStringBuilder(database.Data.ConnectionString);
        string name = options.Database ?? "";
        if (Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1" || container.Length != 64 || !container.All(char.IsAsciiHexDigit) ||
            options.Host is not ("127.0.0.1" or "localhost" or "::1") || options.Username != "shop_test_runner" ||
            name.Length != 51 || !name.StartsWith("shop_qualification_", StringComparison.Ordinal) || !name[19..].All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Only owned disposable target databases are eligible.");
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = restore, RedirectStandardOutput = !restore };
        foreach (string argument in new[] { "exec", restore ? "-i" : container, restore ? container : "pg_dump" }) start.ArgumentList.Add(argument);
        if (restore) start.ArgumentList.Add("pg_restore");
        foreach (string argument in new[] { "--username=shop_test_runner", "--no-owner", "--no-privileges", "--dbname=" + name }) start.ArgumentList.Add(argument);
        if (restore) { start.ArgumentList.Add("--single-transaction"); start.ArgumentList.Add("--exit-on-error"); }
        else start.ArgumentList.Add("--format=custom");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic backup utility did not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await using (var file = new FileStream(backup, restore ? FileMode.Open : FileMode.CreateNew, restore ? FileAccess.Read : FileAccess.Write, FileShare.None))
            {
                if (restore) { await file.CopyToAsync(process.StandardInput.BaseStream, timeout.Token); process.StandardInput.Close(); }
                else await process.StandardOutput.BaseStream.CopyToAsync(file, timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token); await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic target backup/restore failed.");
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    private static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException("Target restore qualification failed: " + stage + "."); }
}
