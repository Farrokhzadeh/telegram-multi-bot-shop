using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpgsqlTypes;
using Platform.Core;
using Platform.AotSmoke;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;
using Platform.Telegram;

NativeHostSmokeTests.VerifyInventoryIsolation();
CipherQualification.Run();
Console.WriteLine("PASS: native SC02 golden vector, scope/row/field/key binding, corruption and inventory isolation.");

var status = new QualificationStatus("AotSmoke", "synthetic-qualification", false);
string json = JsonSerializer.Serialize(status, PlatformJsonContext.Default.QualificationStatus);
if (JsonSerializer.Deserialize(json, PlatformJsonContext.Default.QualificationStatus) != status)
{
    throw new InvalidOperationException("Source-generated JSON roundtrip failed.");
}

byte[] key = RandomNumberGenerator.GetBytes(32);
byte[] nonce = RandomNumberGenerator.GetBytes(12);
byte[] plaintext = Encoding.UTF8.GetBytes("synthetic credential / داده آزمایشی");
byte[] encrypted = new byte[plaintext.Length];
byte[] tag = new byte[16];
byte[] decrypted = new byte[plaintext.Length];
byte[] aad = Encoding.UTF8.GetBytes("synthetic-brand/table/row/field");
using (var aes = new AesGcm(key, 16))
{
    aes.Encrypt(nonce, plaintext, encrypted, tag, aad);
    aes.Decrypt(nonce, encrypted, tag, decrypted, aad);
    if (!CryptographicOperations.FixedTimeEquals(plaintext, decrypted))
    {
        throw new InvalidOperationException("AES-GCM roundtrip failed.");
    }

    bool rejected = false;
    try
    {
        aes.Decrypt(nonce, encrypted, tag, decrypted, Encoding.UTF8.GetBytes("another-brand"));
    }
    catch (AuthenticationTagMismatchException)
    {
        rejected = true;
    }

    if (!rejected)
    {
        throw new InvalidOperationException("Wrong-scope ciphertext was accepted.");
    }
}

CryptographicOperations.ZeroMemory(key);
CryptographicOperations.ZeroMemory(plaintext);
CryptographicOperations.ZeroMemory(decrypted);
using (var vectors = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json"))))
{
    foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
    {
        LegacyFieldKind fieldKind = vector.GetProperty("kind").GetString() switch
        {
            "Secret" => LegacyFieldKind.Secret,
            "Json" => LegacyFieldKind.Json,
            "Text" => LegacyFieldKind.Text,
            _ => throw new InvalidOperationException("Unknown fixture field kind.")
        };
        string result = LegacyFernetReader.Decrypt(vector.GetProperty("ciphertext").GetString()!, fieldKind,
            vector.GetProperty("keys").EnumerateArray().Select(v => v.GetString()!));
        if (result != vector.GetProperty("plaintext").GetString())
        {
            throw new InvalidOperationException("Legacy Fernet interoperability failed.");
        }
    }
}

using (var files = new SyntheticSecretFiles())
{
    using var snapshot = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
    var registry = new BotBrandRegistry([new("native-bot", "a", SyntheticSecretFiles.BrandA, 101,
        SyntheticSecretFiles.Deployment, 7, true)]);
    var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "a")], "native-bot"));
    if (!registry.TryResolveAuthenticated("native-bot", principal, out var context) ||
        snapshot.CurrentBrandKeyId(context!) != "current" ||
        snapshot.UseBrandKey(context!, "old", value => value.Length) != 32 ||
        snapshot.UseInventoryKey("current", value => value.Length) != 32)
        throw new InvalidOperationException("Native secret-file load failed.");
    var expected = File.ReadAllBytes(Path.Combine(files.Root, "a-current"));
    try
    {
        if (!snapshot.UseBrandKey(context!, "current", value => CryptographicOperations.FixedTimeEquals(expected, value)))
            throw new InvalidOperationException("Native key bytes did not match the protected source file.");
    }
    finally { CryptographicOperations.ZeroMemory(expected); }
    bool denied = false;
    try { using var invalid = SecretFiles.Load(files.Root, "service-config.json", files.Grant(revision: 8)); }
    catch (SecretConfigurationException error) when (error.InnerException is null) { denied = true; }
    if (!denied) throw new InvalidOperationException("Native configuration revision was not checked.");
    files.WriteManifest(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]);
    using var bot = SecretFiles.Load(files.Root, "service-config.json",
        files.Grant(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA]));
    denied = false;
    try { bot.UseInventoryKey("current", value => value.Length); }
    catch (SecretConfigurationException) { denied = true; }
    if (!denied) throw new InvalidOperationException("Native bot inventory access was accepted.");
    File.Delete(Path.Combine(files.Root, "a-current"));
    File.CreateSymbolicLink(Path.Combine(files.Root, "a-current"), Path.Combine(files.Root, "b-current"));
    denied = false;
    try { using var invalid = SecretFiles.Load(files.Root, "service-config.json", files.Grant(SecretService.BotHost, false, brands: [SyntheticSecretFiles.BrandA])); }
    catch (SecretConfigurationException error) when (error.InnerException is null) { denied = true; }
    if (!denied) throw new InvalidOperationException("Native secret symlink was accepted.");
}
Console.WriteLine("PASS: native versioned secret files, historical key lookup, independent inventory authorization and no-follow paths.");

