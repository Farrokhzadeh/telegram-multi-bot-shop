using Platform.Fulfillment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Api;
using Platform.Payments;
using System.Globalization;
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

public sealed record CutoverStageState(Guid Run, string Database, Guid Product, Guid User, long WriterRevision = 0);
public sealed record CutoverDeliveryAttempt(Guid Run, Guid Delivery, Guid Order, Guid Generation, Guid Job, long Attempt, long? Target, bool File, string PayloadSha256);
public sealed record CutoverStageApi(Guid Run, string Address);
public sealed record CutoverStockPlan(Guid Run, Guid Product, Guid Unit, string Credential);
public sealed record CutoverStockBinding(Guid Product, Guid Unit, string Content);
public sealed record CutoverStageResult(string Operation, string Outcome, long Orders, long Allocated, long Debits, bool Synthetic = true, bool ReleaseReady = false);
public sealed record CutoverPilotResult(Guid Run, Guid Product, string Outcome, string? Title = null, int? Minimum = null, int? Maximum = null,
    int? Quantity = null, decimal? Amount = null, string? Currency = null, Guid? Order = null,
    decimal? Balance = null, IReadOnlyList<CutoverPilotOrder>? Orders = null, IReadOnlyList<string>? Items = null,
    CustomerHome? Home = null, CustomerScreenPage? Page = null, CustomerScreenEntry? Entry = null, string? Description = null, long? Stock = null, string? Icon = null);
