using Platform.Telegram;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
[SupportedOSPlatform("linux")]
public sealed class StageWriterHostTests
{
    private const string Root = "/stage/target";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task Until(Func<Task<bool>> predicate, Process? process = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct); limit.CancelAfter(TimeSpan.FromSeconds(15));
        while (!await predicate())
        {
            if (process?.HasExited == true) throw new InvalidOperationException("Synthetic stage actor exited before its checkpoint.");
            await Task.Delay(20, limit.Token);
        }
    }
    private static void Prepare()
    {
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string name in new[] { "requests", "results", "tmp" }) Directory.CreateDirectory(Path.Combine(Root, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CutoverStageRuntime.Write(Path.Combine(Root, "identity"), Guid.NewGuid().ToString("N"));
    }
    private static CutoverStageState State() => JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "state.json")), CutoverStageJson.Default.CutoverStageState)!;
    private static Process Start()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add("/probe/StageHost.dll"); start.ArgumentList.Add("serve");
        return Process.Start(start) ?? throw new InvalidOperationException();
    }
    private static async Task Ready(Process process) => await Until(() => Task.FromResult(File.Exists(Path.Combine(Root, "ready.json"))), process);
    private static void Request(Guid id, string command) => CutoverStageRuntime.Write(Path.Combine(Root, "requests", id.ToString("N") + ".request"), command);
    private static async Task<CutoverStageResult> Result(Guid id, Process process)
    {
        string path = Path.Combine(Root, "results", id.ToString("N") + ".json");
        await Until(() => Task.FromResult(File.Exists(path)), process);
        var result = JsonSerializer.Deserialize(File.ReadAllBytes(path), CutoverStageJson.Default.CutoverStageResult)!;
        Assert.True(result.Synthetic); Assert.False(result.ReleaseReady); return result;
    }
    private static async Task Stop(Process process)
    {
        Guid id = Guid.NewGuid(); Request(id, "stop"); await Result(id, process);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct); Assert.Equal(0, process.ExitCode);
    }
    private static async Task Cleanup(CutoverStageState? state, NpgsqlDataSource? data, Process? process)
    {
        if (process is not null) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
        if (data is not null) await data.DisposeAsync();
        if (state is not null)
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN"));
            Assert.Equal("shop_test_control", options.Database); Assert.Equal("shop_test_runner", options.Username);
            Assert.True(state.Database.StartsWith("shop_qualification_", StringComparison.Ordinal) && state.Database.Length == 51 && Guid.TryParseExact(state.Database[19..], "N", out _));
            await using var admin = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
            await using var drop = admin.CreateCommand("DROP DATABASE \"" + state.Database + "\" WITH (FORCE)"); await drop.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(Root))
        {
            string history = "/stage/history"; Directory.CreateDirectory(history, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Move(Root, Path.Combine(history, state?.Run.ToString("N") ?? Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(Root, "tmp"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private static async Task<AuthenticatedAdmin> Admin(DbSessionFactory sessions)
    {
        await using var login = await sessions.BeginAsync(Ct);
        using var ticket = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
        await login.CommitAsync(Ct); await using var read = await sessions.BeginAsync(Ct); return await AdminSessionStore.AuthenticateAsync(read, ticket.Token, Ct);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneActorRejectsStaleRestartAndRecoversOnlyWithExplicitRevision(bool interrupt)
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); await CutoverStageRuntime.RunAsync(Root, "telegram-init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data); var context = CheckoutFixture.ResolveContexts()[0]; var owner = await Admin(sessions); var controls = new BrandWriterStore(sessions);
            actor = Start(); await Ready(actor); Guid accepted = Guid.NewGuid(); Request(accepted, "wallet"); Assert.Equal("Purchased", (await Result(accepted, actor)).Outcome);
            if (interrupt)
            {
                Guid held = Guid.NewGuid(); Request(held, "wallet-paused");
                await Until(() => Task.FromResult(File.Exists(Path.Combine(Root, "held-" + held.ToString("N") + ".json"))), actor);
                Task<BrandWriterState> pause = controls.SetActiveAsync(owner, context, 0, false, Guid.NewGuid(), Ct);
                await Until(async () => { await using var read = await sessions.BeginAsync(Ct); return await read.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND cardinality(pg_blocking_pids(pid))>0)"), r => r.GetBoolean(0), Ct); }, actor);
                Assert.False(pause.IsCompleted); actor.Kill(true); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct); Assert.NotEqual(0, actor.ExitCode); Assert.Equal(new(false, 1), await pause);
                actor.Dispose(); actor = null;
                await using var read = await sessions.BeginAsync(Ct);
                Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders WHERE id=$1", DbValue.Uuid(held)), r => r.GetInt64(0), Ct));
            }
            else
            {
                Assert.Equal(new(false, 1), await controls.SetActiveAsync(owner, context, 0, false, Guid.NewGuid(), Ct));
                string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
                Guid rejected = Guid.NewGuid(); Request(rejected, "wallet"); Assert.Equal("Rejected", (await Result(rejected, actor)).Outcome);
                Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
            }
            Assert.Equal(new(true, 2), await controls.SetActiveAsync(owner, context, 1, true, Guid.NewGuid(), Ct));
            if (actor is not null)
            {
                string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
                Guid stale = Guid.NewGuid(); Request(stale, "wallet"); Assert.Equal("Rejected", (await Result(stale, actor)).Outcome);
                Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct)); await Stop(actor); actor.Dispose(); actor = null;
            }
            actor = Start(); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(78, actor.ExitCode); Assert.False(File.Exists(Path.Combine(Root, "ready.json"))); actor.Dispose(); actor = null;
            Assert.Equal(0, State().WriterRevision);
            state = state with { WriterRevision = 2 }; CutoverStageRuntime.Write(Path.Combine(Root, "state.json"), JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState), true);
            Directory.CreateDirectory(Path.Combine(Root, "retained-results"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(Path.Combine(Root, "results", accepted.ToString("N") + ".json"), Path.Combine(Root, "retained-results", accepted.ToString("N") + ".json"));
            if (interrupt)
            {
                Guid held = Directory.GetFiles(Path.Combine(Root, "requests")).Select(Path.GetFileNameWithoutExtension).Where(n => Guid.TryParseExact(n, "N", out _)).Select(n => Guid.ParseExact(n!, "N")).Single(id => File.ReadAllText(Path.Combine(Root, "requests", id.ToString("N") + ".request")).Trim() == "wallet-paused");
                Directory.CreateDirectory(Path.Combine(Root, "retained-interrupted"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.Move(Path.Combine(Root, "requests", held.ToString("N") + ".request"), Path.Combine(Root, "retained-interrupted", held.ToString("N") + ".request"));
            }
            actor = Start(); await Ready(actor); Assert.Equal("Replayed", (await Result(accepted, actor)).Outcome);
            var key = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "telegram-key.json")), CutoverTelegramJson.Default.CutoverTelegramKey)!;
            Guid telegram = Guid.NewGuid(); byte[] secret = Convert.FromHexString(key.Secret);
            var input = new CutoverTelegramInput(state.Run, 99, Encoding.UTF8.GetString(TelegramFixture.Body("/menu", updateId: 99)), TelegramFixture.Header(secret));
            CutoverStageRuntime.Write(Path.Combine(Root, "telegram", telegram.ToString("N") + ".json"), JsonSerializer.Serialize(input, CutoverTelegramJson.Default.CutoverTelegramInput)); Request(telegram, "telegram"); Assert.Equal("Menu", (await Result(telegram, actor)).Outcome);
            var status = Guid.NewGuid(); Request(status, "status"); var final = await Result(status, actor); Assert.Equal((1L, 1L, 1L), (final.Orders, final.Allocated, final.Debits));
            await using (var read = await sessions.BeginAsync(Ct)) Assert.True(await read.QuerySingleAsync(new DbStatement("SELECT balance=90 FROM shop.wallets WHERE user_id=$1", DbValue.Uuid(state.User)), r => r.GetBoolean(0), Ct));
            await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, data, actor); }
    }
    [Fact]
    public async Task PaymentInitializationPersistsItsExactWriterRevisionBeforeServe()
    {
        CutoverStageState? state = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); state = State(); Assert.Equal(0, state.WriterRevision);
            await CutoverStageRuntime.RunAsync(Root, "payment-init", Ct); state = State(); Assert.Equal(1, state.WriterRevision);
            actor = Start(); await Ready(actor); await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, null, actor); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneIngressRetainsRejectedInputAndReplaysAfterExplicitResume(bool payment)
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); await CutoverStageRuntime.RunAsync(Root, "payment-init", Ct); await CutoverStageRuntime.RunAsync(Root, "telegram-init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data); var context = CheckoutFixture.ResolveContexts()[0]; var owner = await Admin(sessions); var controls = new BrandWriterStore(sessions);
            long postings;
            await using (var read = await sessions.BeginReadOnlyAsync(Ct)) postings = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), Ct);
            string body, directory = payment ? "payment" : "telegram";
            if (payment)
            {
                var key = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "payment-key.json")), CutoverPaymentJson.Default.CutoverPaymentKey)!;
                string notice = PaymentNoticeFixture.Body(key.Origin, "finished", asset: "usdtbsc", quote: 10.25m, provider: key.Provider);
                string signature = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.ASCII.GetBytes(key.Secret), Encoding.UTF8.GetBytes(notice)));
                body = JsonSerializer.Serialize(new CutoverPaymentInput(state.Run, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(signature))), notice, signature), CutoverPaymentJson.Default.CutoverPaymentInput);
            }
            else
            {
                var key = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "telegram-key.json")), CutoverTelegramJson.Default.CutoverTelegramKey)!;
                body = JsonSerializer.Serialize(new CutoverTelegramInput(state.Run, 801, Encoding.UTF8.GetString(TelegramFixture.Body("/menu", updateId: 801)), TelegramFixture.Header(Convert.FromHexString(key.Secret))), CutoverTelegramJson.Default.CutoverTelegramInput);
            }
            Guid Send()
            {
                Guid id = Guid.NewGuid(); CutoverStageRuntime.Write(Path.Combine(Root, directory, id.ToString("N") + ".json"), body); Request(id, directory); return id;
            }
            actor = Start(); await Ready(actor); Assert.Equal(new(false, 2), await controls.SetActiveAsync(owner, context, 1, false, Guid.NewGuid(), Ct));
            string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            Guid pending = Send(); Assert.Equal("Rejected", (await Result(pending, actor)).Outcome);
            Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct)); Assert.Empty(Directory.GetFiles(Path.Combine(Root, "payment-archives")));
            Assert.Equal(new(true, 3), await controls.SetActiveAsync(owner, context, 2, true, Guid.NewGuid(), Ct));
            before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            Assert.Equal("Rejected", (await Result(Send(), actor)).Outcome); Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
            await Stop(actor); actor.Dispose(); actor = Start(); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(78, actor.ExitCode); Assert.False(File.Exists(Path.Combine(Root, "ready.json"))); actor.Dispose(); actor = null;
            state = state with { WriterRevision = 3 }; CutoverStageRuntime.Write(Path.Combine(Root, "state.json"), JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState), true);
            string retained = Path.Combine(Root, "retained-results"); Directory.CreateDirectory(retained, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string result = Path.Combine(Root, "results", pending.ToString("N") + ".json");
            File.Move(result, Path.Combine(retained, pending.ToString("N") + "-rejected.json"));
            actor = Start(); await Ready(actor); Assert.Equal(payment ? "Ready" : "Menu", (await Result(pending, actor)).Outcome);
            byte[] original = File.ReadAllBytes(Path.Combine(Root, directory, pending.ToString("N") + ".json"));
            var archives = Directory.GetFiles(Path.Combine(Root, "payment-archives")).ToDictionary(p => p, File.ReadAllBytes);
            actor.Kill(true); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct); Assert.NotEqual(0, actor.ExitCode); actor.Dispose(); actor = null;
            File.Move(result, Path.Combine(retained, pending.ToString("N") + "-accepted.json"));
            actor = Start(); await Ready(actor); Assert.Equal(payment ? "Replayed" : "Duplicate", (await Result(pending, actor)).Outcome);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(Root, directory, pending.ToString("N") + ".json")));
            Assert.Equal(payment ? 1 : 0, archives.Count); Assert.Equal(archives.Count, Directory.GetFiles(Path.Combine(Root, "payment-archives")).Length);
            foreach (var archive in archives) Assert.Equal(archive.Value, File.ReadAllBytes(archive.Key));
            await using (var read = await sessions.BeginReadOnlyAsync(Ct))
            {
                Assert.Equal(postings, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), Ct));
                Assert.Equal(payment ? 1L : 0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='pending'"), r => r.GetInt64(0), Ct));
                Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.payments WHERE status='settled'"), r => r.GetInt64(0), Ct));
                Assert.Equal(payment ? 0L : 1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.bot_activity_events WHERE telegram_update_id=801"), r => r.GetInt64(0), Ct));
            }
            Guid status = Guid.NewGuid(); Request(status, "status"); var final = await Result(status, actor); Assert.Equal((0L, 0L, 0L), (final.Orders, final.Allocated, final.Debits)); await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, data, actor); }
    }

    [Fact]
    public async Task SingleBrandApiBotAndPaymentWorkerShareRevisionAndRecoverWithoutDuplicatingMoney()
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); await CutoverStageRuntime.RunAsync(Root, "payment-init", Ct); await CutoverStageRuntime.RunAsync(Root, "telegram-init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data); var context = CheckoutFixture.ResolveContexts()[0]; var owner = await Admin(sessions); var controls = new BrandWriterStore(sessions);
            await using var login = await sessions.BeginAsync(Ct);
            using var ticket = await AdminSessionStore.LoginAsync(login, "public-checkout-admin", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct); await login.CommitAsync(Ct);
            Guid adjustment = Guid.NewGuid(); string body = "{\"BrandId\":\"" + context.BrandId + "\",\"Value\":{\"MemberId\":\"" + state.User + "\",\"AmountUsd\":\"1\",\"ExpectedVersion\":1,\"Reason\":\"public synthetic adjustment\",\"CommandId\":\"" + adjustment + "\"}}";
            HttpClient Client()
            {
                var endpoint = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "api.json")), CutoverStageJson.Default.CutoverStageApi)!;
                Assert.Equal(state.Run, endpoint.Run); var uri = new Uri(endpoint.Address); Assert.Equal("127.0.0.1", uri.Host); Assert.Equal("http", uri.Scheme);
                var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ticket.Token.ExportForTransport()); return client;
            }
            async Task<HttpStatusCode> Adjust(HttpClient client, bool fresh = false)
            {
                using var response = await client.PostAsync("/v1/admin/wallet-adjustments", new StringContent(fresh ? body.Replace(adjustment.ToString(), Guid.NewGuid().ToString()).Replace("\"ExpectedVersion\":1", "\"ExpectedVersion\":2") : body, Encoding.UTF8, "application/json"), Ct); return response.StatusCode;
            }
            actor = Start(); await Ready(actor); using var first = Client();
            for (int i = 0; i < 16; i++) { Guid probe = Guid.NewGuid(); Request(probe, "status"); Assert.Equal("ready", (await Result(probe, actor)).Outcome); }
            first.DefaultRequestHeaders.Authorization = null; Assert.Equal(HttpStatusCode.Unauthorized, await Adjust(first));
            first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ticket.Token.ExportForTransport()); Assert.Equal(HttpStatusCode.OK, await Adjust(first));
            var key = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "payment-key.json")), CutoverPaymentJson.Default.CutoverPaymentKey)!;
            string notice = PaymentNoticeFixture.Body(key.Origin, "finished", asset: "usdtbsc", quote: 10.25m, provider: key.Provider);
            string signature = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.ASCII.GetBytes(key.Secret), Encoding.UTF8.GetBytes(notice)));
            var input = new CutoverPaymentInput(state.Run, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(signature))), notice, signature);
            Guid capture = Guid.NewGuid(); CutoverStageRuntime.Write(Path.Combine(Root, "payment", capture.ToString("N") + ".json"), JsonSerializer.Serialize(input, CutoverPaymentJson.Default.CutoverPaymentInput)); Request(capture, "payment"); Assert.Equal("Ready", (await Result(capture, actor)).Outcome);
            Assert.Equal(new(false, 2), await controls.SetActiveAsync(owner, context, 1, false, Guid.NewGuid(), Ct));
            string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            Guid worker = Guid.NewGuid(); Request(worker, "payment-worker"); Assert.Equal("Rejected", (await Result(worker, actor)).Outcome); Assert.True((int)await Adjust(first, true) >= 400);
            Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
            Assert.Equal(new(true, 3), await controls.SetActiveAsync(owner, context, 2, true, Guid.NewGuid(), Ct));
            before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            Guid stale = Guid.NewGuid(); Request(stale, "payment-worker"); Assert.Equal("Rejected", (await Result(stale, actor)).Outcome); Assert.True((int)await Adjust(first, true) >= 400);
            Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct)); await Stop(actor); actor.Dispose(); actor = Start(); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(78, actor.ExitCode); Assert.False(File.Exists(Path.Combine(Root, "ready.json"))); Assert.False(File.Exists(Path.Combine(Root, "api.json"))); actor.Dispose(); actor = null;
            state = state with { WriterRevision = 3 }; CutoverStageRuntime.Write(Path.Combine(Root, "state.json"), JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState), true);
            string retained = Path.Combine(Root, "retained-results"); Directory.CreateDirectory(retained, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string result = Path.Combine(Root, "results", worker.ToString("N") + ".json"); File.Move(result, Path.Combine(retained, worker.ToString("N") + "-rejected.json"));
            actor = Start(); await Ready(actor); Assert.Equal("Settled", (await Result(worker, actor)).Outcome);
            using (var resumed = Client()) Assert.Equal(HttpStatusCode.OK, await Adjust(resumed));
            var telegramKey = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "telegram-key.json")), CutoverTelegramJson.Default.CutoverTelegramKey)!;
            Guid telegram = Guid.NewGuid(); var update = new CutoverTelegramInput(state.Run, 901, Encoding.UTF8.GetString(TelegramFixture.Body("/menu", updateId: 901)), TelegramFixture.Header(Convert.FromHexString(telegramKey.Secret)));
            CutoverStageRuntime.Write(Path.Combine(Root, "telegram", telegram.ToString("N") + ".json"), JsonSerializer.Serialize(update, CutoverTelegramJson.Default.CutoverTelegramInput)); Request(telegram, "telegram"); Assert.Equal("Menu", (await Result(telegram, actor)).Outcome);
            Guid purchase = Guid.NewGuid(); Request(purchase, "wallet-current"); Assert.Equal("Purchased", (await Result(purchase, actor)).Outcome);
            actor.Kill(true); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct); actor.Dispose(); actor = null;
            File.Move(result, Path.Combine(retained, worker.ToString("N") + "-settled.json"));
            actor = Start(); await Ready(actor); Assert.Equal("Idle", (await Result(worker, actor)).Outcome);
            using (var recovered = Client()) Assert.Equal(HttpStatusCode.OK, await Adjust(recovered));
            await using (var read = await sessions.BeginReadOnlyAsync(Ct))
            {
                Assert.True(await read.QuerySingleAsync(new DbStatement("SELECT balance=101.25 AND version=4 FROM shop.wallets WHERE user_id=$1", DbValue.Uuid(state.User)), r => r.GetBoolean(0), Ct));
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.payments WHERE status='settled'"), r => r.GetInt64(0), Ct));
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='verify_payment' AND status='succeeded' AND attempts=1"), r => r.GetInt64(0), Ct));
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest' AND status='pending'"), r => r.GetInt64(0), Ct));
                Assert.Equal(4L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), Ct));
            }
            Guid status = Guid.NewGuid(); Request(status, "status"); var final = await Result(status, actor); Assert.Equal((1L, 1L, 1L), (final.Orders, final.Allocated, final.Debits)); await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, data, actor); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialDeliveryCrashRetainsManifestAndNeverBlindlyResendsANewMessage(bool savedAnchor)
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data); var context = CheckoutFixture.ResolveContexts()[0]; var owner = await Admin(sessions); var controls = new BrandWriterStore(sessions); using var secrets = SecretFiles.Load(state.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true)); var store = new DeliveryStore(sessions, secrets);
            actor = Start(); await Ready(actor); Guid order = Guid.NewGuid(); Request(order, "wallet"); Assert.Equal("Purchased", (await Result(order, actor)).Outcome);
            if (savedAnchor)
            {
                await using var anchor = await sessions.BeginAsync(Ct);
                await anchor.ExecuteAsync(new DbStatement("UPDATE shop.users SET flow_state='payment',flow_order_id=$1,flow_product_id=$2,flow_anchor_message_id=750,flow_expires_at=clock_timestamp()+INTERVAL '1 hour',flow_revision=flow_revision+1 WHERE id=$3", DbValue.Uuid(order), DbValue.Uuid(state.Product), DbValue.Uuid(state.User)), Ct);
                byte[] secret = Enumerable.Repeat((byte)7, 32).ToArray(); using var verifier = new TelegramWebhookVerifier(context, secret);
                var update = verifier.Verify([TelegramFixture.Header(secret)], TelegramFixture.Body("order", true, messageId: 750));
                await OrderDeliveryRouter.RecordAnchorAsync(anchor, update, order, Ct); await anchor.CommitAsync(Ct);
            }
            Assert.Equal(new(false, 1), await controls.SetActiveAsync(owner, context, 0, false, Guid.NewGuid(), Ct));
            string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            Guid prepare = Guid.NewGuid(); Request(prepare, "manifest-worker"); Assert.Equal("Rejected", (await Result(prepare, actor)).Outcome); Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
            Assert.Equal(new(true, 2), await controls.SetActiveAsync(owner, context, 1, true, Guid.NewGuid(), Ct));
            before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct); Guid stale = Guid.NewGuid(); Request(stale, "manifest-worker"); Assert.Equal("Rejected", (await Result(stale, actor)).Outcome); Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
            await Stop(actor); actor.Dispose(); actor = null; state = state with { WriterRevision = 2 }; CutoverStageRuntime.Write(Path.Combine(Root, "state.json"), JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState), true);
            string retained = Path.Combine(Root, "retained-results"); Directory.CreateDirectory(retained, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(Path.Combine(Root, "results", prepare.ToString("N") + ".json"), Path.Combine(retained, prepare.ToString("N") + ".json"));
            actor = Start(); await Ready(actor); Assert.Equal(savedAnchor ? "Prepared" : "PreparedWithoutSend", (await Result(prepare, actor)).Outcome);
            if (!savedAnchor) await store.AuthorizeSendRecoveryAsync(context, owner, order, Guid.NewGuid(), Ct);
            byte[] original = await store.ReadManifestAsync(context, CheckoutFixture.Actor(0), order, Ct);
            Guid held = Guid.NewGuid(); Request(held, "delivery-held"); string heldPath = Path.Combine(Root, "dispatch-" + held.ToString("N") + ".json");
            await Until(() => Task.FromResult(File.Exists(heldPath)), actor);
            var attempted = JsonSerializer.Deserialize(File.ReadAllBytes(heldPath), CutoverStageJson.Default.CutoverDeliveryAttempt)!;
            Assert.Equal(state.Run, attempted.Run); Assert.Equal(order, attempted.Order); Assert.Equal<long?>(savedAnchor ? 750L : null, attempted.Target); Assert.False(attempted.File); Assert.Equal(1L, attempted.Attempt);
            actor.Kill(true); await actor.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct); Assert.NotEqual(0, actor.ExitCode); actor.Dispose(); actor = null;
            Assert.False(File.Exists(Path.Combine(Root, "results", held.ToString("N") + ".json")));
            string interrupted = Path.Combine(Root, "retained-interrupted"); Directory.CreateDirectory(interrupted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); File.Move(Path.Combine(Root, "requests", held.ToString("N") + ".request"), Path.Combine(interrupted, held.ToString("N") + ".request"));
            await using (var expire = await sessions.BeginAsync(Ct))
            { Assert.Equal(1, await expire.ExecuteAsync(new DbStatement("UPDATE shop.jobs SET lease_until=clock_timestamp()-INTERVAL '1 second' WHERE id=$1 AND status='leased'", DbValue.Uuid(attempted.Job)), Ct)); await expire.CommitAsync(Ct); }
            actor = Start(); await Ready(actor); Guid recover = Guid.NewGuid(); Request(recover, "delivery-recover"); Assert.Equal("Recovered", (await Result(recover, actor)).Outcome);
            Guid retry = Guid.NewGuid(); Request(retry, "delivery-worker"); Assert.Equal(savedAnchor ? "Delivered" : "Idle", (await Result(retry, actor)).Outcome);
            if (savedAnchor)
            {
                var completed = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(Root, "dispatch-" + retry.ToString("N") + ".json")), CutoverStageJson.Default.CutoverDeliveryAttempt)!;
                Assert.Equal(attempted.Delivery, completed.Delivery); Assert.Equal(attempted.Generation, completed.Generation); Assert.Equal(attempted.Job, completed.Job); Assert.Equal(attempted.PayloadSha256, completed.PayloadSha256); Assert.Equal(750L, completed.Target); Assert.Equal(2L, completed.Attempt);
            }
            Guid repeat = Guid.NewGuid(); Request(repeat, "delivery-worker"); Assert.Equal("Idle", (await Result(repeat, actor)).Outcome);
            Assert.Equal(savedAnchor ? 2 : 1, Directory.GetFiles(Root, "dispatch-*.json").Length);
            byte[] recovered = await store.ReadManifestAsync(context, CheckoutFixture.Actor(0), order, Ct); Assert.Equal(original, recovered); CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(recovered);
            await using (var read = await sessions.BeginReadOnlyAsync(Ct))
            {
                Assert.True(await read.QuerySingleAsync(new DbStatement("SELECT balance=90 FROM shop.wallets WHERE user_id=$1", DbValue.Uuid(state.User)), r => r.GetBoolean(0), Ct));
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status=$1", DbValue.Text(savedAnchor ? "succeeded" : "uncertain")), r => r.GetInt64(0), Ct));
                Assert.Equal(savedAnchor ? 1L : 0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'"), r => r.GetInt64(0), Ct));
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest' AND status='succeeded'"), r => r.GetInt64(0), Ct));
            }
            Guid status = Guid.NewGuid(); Request(status, "status"); var final = await Result(status, actor); Assert.Equal((1L, 1L, 1L), (final.Orders, final.Allocated, final.Debits)); await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, data, actor); }
    }

    [Theory]
    [InlineData("database")]
    [InlineData("run")]
    [InlineData("keys")]
    public async Task StagingWriterRejectsHistoricalOrForeignBindingsBeforeReadiness(string mismatch)
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data);
            string before = await WorkflowActivityQualification.DomainAsync(sessions, true, Ct);
            var invalid = mismatch switch { "database" => state with { Database = "historical_read_only_copy" }, "run" => state with { Run = Guid.NewGuid() }, "keys" => state with { Secrets = "/outside-stage/original-keys" }, _ => throw new ArgumentException() };
            CutoverStageRuntime.Write(Path.Combine(Root, "state.json"), JsonSerializer.Serialize(invalid, CutoverStageJson.Default.CutoverStageState), true);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct); limit.CancelAfter(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => CutoverStageRuntime.RunAsync(Root, "serve", limit.Token));
            Assert.False(File.Exists(Path.Combine(Root, "ready.json"))); Assert.False(File.Exists(Path.Combine(Root, "api.json")));
            Assert.Equal(before, await WorkflowActivityQualification.DomainAsync(sessions, true, Ct));
        }
        finally { await Cleanup(state, data, null); }
    }

    [Fact]
    public async Task CredentialWorkerReportsPersistedUncertaintyWithoutAnAutomaticSecondSend()
    {
        CutoverStageState? state = null; NpgsqlDataSource? data = null; Process? actor = null;
        try
        {
            Prepare(); await CutoverStageRuntime.RunAsync(Root, "init", Ct); state = State();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")) { Database = state.Database };
            data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(); var sessions = new DbSessionFactory(data);
            using var secrets = SecretFiles.Load(state.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
            var store = new DeliveryStore(sessions, secrets); var context = CheckoutFixture.ResolveContexts()[0];
            actor = Start(); await Ready(actor); Guid order = Guid.NewGuid(); Request(order, "wallet"); Assert.Equal("Purchased", (await Result(order, actor)).Outcome);
            Guid prepare = Guid.NewGuid(); Request(prepare, "manifest-worker"); Assert.Equal("PreparedWithoutSend", (await Result(prepare, actor)).Outcome);
            await store.AuthorizeSendRecoveryAsync(context, await Admin(sessions), order, Guid.NewGuid(), Ct);
            Guid unknown = Guid.NewGuid(); Request(unknown, "delivery-unknown"); Assert.Equal("Uncertain", (await Result(unknown, actor)).Outcome);
            Guid repeat = Guid.NewGuid(); Request(repeat, "delivery-worker"); Assert.Equal("Idle", (await Result(repeat, actor)).Outcome);
            Assert.Single(Directory.GetFiles(Root, "dispatch-*.json"));
            await using (var read = await sessions.BeginReadOnlyAsync(Ct))
            {
                Assert.Equal(1L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.telegram_deliveries WHERE purpose='order_credentials' AND status='uncertain'"), r => r.GetInt64(0), Ct));
                Assert.Equal(0L, await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders WHERE fulfillment_status='completed'"), r => r.GetInt64(0), Ct));
            }
            byte[] manifest = await store.ReadManifestAsync(context, CheckoutFixture.Actor(0), order, Ct); Assert.NotEmpty(manifest); CryptographicOperations.ZeroMemory(manifest);
            Guid status = Guid.NewGuid(); Request(status, "status"); var final = await Result(status, actor); Assert.Equal((1L, 1L, 1L), (final.Orders, final.Allocated, final.Debits)); await Stop(actor); actor.Dispose(); actor = null;
        }
        finally { await Cleanup(state, data, actor); }
    }
}
