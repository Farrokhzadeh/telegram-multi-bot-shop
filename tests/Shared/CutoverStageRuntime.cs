using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Platform.Persistence;
using Platform.SchemaRunner;
using Platform.Security;
using Platform.Telegram;
namespace Platform.Testing;

public sealed record CutoverStageState(Guid Run, string Database, Guid Product, Guid User, string Secrets);
public sealed record CutoverStockPlan(Guid Run, Guid Product, Guid Unit, string Credential);
public sealed record CutoverStockBinding(Guid Product, Guid Unit, string Cipher, string Plain);
public sealed record CutoverStageResult(string Operation, string Outcome, long Orders, long Allocated, long Debits, bool Synthetic = true, bool ReleaseReady = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CutoverStageState))]
[JsonSerializable(typeof(CutoverStockPlan))]
[JsonSerializable(typeof(CutoverStockBinding))]
[JsonSerializable(typeof(CutoverStageResult))]
public partial class CutoverStageJson : JsonSerializerContext { }
public static class CutoverStageRuntime
{
    public static async Task RunAsync(string root, string mode, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || root != "/stage/target" || mode is not ("init" or "serve" or "stock-init" or "telegram-init") || Environment.GetEnvironmentVariable("SHOP_CUTOVER_STAGE") != "synthetic-acc-abad-v1" || Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1") throw new InvalidOperationException();
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
        options.ApplicationName = "t068-cutover-stage"; options.Database = saved.Database; options.IncludeErrorDetail = false; options.MaxPoolSize = 4;
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        await FoundationQualification.VerifySchemaAsync(data);
        using var secrets = SecretFiles.Load(saved.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
        var sessions = new DbSessionFactory(data);
        var context = CheckoutFixture.ResolveContexts()[0];
        if (mode == "stock-init")
        {
            var plan = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "stock-plan.json"), 1, 1024), CutoverStageJson.Default.CutoverStockPlan) ?? throw new InvalidOperationException();
            if (plan.Run != run || plan.Product == Guid.Empty || plan.Unit == Guid.Empty || plan.Credential != "SYNTHETIC-COMMON-" + run.ToString("N") + "-" + plan.Unit.ToString("N")) throw new InvalidOperationException();
            await using var seed = await sessions.BeginAsync(ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate','SYNTHETIC COMMON CUTOVER') ON CONFLICT DO NOTHING", DbValue.Uuid(plan.Product)), ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brand_products(brand_id,product_id,visible,price_usdt) VALUES ($1,$2,true,10) ON CONFLICT DO NOTHING", DbValue.Uuid(context.BrandId), DbValue.Uuid(plan.Product)), ct);
            var inventory = new InventoryCipher(secrets); byte[] plain = Encoding.UTF8.GetBytes(plan.Credential);
            try
            {
                var rows = await seed.QueryManyAsync(new DbStatement("SELECT credential_ciphertext,credential_key_id,product_id,state FROM shop.inventory_items WHERE id=$1", DbValue.Uuid(plan.Unit)), r => (Bytes: r.GetBinary(0), Key: r.GetString(1), Product: r.GetGuid(2), State: r.GetString(3)), 1, ct);
                byte[] encrypted;
                if (rows.Count == 0)
                {
                    encrypted = inventory.Encrypt(plan.Unit, plain);
                    await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind) VALUES ($1,$2,$3,'current',$4,'local_import')", DbValue.Uuid(plan.Unit), DbValue.Uuid(plan.Product), DbValue.Binary(encrypted), DbValue.Binary(SHA256.HashData(plain))), ct);
                }
                else { if (rows[0].Product != plan.Product || rows[0].State != "available") throw new InvalidOperationException(); encrypted = rows[0].Bytes; }
                byte[] check = inventory.Decrypt(plan.Unit, "current", encrypted);
                try { if (!CryptographicOperations.FixedTimeEquals(plain, check)) throw new InvalidOperationException(); } finally { CryptographicOperations.ZeroMemory(check); }
                await seed.CommitAsync(ct);
                Write(Path.Combine(root, "stock-binding.json"), JsonSerializer.Serialize(new CutoverStockBinding(plan.Product, plan.Unit, Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant()), CutoverStageJson.Default.CutoverStockBinding), true);
                Write(path, JsonSerializer.Serialize(saved with { Product = plan.Product }, CutoverStageJson.Default.CutoverStageState), true);
                return;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        if (mode == "telegram-init")
        {
            string keyPath = Path.Combine(root, "telegram-key.json");
            if (File.Exists(keyPath)) throw new InvalidOperationException();
            Directory.CreateDirectory(Path.Combine(root, "telegram"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.EnumerateFileSystemEntries(Path.Combine(root, "telegram")).Any()) throw new InvalidOperationException();
            await using var seed = await sessions.BeginAsync(ct);
            var member = await BrandUserStore.RequireAsync(seed, context, CheckoutFixture.Actor(0), saved.User, ct);
            _ = await BrandUserStore.TransitionAsync(seed, context, CheckoutFixture.Actor(0), saved.User, member.Flow.Revision, CustomerFlowState.Catalog, anchorMessageId: 77, ct: ct);
            await seed.CommitAsync(ct);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            try { Write(keyPath, JsonSerializer.Serialize(new CutoverTelegramKey(run, Convert.ToHexStringLower(key)), CutoverTelegramJson.Default.CutoverTelegramKey)); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return;
        }
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
                if (command is not ("status" or "wallet" or "wallet-paused" or "telegram" or "stop" or "fake_payment" or "fake_supplier" or "fake_telegram" or "fake_fx" or "fake_ai")) throw new InvalidOperationException();
                string outcome = command.StartsWith("fake_", StringComparison.Ordinal) ? "simulated" : "ready";
                if (command == "telegram")
                {
                    var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "telegram-key.json"), 1, 256), CutoverTelegramJson.Default.CutoverTelegramKey) ?? throw new InvalidOperationException();
                    if (key.Run != run || key.Secret.Length != 64) throw new InvalidOperationException();
                    byte[] secret = Convert.FromHexString(key.Secret);
                    try
                    {
                        var input = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "telegram", id.ToString("N") + ".json"), 1, 8192), CutoverTelegramJson.Default.CutoverTelegramInput) ?? throw new InvalidOperationException();
                        outcome = (await CutoverTelegramRouter.RouteAsync(sessions, context, saved.User, run, secret, input, ct)).Code.ToString();
                    }
                    catch (Exception error) when (error is TelegramIngressRejectedException or UnauthorizedAccessException or ArgumentException or JsonException) { outcome = "Rejected"; }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                if (command == "wallet")
                {
                    var accepted = await new CheckoutStore(sessions, secrets).PurchaseLocalAsync(context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10), ct);
                    outcome = accepted.Outcome.ToString();
                }
                if (command == "wallet-paused")
                {
                    await using var transaction = await sessions.BeginAsync(ct);
                    var held = await new CheckoutStore(sessions, secrets).AcceptLocalAsync(transaction, context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10), ct);
                    if (held.Outcome != CheckoutOutcome.Purchased) throw new InvalidOperationException();
                    Write(Path.Combine(root, "held-" + id.ToString("N") + ".json"), JsonSerializer.Serialize(await Result(command, "transaction-held"), CutoverStageJson.Default.CutoverStageResult));
                    await Task.Delay(TimeSpan.FromMinutes(2), ct);
                    throw new InvalidOperationException();
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
