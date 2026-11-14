using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Platform.Persistence;
using Platform.SchemaRunner;
using Platform.Security;
using Platform.Telegram;
using Platform.Core;
namespace Platform.Testing;

public sealed record CutoverStageState(Guid Run, string Database, Guid Product, Guid User, string Secrets, long WriterRevision = 0);
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
        if (!OperatingSystem.IsLinux() || root != "/stage/target" || mode is not ("init" or "serve" or "stock-init" or "telegram-init" or "payment-init" or "payment-verify" or "payment-audit") || Environment.GetEnvironmentVariable("SHOP_CUTOVER_STAGE") != "synthetic-acc-abad-v1" || Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1") throw new InvalidOperationException();
        if (File.GetUnixFileMode(root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) throw new InvalidOperationException();
        string marker = ArchivePrivateFile.ReadText(Path.Combine(root, "identity")).Trim();
        if (!Guid.TryParseExact(marker, "N", out var run) || run == Guid.Empty) throw new InvalidOperationException();
        if (mode == "payment-verify")
        {
            var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
            var input = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-verification", "input.json"), 1, 8192), CutoverPaymentJson.Default.CutoverPaymentInput) ?? throw new InvalidOperationException();
            bool verified = false;
            try { using var notice = CutoverPaymentCapture.Verify(run, key, input); verified = true; }
            catch (Exception error) when (error is PaymentNoticeRejectedException or ArgumentException or FormatException) { }
            var result = new CutoverPaymentVerification(run, input.Nonce, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Body))), input.Event, verified);
            Write(Path.Combine(root, "payment-verification", "result.json"), JsonSerializer.Serialize(result, CutoverPaymentJson.Default.CutoverPaymentVerification));
            return;
        }
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
        if (saved.WriterRevision < 0 || saved.Run != run || saved.Database.Length != 51 || !saved.Database.StartsWith("shop_qualification_", StringComparison.Ordinal) || !Guid.TryParseExact(saved.Database[19..], "N", out _) || !saved.Secrets.StartsWith(root + "/tmp/shop-secret-qualification-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (mode == "payment-audit")
        {
            using var keys = SecretFiles.Load(saved.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
            var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
            var files = new CutoverPaymentFiles(Path.Combine(root, "payment-archives"), keys, CheckoutFixture.ResolveContexts()[0], key, new(run, Guid.NewGuid(), "", "", ""));
            if (key.Run != run || key.Member != saved.User) throw new InvalidOperationException();
            var paths = Directory.GetFiles(Path.Combine(root, "payment-archives"), "*.json").Order(StringComparer.Ordinal).ToArray();
            if (paths.Length is < 1 or > 64) throw new InvalidOperationException();
            var hashes = new StringBuilder();
            foreach (string archive in paths)
            {
                byte[] plain = files.Read(archive);
                try { hashes.Append(Path.GetFileName(archive)).Append(':').Append(Convert.ToHexStringLower(SHA256.HashData(ArchivePrivateFile.Read(archive, 1, 8192)))).Append('\n'); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Write(Path.Combine(root, "payment-audit-result.json"), JsonSerializer.Serialize(new CutoverPaymentAudit(run, paths.Length, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashes.ToString())))), CutoverPaymentJson.Default.CutoverPaymentAudit));
            return;
        }
        var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException());
        if (options.Host != "127.0.0.1" || options.Database != "shop_test_control" || options.Username != "shop_test_runner") throw new InvalidOperationException();
        options.ApplicationName = "t068-cutover-stage"; options.Database = saved.Database; options.IncludeErrorDetail = false; options.MaxPoolSize = 4;
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        await FoundationQualification.VerifySchemaAsync(data);
        using var secrets = SecretFiles.Load(saved.Secrets, "service-config.json", new(SecretService.JobHost, SyntheticSecretFiles.Deployment, 7, CheckoutFixture.BrandIds, true));
        var sessions = new DbSessionFactory(data);
        var context = CheckoutFixture.ResolveContexts()[0];
        if (mode == "payment-init")
        {
            if (File.Exists(Path.Combine(root, "payment-key.json"))) throw new InvalidOperationException();
            foreach (string directory in new[] { "payment", "payment-archives", "payment-verification" })
            { Directory.CreateDirectory(Path.Combine(root, directory), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); if (Directory.EnumerateFileSystemEntries(Path.Combine(root, directory)).Any()) throw new InvalidOperationException(); }
            await using (var policy = await sessions.BeginAsync(ct))
            { await policy.QuerySingleAsync(new DbStatement("UPDATE shop.brands SET allow_crypto=true,allow_wallet_topup=true,policy_version=policy_version+1 WHERE id=$1 AND active AND policy_version=$2 RETURNING policy_version", DbValue.Uuid(context.BrandId), DbValue.Bigint(saved.WriterRevision)), r => r.GetInt64(0), ct); await policy.CommitAsync(ct); }
            saved = saved with { WriterRevision = checked(saved.WriterRevision + 1) };
            Write(path, JsonSerializer.Serialize(saved, CutoverStageJson.Default.CutoverStageState), true);
            Guid payment = Guid.NewGuid(); var store = new PaymentEvidenceStore(sessions, secrets);
            _ = await store.CreateTopupAsync(context, CheckoutFixture.Actor(0), payment, new(10.25m, Currency.Usd), CryptoAsset.UsdtBsc, ct);
            string provider = PaymentSettlementFixture.Provider(payment);
            await using (var bind = await sessions.BeginAsync(ct))
            { await store.BindInvoiceAsync(bind, context, CheckoutFixture.Actor(0), payment, provider, 10.4m, ct); await bind.CommitAsync(ct); }
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            try { Write(Path.Combine(root, "payment-key.json"), JsonSerializer.Serialize(new CutoverPaymentKey(run, payment, saved.User, "topup:" + payment.ToString("D"), provider, Convert.ToHexStringLower(secret)), CutoverPaymentJson.Default.CutoverPaymentKey)); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            return;
        }
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
        string ready = Path.Combine(root, "ready.json");
        if (File.Exists(ready)) { _ = ArchivePrivateFile.Read(ready, 1, 4096); File.Delete(ready); }
        sessions = new WriterRegistry(sessions, [new(context, saved.WriterRevision)]).ForBrand(context);
        await using (var admission = await sessions.BeginAsync(ct))
        { await admission.AdmitWriterAsync(context, ct); await admission.CommitAsync(ct); }
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
                if (command is not ("status" or "wallet" or "wallet-paused" or "telegram" or "payment" or "stop" or "fake_payment" or "fake_supplier" or "fake_telegram" or "fake_fx" or "fake_ai")) throw new InvalidOperationException();
                string outcome = command.StartsWith("fake_", StringComparison.Ordinal) ? "simulated" : "ready";
                if (command == "payment")
                {
                    var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
                    try
                    {
                        var input = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment", id.ToString("N") + ".json"), 1, 8192), CutoverPaymentJson.Default.CutoverPaymentInput) ?? throw new InvalidOperationException();
                        var files = new CutoverPaymentFiles(Path.Combine(root, "payment-archives"), secrets, context, key, input);
                        outcome = await CutoverPaymentCapture.CaptureAsync(sessions, secrets, context, saved.User, run, key, input, files.SaveAsync, ct);
                    }
                    catch (Exception error) when (error is PaymentNoticeRejectedException or UnauthorizedAccessException or ArgumentException or JsonException) { outcome = "Rejected"; }
                }
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
                    try { outcome = (await new CheckoutStore(sessions, secrets).PurchaseLocalAsync(context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10), ct)).Outcome.ToString(); }
                    catch (UnauthorizedAccessException) { outcome = "Rejected"; }
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
    internal static void Write(string path, string value, bool replace = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
        { byte[] bytes = Encoding.UTF8.GetBytes(value + "\n"); output.Write(bytes); output.Flush(true); }
        File.Move(temporary, path, replace);
    }
}