public sealed record CutoverPilotOrder(Guid Id, string Title, int Quantity, string Status);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CutoverPilotResult))]
[JsonSerializable(typeof(CutoverStageState))]
[JsonSerializable(typeof(CutoverStageApi))]
[JsonSerializable(typeof(CutoverDeliveryAttempt))]
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
            await using (var edit = await f.Sessions.BeginAsync(ct))
            {
                await edit.ExecuteAsync(new DbStatement("UPDATE shop.brands SET active=false,policy_version=policy_version+1 WHERE id<>$1", DbValue.Uuid(SyntheticSecretFiles.BrandA)), ct);
                await edit.CommitAsync(ct);
            }
            var state = new CutoverStageState(run, new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database!, f.Product, f.Users[0]);
            Write(path, JsonSerializer.Serialize(state, CutoverStageJson.Default.CutoverStageState));
            f.Secrets.Dispose();
            return;
        }
        var saved = JsonSerializer.Deserialize(ArchivePrivateFile.Read(path, 1, 4096), CutoverStageJson.Default.CutoverStageState) ?? throw new InvalidOperationException();
        if (saved.WriterRevision < 0 || saved.Run != run || saved.Database.Length != 51 || !saved.Database.StartsWith("shop_qualification_", StringComparison.Ordinal) || !Guid.TryParseExact(saved.Database[19..], "N", out _)) throw new InvalidOperationException();
        if (mode == "payment-audit")
        {
            var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
            var files = new CutoverPaymentFiles(Path.Combine(root, "payment-archives"), CheckoutFixture.ResolveContexts()[0], key, new(run, Guid.NewGuid(), "", "", ""));
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
        await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).EnableArrays().Build();
        await FoundationQualification.VerifySchemaAsync(data);
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
            Guid payment = Guid.NewGuid(); var store = new PaymentEvidenceStore(sessions);
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
            byte[] content = Encoding.UTF8.GetBytes(plan.Credential);
            try
            {
                var rows = await seed.QueryManyAsync(new DbStatement("SELECT credential_content,product_id,state FROM shop.inventory_items WHERE id=$1", DbValue.Uuid(plan.Unit)), r => (Bytes: r.GetBinary(0), Product: r.GetGuid(1), State: r.GetString(2)), 1, ct);
                if (rows.Count == 0)
                    await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.inventory_items(id,product_id,credential_content,source_fingerprint,origin_kind) VALUES ($1,$2,$3,$4,'local_import')", DbValue.Uuid(plan.Unit), DbValue.Uuid(plan.Product), DbValue.Binary(content), DbValue.Binary(SHA256.HashData(content))), ct);
                else if (rows[0].Product != plan.Product || rows[0].State != "available" || !CryptographicOperations.FixedTimeEquals(rows[0].Bytes, content)) throw new InvalidOperationException();
                await seed.CommitAsync(ct);
                Write(Path.Combine(root, "stock-binding.json"), JsonSerializer.Serialize(new CutoverStockBinding(plan.Product, plan.Unit, Convert.ToHexStringLower(SHA256.HashData(content))), CutoverStageJson.Default.CutoverStockBinding), true);
                Write(path, JsonSerializer.Serialize(saved with { Product = plan.Product }, CutoverStageJson.Default.CutoverStageState), true);
                return;
            }
            finally { CryptographicOperations.ZeroMemory(content); }
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
        foreach (string descriptor in new[] { ready, Path.Combine(root, "api.json") })
        { if (File.Exists(descriptor)) { _ = ArchivePrivateFile.Read(descriptor, 1, 4096); File.Delete(descriptor); } }
        var writers = new WriterRegistry(sessions, [new(context, saved.WriterRevision)]); sessions = writers.ForBrand(context);
        await using (var admission = await sessions.BeginAsync(ct))
        { await admission.AdmitWriterAsync(context, ct); await admission.CommitAsync(ct); }
        await using (var read = await sessions.BeginReadOnlyAsync(ct))
        {
            long active = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.brands WHERE active"), r => r.GetInt64(0), ct);
            var member = await BrandUserStore.RequireAsync(read, context, CheckoutFixture.Actor(0), saved.User, ct);
            if (active != 1 || member.Id != saved.User) throw new InvalidOperationException();
        }
        var catalog = new CatalogStore(sessions, [], (_, _) => throw new InvalidOperationException("Only synthetic local catalog operations are configured."));
        using var administration = new BrandAdministrationServices(writers, context, catalog, new byte[32]);
        await using var api = await StartApiAsync(writers, administration, sessions, ct);
        Write(Path.Combine(root, "api.json"), JsonSerializer.Serialize(new CutoverStageApi(run, api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), CutoverStageJson.Default.CutoverStageApi), true);
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
                if (command.StartsWith("pilot-", StringComparison.Ordinal))
                {
                    CutoverPilotResult pilot = await PilotAsync(id, command);
                    Write(result, JsonSerializer.Serialize(pilot, CutoverStageJson.Default.CutoverPilotResult));
                    if (command.StartsWith("pilot-screen:", StringComparison.Ordinal)) File.Delete(request);
                    continue;
                }
                if (command is not ("status" or "wallet" or "wallet-current" or "wallet-paused" or "telegram" or "payment" or "payment-worker" or "manifest-worker" or "delivery-worker" or "delivery-held" or "delivery-unknown" or "delivery-recover" or "stop" or "fake_payment" or "fake_supplier" or "fake_telegram" or "fake_fx" or "fake_ai")) throw new InvalidOperationException();
                string outcome = command.StartsWith("fake_", StringComparison.Ordinal) ? "simulated" : "ready";
                if (command == "payment")
                {
                    var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
                    try
                    {
                        var input = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment", id.ToString("N") + ".json"), 1, 8192), CutoverPaymentJson.Default.CutoverPaymentInput) ?? throw new InvalidOperationException();
                        var files = new CutoverPaymentFiles(Path.Combine(root, "payment-archives"), secrets, context, key, input);
                        outcome = await CutoverPaymentCapture.CaptureAsync(sessions, context, saved.User, run, key, input, files.SaveAsync, ct);
                    }
                    catch (Exception error) when (error is PaymentNoticeRejectedException or UnauthorizedAccessException or ArgumentException or JsonException) { outcome = "Rejected"; }
                }

                if (command == "payment-worker")
                {
                    try
                    {
                        var key = JsonSerializer.Deserialize(ArchivePrivateFile.Read(Path.Combine(root, "payment-key.json"), 1, 1024), CutoverPaymentJson.Default.CutoverPaymentKey) ?? throw new InvalidOperationException();
                        if (key.Run != run || key.Member != saved.User) throw new InvalidOperationException();
                        JobLease? lease;
                        await using (var claim = await sessions.BeginAsync(ct))
                        { lease = await OutboxStore.ClaimAsync(claim, JobKind.VerifyPayment, Guid.NewGuid(), TimeSpan.FromSeconds(60), ct); await claim.CommitAsync(ct); }
                        if (lease is null) outcome = "Idle";
                        else
                        {
                            using var provider = PaymentSettlementFixture.Client(request => request == "/v1/payment/" + key.Provider ? PaymentNoticeFixture.Body(key.Origin, quote: 10.25m, provider: key.Provider, asset: "usdtbsc") : throw new InvalidOperationException());
                            outcome = (await new PaymentVerificationWorker(sessions, new PaymentSettlementStore(sessions, catalog), provider).RunForBrandAsync(context, saved.WriterRevision, lease, ct))?.Outcome.ToString() ?? "Rejected";
                        }
                    }
                    catch (UnauthorizedAccessException) { outcome = "Rejected"; }
                }


                if (command is "manifest-worker" or "delivery-worker" or "delivery-held" or "delivery-unknown" or "delivery-recover")
                {
                    try
                    {
                        var store = new DeliveryStore(sessions);
                        if (command == "delivery-recover")
                        {
                            IReadOnlyList<Guid> deliveries;
                            await using (var read = await sessions.BeginReadOnlyAsync(ct))
                                deliveries = await read.QueryManyAsync(new DbStatement("SELECT d.id FROM shop.telegram_deliveries d WHERE d.brand_id=$1 AND d.user_id=$2 AND d.purpose='order_credentials' AND d.status='leased' AND EXISTS(SELECT 1 FROM shop.jobs j WHERE j.telegram_delivery_id=d.id AND j.status='leased' AND j.lease_until<=clock_timestamp()) ORDER BY d.id", DbValue.Uuid(context.BrandId), DbValue.Uuid(saved.User)), r => r.GetGuid(0), 64, ct);
                            foreach (Guid delivery in deliveries) await store.RecoverExpiredAsync(context, delivery, ct);
                            outcome = "Recovered";
                        }
                        else
                        {
                            JobLease? lease;
                            await using (var claim = await sessions.BeginAsync(ct))
                            { lease = await OutboxStore.ClaimAsync(claim, command == "manifest-worker" ? JobKind.PrepareManifest : JobKind.SendTelegram, Guid.NewGuid(), TimeSpan.FromSeconds(60), ct); await claim.CommitAsync(ct); }
                            if (lease is null) outcome = "Idle";
                            else if (command == "manifest-worker") outcome = await store.PrepareAsync(context, lease, ct) is null ? "PreparedWithoutSend" : "Prepared";
                            else
                            {
                                await new LocalDeliveryWorker(store).RunAsync(context, lease, async (dispatch, token) =>
                                {
                                    if (dispatch.BotId != context.BotId || dispatch.ChatId != CheckoutFixture.Actor(0) || dispatch.IsFile || dispatch.Content.IsEmpty || dispatch.Content.Length > 4096) throw new InvalidOperationException();
                                    var attempt = new CutoverDeliveryAttempt(run, dispatch.Id, dispatch.Order, dispatch.Generation, lease.Id, lease.Attempt, dispatch.TargetMessageId, dispatch.IsFile, Convert.ToHexStringLower(SHA256.HashData(dispatch.Content.Span)));
                                    Write(Path.Combine(root, "dispatch-" + id.ToString("N") + ".json"), JsonSerializer.Serialize(attempt, CutoverStageJson.Default.CutoverDeliveryAttempt));
                                    if (command == "delivery-unknown") return new DeliveryOutcome(DeliveryOutcomeKind.Unknown);
                                    if (command == "delivery-held") { await Task.Delay(TimeSpan.FromMinutes(2), token); throw new InvalidOperationException(); }
                                    return new DeliveryOutcome(DeliveryOutcomeKind.Succeeded, dispatch.TargetMessageId ?? 761);
                                }, ct);
                                await using var read = await sessions.BeginReadOnlyAsync(ct);
                                outcome = await read.QuerySingleAsync(new DbStatement("SELECT CASE status WHEN 'succeeded' THEN 'Delivered' WHEN 'uncertain' THEN 'Uncertain' WHEN 'retry' THEN 'Retry' WHEN 'failed' THEN 'Rejected' WHEN 'cancelled' THEN 'Suppressed' ELSE 'NoDispatch' END FROM shop.telegram_deliveries WHERE id=$1 AND brand_id=$2 AND user_id=$3", DbValue.Uuid(lease.Intent.OriginId!.Value), DbValue.Uuid(context.BrandId), DbValue.Uuid(saved.User)), r => r.GetString(0), ct);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { outcome = "Rejected"; }
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
                if (command is "wallet" or "wallet-current")
                {
                    try { outcome = (await new CheckoutStore(sessions).PurchaseLocalAsync(context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10, PolicyRevision: command == "wallet-current" ? saved.WriterRevision : 0), ct)).Outcome.ToString(); }
                    catch (UnauthorizedAccessException) { outcome = "Rejected"; }
                }
                if (command == "wallet-paused")
                {
                    await using var transaction = await sessions.BeginAsync(ct);
                    var held = await new CheckoutStore(sessions).AcceptLocalAsync(transaction, context, CheckoutFixture.Actor(0), new(id, saved.Product, 1, 10), ct);
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
        async Task<CutoverPilotResult> PilotAsync(Guid id, string command)
        {
            const string quotePrefix = "pilot-quote:";
            const string buyPrefix = "pilot-buy:";
            long actor = CheckoutFixture.Actor(0);
            var denied = new CutoverPilotResult(run, saved.Product, "Unavailable");
            try
            {
                if (command.StartsWith("pilot-screen:", StringComparison.Ordinal))
                {
                    var screens = new CustomerScreens(sessions);
                    string[] parts = command.Split(':');
                    if (parts is [_, "home"])
                        return new(run, saved.Product, "Home", Home: await screens.HomeAsync(context, actor, ct));
                    if (parts is [_, "language", "fa" or "en"])
                    {
                        await screens.SetLanguageAsync(context, actor, parts[2], ct);
                        return new(run, saved.Product, "Home", Home: await screens.HomeAsync(context, actor, ct));
                    }
                    if (parts is [_, "detail", "Orders" or "Deposits", var rawId] &&
                        Guid.TryParseExact(rawId, "N", out Guid itemId))
                    {
                        var entry = await screens.DetailAsync(context, actor, Enum.Parse<CustomerScreenList>(parts[2]), itemId, ct);
                        return entry is null ? denied : new(run, saved.Product, "Detail", Entry: entry);
                    }
                    if (parts is [_, "list", "Orders" or "Transactions" or "Deposits" or "Discounts", "all" or "active" or "finished", var number] &&
                        int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
                        return new(run, saved.Product, "Page", Page: await screens.ListAsync(context, actor,
                            Enum.Parse<CustomerScreenList>(parts[2]), parts[3], page, ct));
                    return denied;
                }
                if (command == "pilot-wallet")
                {
                    await using var read = await sessions.BeginReadOnlyAsync(ct);
                    decimal balance = await read.QuerySingleAsync(new DbStatement("SELECT balance::text FROM shop.wallets WHERE user_id=$1",
                        DbValue.Uuid(saved.User)), row => decimal.Parse(row.GetString(0), CultureInfo.InvariantCulture), ct);
                    return new(run, saved.Product, "Wallet", Balance: balance);
                }
                if (command == "pilot-orders")
                {
                    await using var read = await sessions.BeginReadOnlyAsync(ct);
                    var orders = await read.QueryManyAsync(new DbStatement("""
                        SELECT id,product_title_snapshot,quantity,fulfillment_status
                        FROM shop.orders WHERE user_id=$1 ORDER BY created_at DESC,id DESC LIMIT 5
                        """, DbValue.Uuid(saved.User)),
                        row => new CutoverPilotOrder(row.GetGuid(0), row.GetString(1), row.GetInt32(2), row.GetString(3)), 5, ct);
                    return new(run, saved.Product, "Orders", Orders: orders);
                }
                const string revealPrefix = "pilot-reveal:";
                if (command.StartsWith(revealPrefix, StringComparison.Ordinal) &&
                    Guid.TryParseExact(command.AsSpan(revealPrefix.Length), "N", out Guid reveal))
                {
                    byte[] manifest = await new DeliveryStore(sessions).ReadManifestAsync(context, actor, reveal, ct);
                    try
                    {
                        using var stream = new MemoryStream(manifest, writable: false);
                        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                        if (reader.ReadInt32() != 1) return denied;
                        int count = reader.ReadInt32();
                        if (count is < 1 or > 3) return denied;
                        var items = new List<string>(count);
                        const string synthetic = "public synthetic credential ";
                        for (int unit = 1; unit <= count; unit++)
                        {
                            if (reader.ReadInt32() != unit || reader.ReadString() != "text" ||
                                reader.ReadInt32() != synthetic.Length + 36) return denied;
                            string value = Encoding.UTF8.GetString(reader.ReadBytes(synthetic.Length + 36));
                            if (!value.StartsWith(synthetic, StringComparison.Ordinal) ||
                                !Guid.TryParseExact(value.AsSpan(synthetic.Length), "D", out _)) return denied;
                            items.Add(value);
                        }
                        if (stream.Position != stream.Length) return denied;
                        return new(run, saved.Product, "Items", Order: reveal, Items: items);
                    }
                    finally { CryptographicOperations.ZeroMemory(manifest); }
                }
                if (command == "pilot-catalog")
                {
                    var item = await catalog.GetForBrandAsync(context, saved.WriterRevision, actor, saved.Product, ct);
                    return item is null ? denied : new(run, saved.Product,
                        item is { CanPurchase: true, Wallet: true } ? "Available" : "OutOfStock",
                        item.Presentation.Title, item.Minimum, item.Maximum, Amount: item.UnitUsdt, Currency: "USD",
                        Description: item.Presentation.Description, Stock: item.LocalStock, Icon: item.Presentation.Icon);
                }
                if (command.StartsWith(quotePrefix, StringComparison.Ordinal) &&
                    int.TryParse(command.AsSpan(quotePrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int quantity) &&
                    quantity is > 0 and <= 10000)
                {
                    var quote = await catalog.PrepareForBrandAsync(context, saved.WriterRevision, actor, new(saved.Product, quantity, CheckoutPayment.Wallet), ct);
                    return quote.Quote.Terms.Payment == CheckoutPayment.Wallet
                        ? new(run, saved.Product, "Quoted", quote.Quote.Terms.Product.Title, Quantity: quantity, Amount: quote.Quote.Payable.Amount,
                            Currency: quote.Quote.Payable.Currency.ToString(), Order: id)
                        : denied;
                }
                if (!command.StartsWith(buyPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(command.AsSpan(buyPrefix.Length), "N", out Guid order) || order == Guid.Empty || order == id)
                    return denied;
                string quotePath = Path.Combine(root, "results", order.ToString("N") + ".json");
                if (!File.Exists(quotePath)) return denied;
                var prior = JsonSerializer.Deserialize(ArchivePrivateFile.Read(quotePath, 1, 4096), CutoverStageJson.Default.CutoverPilotResult);
                if (prior is not { Outcome: "Quoted", Quantity: int units, Amount: decimal expected } ||
                    prior.Run != run || prior.Product != saved.Product || prior.Order != order || prior.Currency is null)
                    return denied;
                var store = new CheckoutStore(sessions);
                CheckoutRequest request = new(order, saved.Product, units);
                var replay = await store.ReplayForBrandAsync(context, saved.WriterRevision, actor, request, ct);
                if (replay is not null) return prior with { Outcome = replay.Outcome.ToString(), Order = replay.OrderId };
                var current = await catalog.PrepareForBrandAsync(context, saved.WriterRevision, actor, new(saved.Product, units, CheckoutPayment.Wallet), ct);
                if (current.Quote.Terms.Payment != CheckoutPayment.Wallet || current.Quote.Payable.Amount != expected ||
                    current.Quote.Payable.Currency.ToString() != prior.Currency)
                    return denied with { Outcome = "QuoteChanged" };
                BrandPolicySnapshot policy;
                await using (var read = await sessions.BeginAsync(ct))
                    policy = await BrandPolicyStore.ReadAsync(read, context, actor, ct);
                var access = await BrandPolicy.CheckChannelsAsync(policy, actor, static (_, _, _) => Task.FromResult(ChannelMembership.Unknown), ct);
                if (access is null) return denied;
                var accepted = await new WalletCheckoutService(sessions, store, catalog).AcceptForBrandAsync(context, saved.WriterRevision, actor, request, current, access, ct);
                return prior with { Outcome = accepted.Outcome.ToString(), Order = accepted.OrderId };
            }
            catch (Exception error) when (error is PricingRejectedException or DiscountQuantityException or UnauthorizedAccessException or JsonException or IOException or ArgumentOutOfRangeException)
            {
                return denied;
            }
        }

        async Task<CutoverStageResult> Result(string operation, string outcome)
        {
            await using var read = await sessions.BeginReadOnlyAsync(ct);
            return await read.QuerySingleAsync(new DbStatement("SELECT (SELECT count(*) FROM shop.orders),(SELECT count(*) FROM shop.inventory_items WHERE state='allocated'),(SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit')"), r => new CutoverStageResult(operation, outcome, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ct);
        }
    }

    private static async Task<WebApplication> StartApiAsync(WriterRegistry writers, BrandAdministrationServices services, DbSessionFactory sessions, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] }); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        var app = builder.Build();
        try
        {
            app.UseRouting(); app.UseMiddleware<DiagnosticsMiddleware>((Action<string>)(_ => { }));
            app.UseMiddleware<AdminGuardMiddleware>((Func<AdminToken, CancellationToken, Task<AuthenticatedAdmin?>>)(async (token, cancellation) =>
            { await using var read = await sessions.BeginReadOnlyAsync(cancellation); return await AdminSessionStore.AuthenticateAsync(read, token, cancellation); }));
            app.MapScopedCatalogAdministration(writers); app.MapScopedOrderAdministration(writers, _ => services); await app.StartAsync(ct); return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    internal static void Write(string path, string value, bool replace = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory == "/stage/target/requests" ? "/stage/target/tmp" : directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var output = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
        { byte[] bytes = Encoding.UTF8.GetBytes(value + "\n"); output.Write(bytes); output.Flush(true); }
        File.Move(temporary, path, replace);
    }
}
