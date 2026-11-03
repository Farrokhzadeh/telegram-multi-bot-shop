using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Testing;

public static class PreorderProcessQualification
{
    private const string Application = "public-preorder-process";
    public static async Task ChildAsync()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1") throw new InvalidOperationException("Synthetic qualification is required.");
        string root = DirectoryValue("SHOP_TEST_PREORDER_SECRET_ROOT", "shop-secret-qualification-"), sync = DirectoryValue("SHOP_TEST_PREORDER_SYNC", "shop-preorder-process-");
        string phase = Environment.GetEnvironmentVariable("SHOP_TEST_PREORDER_PHASE") ?? "";
        if (phase is not ("before" or "after" or "complete")) throw new InvalidOperationException("Unknown qualification phase.");
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
        { Database = Environment.GetEnvironmentVariable("SHOP_TEST_PREORDER_DATABASE") ?? throw new InvalidOperationException("Synthetic database is missing."), ApplicationName = Application, Pooling = false };
        _ = SchemaManifest.ForQualification(options.ConnectionString);
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        await FoundationQualification.VerifySchemaAsync(data);
        using var secrets = SecretFiles.Load(root, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
        var sessions = new DbSessionFactory(data);
        var worker = new LocalPreorderWorker(new(sessions, secrets), CheckoutFixture.ResolveContexts());
        for (int i = 0; i < 30; i++)
        {
            JobLease? lease;
            await using (var claim = await sessions.BeginAsync())
            {
                lease = await OutboxStore.ClaimAsync(claim, JobKind.DrainLocalPreorders, Guid.NewGuid(), TimeSpan.FromSeconds(phase == "complete" ? 60 : 1));
                await claim.CommitAsync();
            }
            if (lease is null) { if (i == 0) throw new InvalidOperationException("Qualification work is missing."); return; }
            if (i == 0) await File.WriteAllTextAsync(Path.Combine(sync, "claimed"), lease.Id.ToString("D"));
            await worker.RunAsync(lease);
            if (phase != "complete")
            {
                await File.WriteAllTextAsync(Path.Combine(sync, "committed"), lease.Id.ToString("D"));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
        }
        throw new InvalidOperationException("Qualification work did not drain.");
    }
    public static async Task RunAsync(bool afterCommit, string executable, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(executable)) throw new InvalidOperationException("Linux qualification executable is required.");
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: ct);
        await using (var policy = await f.Sessions.BeginAsync(ct))
        { await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), ct); await policy.CommitAsync(ct); }
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, f.Secrets, [], (_, _) => throw new InvalidOperationException("Unexpected supplier transport.")));
        for (int user = 0; user < 2; user++)
            if ((await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, user == 0 ? 5 : 2), (_, _, _) => Task.FromResult(ChannelMembership.Member), ct)).Outcome != CheckoutOutcome.Purchased) throw new InvalidOperationException("Qualification acceptance failed.");
        await f.AddStockAsync(7, ct);
        string sync = Path.Combine(Path.GetTempPath(), "shop-preorder-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sync, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using (var gate = await f.Sessions.BeginAsync(ct))
            {
                if (!afterCommit)
                {
                    await gate.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock(934817263)"), ct);
                    await using var ddl = f.Database.Data.CreateCommand("CREATE FUNCTION shop.preorder_process_gate() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(934817263); RETURN NEW; END $$; CREATE TRIGGER preorder_process_gate BEFORE INSERT ON shop.fulfillments FOR EACH ROW WHEN (NEW.unit_no=5) EXECUTE FUNCTION shop.preorder_process_gate()");
                    await ddl.ExecuteNonQueryAsync(ct);
                }
                using var child = Start(afterCommit ? "after" : "before");
                try
                {
                    await UntilAsync(async () => afterCommit ? File.Exists(Path.Combine(sync, "committed")) : await ActiveAsync(true) == 1, child, ct);
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                    if (child.ExitCode == 0) throw new InvalidOperationException("Qualification worker was not killed.");
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(ct); }
                }
                await gate.RollbackAsync(ct);
            }
            await UntilAsync(async () => await ActiveAsync() == 0, null, ct);
            if (await f.CountAsync("SELECT count(*) FROM shop.fulfillments", ct) != (afterCommit ? 5 : 0) || await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", ct) != (afterCommit ? 1 : 0)) throw new InvalidOperationException("Killed worker left a partial transaction.");
            if (!afterCommit)
            {
                await using var ddl = f.Database.Data.CreateCommand("DROP TRIGGER preorder_process_gate ON shop.fulfillments; DROP FUNCTION shop.preorder_process_gate()");
                await ddl.ExecuteNonQueryAsync(ct);
                await Task.Delay(TimeSpan.FromMilliseconds(1100), ct);
            }
            Guid original = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(sync, "claimed"), ct));
            File.Delete(Path.Combine(sync, "claimed"));
            using (var restarted = Start("complete"))
            {
                try { await restarted.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
                finally { if (!restarted.HasExited) { restarted.Kill(entireProcessTree: true); await restarted.WaitForExitAsync(ct); } }
                if (restarted.ExitCode != 0) throw new InvalidOperationException("Fresh qualification worker failed.");
            }
            Guid recovered = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(sync, "claimed"), ct));
            if (afterCommit ? recovered == original : recovered != original) throw new InvalidOperationException("Fresh scheduler selected the wrong durable job.");
            if (await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='acquired'", ct) != 7 || await f.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'", ct) != 7 || await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", ct) != 2 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) != 2) throw new InvalidOperationException("Process restart repeated or lost business effects.");
            if (await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind NOT IN ('admin_adjustment','purchase_debit')", ct) != 0) throw new InvalidOperationException("Process restart changed financial history.");
            await using (var read = await f.Sessions.BeginAsync(ct))
            {
                var originalJob = await read.QuerySingleAsync(new DbStatement("SELECT status,attempts FROM shop.jobs WHERE id=$1", DbValue.Uuid(original)), r => (State: r.GetString(0), Attempts: r.GetInt64(1)), ct);
                if (originalJob != ("succeeded", afterCommit ? 1L : 2L)) throw new InvalidOperationException("Original crash job history changed.");
            }
            for (int user = 0; user < 2; user++)
            {
                JobLease manifest;
                await using (var claim = await f.Sessions.BeginAsync(ct))
                { manifest = await OutboxStore.ClaimAsync(claim, JobKind.PrepareManifest, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct) ?? throw new InvalidOperationException("Recovered manifest work is missing."); await claim.CommitAsync(ct); }
                var store = new DeliveryStore(f.Sessions, f.Secrets);
                await store.PrepareAsync(f.Context(user), manifest, ct);
                byte[] content = await store.ReadManifestAsync(f.Context(user), CheckoutFixture.Actor(user), manifest.Intent.OriginId!.Value, ct);
                CryptographicOperations.ZeroMemory(content);
            }
        }
        finally { Directory.Delete(sync, recursive: true); }
        Process Start(string phase)
        {
            var start = new ProcessStartInfo(executable.EndsWith(".dll", StringComparison.Ordinal) ? "dotnet" : executable) { UseShellExecute = false };
            if (executable.EndsWith(".dll", StringComparison.Ordinal)) start.ArgumentList.Add(executable);
            start.ArgumentList.Add("--preorder-worker-probe");
            start.Environment["SHOP_TEST_PREORDER_DATABASE"] = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database;
            start.Environment["SHOP_TEST_PREORDER_SECRET_ROOT"] = f.SecretRoot; start.Environment["SHOP_TEST_PREORDER_SYNC"] = sync; start.Environment["SHOP_TEST_PREORDER_PHASE"] = phase;
            return Process.Start(start) ?? throw new InvalidOperationException("Qualification worker did not start.");
        }
        Task<long> ActiveAsync(bool waiting = false) => f.CountAsync("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name='" + Application + "'" + (waiting ? " AND wait_event_type='Lock'" : ""), ct);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, Process? child, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (child?.HasExited == true || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Qualification process barrier failed.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }
    private static string DirectoryValue(string name, string prefix)
    {
        string path = Path.GetFullPath(Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Qualification directory is missing."));
        if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(path)) throw new InvalidOperationException("Qualification directory is invalid.");
        return path;
    }
}
