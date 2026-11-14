using System.Diagnostics;
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
}
