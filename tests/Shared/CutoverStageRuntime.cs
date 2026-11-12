using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Platform.Persistence;
using Platform.SchemaRunner;
using Platform.Security;
namespace Platform.Testing;

public sealed record CutoverStageState(Guid Run, string Database, Guid Product, Guid User, string Secrets);
public sealed record CutoverStageResult(string Operation, string Outcome, long Orders, long Allocated, long Debits, bool Synthetic = true, bool ReleaseReady = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CutoverStageState))]
[JsonSerializable(typeof(CutoverStageResult))]
public partial class CutoverStageJson : JsonSerializerContext { }
public static class CutoverStageRuntime
{
    public static async Task RunAsync(string root, string mode, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || root != "/stage/target" || mode is not ("init" or "serve") || Environment.GetEnvironmentVariable("SHOP_CUTOVER_STAGE") != "synthetic-acc-abad-v1" || Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1") throw new InvalidOperationException();
        if (File.GetUnixFileMode(root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) throw new InvalidOperationException();
        string marker = ArchivePrivateFile.ReadText(Path.Combine(root, "identity")).Trim();
        if (!Guid.TryParseExact(marker, "N", out var run) || run == Guid.Empty) throw new InvalidOperationException();
        string path = Path.Combine(root, "state.json");
        if (mode == "init")
        {
            if (File.Exists(path) || Directory.EnumerateFileSystemEntries(Path.Combine(root, "requests")).Any() || Directory.EnumerateFileSystemEntries(Path.Combine(root, "results")).Any()) throw new InvalidOperationException();
            var f = await CheckoutFixture.CreateAsync(users: 1, stock: 3, ct: ct);
            if (!f.SecretRoot.StartsWith(root + "/tmp/shop-secret-qualification-", StringComparison.Ordinal)) throw new InvalidOperationException();
            await using (var edit = await f.Sessions.BeginAsync(ct))
            {
                await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET active=false,policy_version=policy_version+1 WHERE id<>$1", DbValue.Uuid(SyntheticSecretFiles.BrandA)), ct);
                await edit.CommitAsync(ct);
            }
            var state = new CutoverStageState(run, new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database!, f.Product, f.Users[0], f.SecretRoot);
            Write(path, JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState));
            f.Secrets.Dispose();
            return;
        }
        var saved = JsonSerializer.Deserialize(ArchivePrivateFile.Read(path, 1, 4096), CutoverStageJson.Default.CutoverStageState) ?? throw new InvalidOperationException();
        if (saved.Run != run || saved.Database.Length != 51 || !saved.Database.StartsWith("shop_qualification_", StringComparison.Ordinal) || !Guid.TryParseExact(saved.Database[19..], "N", out _) || !saved.Secrets.StartsWith(root + "/tmp/shop-secret-qualification-", StringComparison.Ordinal)) throw new InvalidOperationException();
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException());
        if (options.Host != "127.0.0.1" || options.Database != "shop_test_control" || options.Username != "shop_test_runner") throw new InvalidOperationException();
        options.Database = saved.Database; options.IncludeErrorDetail = false; options.MaxPoolSize = 4;
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        await FoundationQualification.VerifySchemaAsync(data);
        using var secrets = SecretFiles.Load(saved.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
        var sessions = new DbSessionFactory(data);
        var context = CheckoutFixture.ResolveContexts()[0];
        await using (var read = await sessions.BeginReadOnlyAsync(ct))
        {
            long active = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.brands WHERE active"), r => r.GetInt64(0), ct);
            var member = await BrandUserStore.RequireAsync(read, context, CheckoutFixture.Actor(0), saved.User, ct);
            if (active != 1 || member.Id != saved.User) throw new InvalidOperationException();
        }
        Write(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(await Result("ready", "ready"), CutoverStageJson.Default.CutoverStageResult), true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(20));
        while (!deadline.IsCancellationRequested)
        {
            string[] requests = Directory.GetFileSystemEntries(Path.Combine(root, "requests"));
            if (requests.Length > 64) throw new InvalidOperationException();
            foreach (string request in requests.Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(request);
                if (!name.EndsWith(".request", StringComparison.Ordinal) || !Guid.TryParseExact(name[..^8], "N", out var id) || id == Guid.Empty) throw new InvalidOperationException();
                string result = Path.Combine(root, "results", id.ToString("N") + ".json");
                if (File.Exists(result)) continue;
                byte[] bytes = ArchivePrivateFile.Read(request, 1, 128); string command;
                try { command = new UTF8Encoding(false, true).GetString(bytes).Trim(); } finally { CryptographicOperations.ZeroMemory(bytes); }
                if (command is not ("status" or "wallet" or "stop" or "fake_payment" or "fake_supplier" or "fake_telegram" or "fake_fx" or "fake_ai")) throw new InvalidOperationException();
                string outcome = command.StartsWith("fake_", StringComparison.Ordinal) ? "simulated" : "ready";
                if (command == "wallet")
                {
                    var accepted = await new CheckoutStore(sessions, secrets).PurchaseLocalAsync(context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10), ct);
                    outcome = accepted.Outcome.ToString();
                }
                Write(result, JsonSerializer.Serialize(await Result(command, outcome), CutoverStageJson.Default.CutoverStageResult));
                if (command == "stop") return;
            }
            await Task.Delay(100, deadline.Token);
        }
        async Task<CutoverStageResult> Result(string operation, string outcome)
        {
            await using var read = await sessions.BeginReadOnlyAsync(ct);
            return await read.QuerySingleAsync(new DbStatement("SELECT (SELECT count(*) FROM shop.orders),(SELECT count(*) FROM shop.inventory_items WHERE state='allocated'),(SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit')"), r => new CutoverStageResult(operation, outcome, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ct);
        }
    }
    private static void Write(string path, string value, bool replace = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
        { byte[] bytes = Encoding.UTF8.GetBytes(value + "\n"); output.Write(bytes); output.Flush(true); }
        File.Move(temporary, path, replace);
    }
}