await using (var checkout = await CheckoutFixture.CreateAsync())
{
    var request = checkout.Request();
    var result = await checkout.Store.PurchaseLocalAsync(checkout.Context(0), CheckoutFixture.Actor(0), request);
    var replay = await checkout.Store.PurchaseLocalAsync(checkout.Context(0), CheckoutFixture.Actor(0), request);
    if (result.Outcome != CheckoutOutcome.Purchased || replay.Outcome != CheckoutOutcome.Replayed ||
        await checkout.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'") != 1 ||
        await checkout.CountAsync("SELECT count(*) FROM shop.inventory_items WHERE state='allocated'") != 1 ||
        await checkout.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'") != 1)
        throw new InvalidOperationException("Native atomic wallet checkout/replay failed.");
}
Console.WriteLine("PASS: native atomic local wallet purchase, encrypted allocation and durable replay without transport.");

await using (var database = await DisposablePostgres.CreateAsync())
{
    var manifest = SchemaManifest.ForQualification(database.Data.ConnectionString);
    SchemaReceipt applied = await SchemaRunner.ApplyAsync(database.Data, manifest);
    SchemaReceipt repeated = await SchemaRunner.ApplyAsync(database.Data, manifest);
    if (!applied.Applied || repeated.Applied || applied.CatalogSha256 != repeated.CatalogSha256)
    {
        throw new InvalidOperationException("Native schema application/reapplication failed.");
    }

    await NativeHostSmokeTests.VerifyDatabaseAsync(database.Data);
    Console.WriteLine("PASS: native foundation 28-table/zero-JSON metadata and independent inventory-key boundaries.");
    await AdminQualification.RunAsync(database.Data);
    Console.WriteLine("PASS: native administrator sessions, audit idempotency, two-channel binding and immediate revocation.");
    await OutboxQualification.RunAsync(database.Data);
    Console.WriteLine("PASS: native typed outbox enqueue, deduplication, lease renewal and stale-generation fences.");

    var userSessions = new DbSessionFactory(database.Data);
    Guid customerBrand = Guid.NewGuid(), customerDeployment = SyntheticSecretFiles.Deployment;
    var customerRegistry = new BotBrandRegistry([new("native-customer", "public-customer", customerBrand, 91001, customerDeployment, 7, true)]);
    var customerPrincipal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "public-customer")], "native-customer"));
    if (!customerRegistry.TryResolveAuthenticated("native-customer", customerPrincipal, out var customerContext))
        throw new InvalidOperationException("Native customer identity failed.");
    await using (var seed = await userSessions.BeginAsync())
    {
        await seed.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active)
            VALUES ($1,'public-customer','public customer','public customer','international','en','public_customer',91001,$2,'public-key',true)
            """, DbValue.Uuid(customerBrand), DbValue.Binary([1])));
        await seed.CommitAsync();
    }
    BrandUser nativeMember;
    await using (var start = await userSessions.BeginAsync())
    {
        nativeMember = await BrandUserStore.ObservePrivateAsync(start, customerContext!, 97001);
        await BrandUserStore.SetPreferencesAsync(start, customerContext!, 97001, nativeMember.Id, "fa", true);
        await BrandUserStore.TransitionAsync(start, customerContext!, 97001, nativeMember.Id, 0, CustomerFlowState.Catalog);
        await start.CommitAsync();
    }
    await using (var read = await userSessions.BeginAsync())
    {
        var member = await BrandUserStore.RequireAsync(read, customerContext!, 97001, nativeMember.Id);
        if (member.Language != "fa" || !member.MarketingOptOut || member.Flow.Revision != 1 ||
            await BrandUserStore.ResolvePrivateDestinationAsync(read, customerContext!, nativeMember.Id) != 97001)
            throw new InvalidOperationException("Native scoped customer state failed.");
    }
    await using (var stale = await userSessions.BeginAsync())
    {
        bool rejected = false;
        try { await BrandUserStore.TransitionAsync(stale, customerContext!, 97001, nativeMember.Id, 0, CustomerFlowState.Idle); }
        catch (UnauthorizedAccessException) { rejected = stale.State == DbSessionState.Aborted; }
        if (!rejected) throw new InvalidOperationException("Native stale customer callback was accepted.");
    }
    Console.WriteLine("PASS: native brand-scoped membership, preferences, DM destination and stale-flow rollback.");
    BrandPolicySnapshot nativePolicy;
    await using (var readPolicy = await userSessions.BeginAsync())
        nativePolicy = await BrandPolicyStore.ReadAsync(readPolicy, customerContext!, 97001);
    ChannelAccess? channelAccess = await BrandPolicy.CheckChannelsAsync(nativePolicy, 97001,
        (_, _, _) => throw new InvalidOperationException("Bypassed native channel transport was called."));
    await using (var applyPolicy = await userSessions.BeginAsync())
    {
        if (channelAccess is null || Localization.Text(nativePolicy, CustomerText.OutOfStock) != "ناموجود")
            throw new InvalidOperationException("Native localized policy failed.");
        await channelAccess.RequireCurrentAsync(applyPolicy, customerContext!, 97001);
        await applyPolicy.CommitAsync();
    }
    Console.WriteLine("PASS: native channel-policy revalidation and localized stock notice with no transport call.");
    byte[] webhookSecret = SHA256.HashData("public synthetic native route fixture"u8);
    using (var verifier = new TelegramWebhookVerifier(customerContext!, webhookSecret))
    {
        var update = verifier.Verify([TelegramFixture.Header(webhookSecret)], TelegramFixture.Body("/menu", updateId: 930001, actorId: 97001, chatId: 97001));
        var router = new UpdateRouter(userSessions, (_, _, _) => throw new InvalidOperationException("Navigation called availability transport."));
        var routed = await router.RouteAsync(update);
        var replayed = await router.RouteAsync(update);
        if (routed.Code != UpdateRouteCode.Menu || replayed.Code != UpdateRouteCode.Duplicate || routed.FlowRevision != replayed.FlowRevision)
            throw new InvalidOperationException("Native verified Telegram routing failed.");
    }
    Console.WriteLine("PASS: native verified private update routing and replay suppression with no transport.");
    await using (var enableSupport = await userSessions.BeginAsync())
    {
        Guid contact = Guid.NewGuid(), audit = Guid.NewGuid();
        await enableSupport.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,external_subject) VALUES ($1,'public-native-support','external','public-native-support')", DbValue.Uuid(contact)));
        await enableSupport.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,action,entity_kind,entity_id,reason,correlation_id)
            VALUES ($1,'admin',$2,'bind_telegram','admin',$2,'public synthetic fixture',gen_random_uuid())
            """, DbValue.Uuid(audit), DbValue.Uuid(contact)));
        await enableSupport.ExecuteAsync(new DbStatement("UPDATE shop.admins SET telegram_user_id=98001,telegram_verified_by_admin_id=$1,telegram_verified_at=CURRENT_TIMESTAMP,telegram_verification_audit_id=$2,public_contact_handle='public_support' WHERE id=$1", DbValue.Uuid(contact), DbValue.Uuid(audit)));
        await enableSupport.ExecuteAsync(new DbStatement("UPDATE shop.brands SET support_enabled=true,customer_support_admin_id=$1,policy_version=policy_version+1 WHERE id=$2", DbValue.Uuid(contact), DbValue.Uuid(customerBrand)));
        await enableSupport.CommitAsync();
    }
    using (var supportFiles = new SyntheticSecretFiles())
    {
        supportFiles.WriteManifest(SecretService.BotHost, false, brands: [customerBrand]);
        using var supportSecrets = SecretFiles.Load(supportFiles.Root, "service-config.json", supportFiles.Grant(SecretService.BotHost, false, brands: [customerBrand]));
        using var verifier = new TelegramWebhookVerifier(customerContext!, webhookSecret);
        var support = new HumanSupportRouter(userSessions, supportSecrets);
        var opened = await support.RouteAsync(verifier.Verify([TelegramFixture.Header(webhookSecret)], TelegramFixture.Body("/support", updateId: 930002, actorId: 97001, chatId: 97001, messageId: 930002)));
        var text = verifier.Verify([TelegramFixture.Header(webhookSecret)], TelegramFixture.Body("public native support text", updateId: 930003, actorId: 97001, chatId: 97001, messageId: 930003));
        await support.RouteAsync(text);
        await new HumanSupportRouter(userSessions, supportSecrets).RouteAsync(text);
        await using var read = await userSessions.BeginAsync();
        var message = await read.QuerySingleAsync(new DbStatement("SELECT id,body_ciphertext,body_key_id FROM shop.support_messages WHERE ticket_id=$1 AND telegram_message_id=930003", DbValue.Uuid(opened.TicketId)),
            row => (Id: row.GetGuid(0), Bytes: row.GetBinary(1), Key: row.GetString(2)));
        byte[] plain = new BrandCipher(supportSecrets).Decrypt(customerContext!, "support_messages", message.Id, "body_ciphertext", message.Key, message.Bytes);
        try { if (!plain.AsSpan().SequenceEqual("public native support text"u8)) throw new InvalidOperationException("Native support cipher failed."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        long work = await read.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.jobs WHERE job_kind='support_notice' AND brand_id=$1", DbValue.Uuid(customerBrand)), row => row.GetInt64(0));
        if (work != 2) throw new InvalidOperationException("Native support replay duplicated staff work.");
    }
    Console.WriteLine("PASS: native encrypted human support and durable staff work survive router restart without AI or transport.");

    string? operatorPath = Environment.GetEnvironmentVariable("SHOP_SCHEMA_RUNNER_PATH");
    if (operatorPath is not null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "shop-schema-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string manifestPath = Path.Combine(directory, "manifest");
            string connectionPath = Path.Combine(directory, "connection");
            string receiptPath = Path.Combine(directory, "receipt");
            await File.WriteAllTextAsync(manifestPath, manifest.ToFileText());
            var operatorConnection = new Npgsql.NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
                ?? throw new InvalidOperationException("Synthetic control identity is missing."))
            { Database = manifest.Database };
            await File.WriteAllTextAsync(connectionPath, operatorConnection.ConnectionString);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(connectionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var start = new System.Diagnostics.ProcessStartInfo(operatorPath) { UseShellExecute = false };
            foreach (string argument in new[] { "--apply-schema", "--manifest", manifestPath, "--connection-file", connectionPath, "--receipt", receiptPath })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Operator probe did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(receiptPath) ||
                !(await File.ReadAllTextAsync(receiptPath)).Contains("catalog_sha256=" + applied.CatalogSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native operator receipt probe failed.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    await using var command = database.Data.CreateCommand("SELECT @amount::numeric");
    const decimal amount = 123456789012.123456789012m;
    command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
    if ((decimal)(await command.ExecuteScalarAsync())! != amount)
    {
        throw new InvalidOperationException("Native Npgsql decimal mapping failed.");
    }

    var sessions = new DbSessionFactory(database.Data);
    await using (var session = await sessions.BeginAsync())
    {
        Money expected = Money.Parse("123456789012.123456789012", Currency.Usd);
        Money actual = await session.QuerySingleAsync(new DbStatement("SELECT $1::numeric::text", DbValue.Money(expected, Currency.Usd)),
            r => DbValue.ReadMoney(r, 0, Currency.Usd));
        if (expected != actual || ExactDecimal.Divide(1, 8) != 0.125m)
            throw new InvalidOperationException("Native exact money mapping failed.");
        await session.CommitAsync();
    }
    var synthetic = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
        ?? throw new InvalidOperationException("Synthetic control identity is missing."))
    { Database = manifest.Database };
    await using var proxy = new DropCommitReplyProxy(synthetic.ConnectionString);
    await using var proxyData = proxy.CreateClientDataSource();
    Guid effect = Guid.NewGuid();
    await using (var uncertain = await new DbSessionFactory(proxyData).BeginAsync())
    {
        await uncertain.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate',$2)",
            DbValue.Uuid(effect), DbValue.Text("public native lost acknowledgement")));
        bool rejected = false;
        try { await uncertain.CommitAsync(); }
        catch (DbOperationException error) when (error.Failure.Recovery == DbRecovery.ReconcileBeforeRetry) { rejected = true; }
        await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10));
        if (!rejected || uncertain.State != DbSessionState.OutcomeUnknown)
            throw new InvalidOperationException("Native unknown commit policy failed.");
    }
    await using var verify = await sessions.BeginAsync();
    long effects = await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(effect)), r => r.GetInt64(0));
    if (effects != 1) throw new InvalidOperationException("Native committed effect reconciliation failed.");
}

Console.WriteLine("PASS: native JSON, AES-GCM scope, legacy Fernet, schema/operator, exact money, sessions and lost-commit reconciliation.");
