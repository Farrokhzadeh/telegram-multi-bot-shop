using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.SchemaRunner;

internal static class WalletCopyQualification
{
    private static readonly Dictionary<string, (string Brand, string Archive)> Sources = new(StringComparer.Ordinal)
    {
        ["teleg_source_prod1"] = ("acc-abad", "0e86433927fa420ad3e13bbff2098119d47dfbaa0b626573c552999902703667"),
        ["teleg_source_prod2"] = ("kuala", "a61aaddbbd76a10aa6894aa02cffe861cb44a9602b1d085eecc981f822656071"),
        ["teleg_source_prod3"] = ("zikotech", "4481980586074034e84ff11d867621a1aa847c18589576ab7dd89155a21d6245")
    };
    internal static string SourceBrand(string database, string archive) => Sources.TryGetValue(database, out var source) && source.Archive == archive ? source.Brand : throw new MigrationRejectedException();
    internal static async Task RunAsync(string job, string receipt, Func<string, string> read, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_WALLET_QUALIFICATION") != "1" || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || job.Length > 16384) throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(job); var root = document.RootElement;
        string[] fields = ["version", "source_connection_file", "source_plan_file", "target_connection_file", "register_file", "work_directory"];
        if (root.EnumerateObject().Count() != fields.Length || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length || fields.Any(p => !root.TryGetProperty(p, out _)) || root.GetProperty("version").GetInt32() != 1) throw new MigrationRejectedException();
        string Value(string name) => root.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
        string directory = Value("work_directory"), sourceConnection = read(Value("source_connection_file")).Trim(), sourcePlan = read(Value("source_plan_file")), targetConnection = read(Value("target_connection_file")).Trim(), registerPath = Value("register_file");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || !Path.GetFileName(directory).StartsWith("teleg-wallet-qualification-", StringComparison.Ordinal) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0 || !Path.IsPathFullyQualified(receipt) || Path.GetDirectoryName(receipt) != directory || !receipt.EndsWith(".receipt.json", StringComparison.Ordinal) || File.Exists(receipt) || new FileInfo(registerPath).Length > 67108864) throw new MigrationRejectedException();
        byte[] register = Encoding.UTF8.GetBytes(read(registerPath));
        try
        {
            using var planDocument = JsonDocument.Parse(sourcePlan); var plan = planDocument.RootElement;
            string database = plan.GetProperty("database").GetString() ?? throw new MigrationRejectedException(), host = plan.GetProperty("socket").GetString() ?? throw new MigrationRejectedException();
            bool staging = database == StagingWalletSnapshot.Database;
            var identity = staging ? (Brand: StagingWalletSnapshot.Brand, Archive: StagingWalletSnapshot.ArchiveSha256) :
                Sources.TryGetValue(database, out var historical) ? historical : throw new MigrationRejectedException();
            if (plan.GetProperty("archive_sha256").GetString() != identity.Archive) throw new MigrationRejectedException();
            string retentionReference = staging ? StagingWalletSnapshot.RetentionReference : "protected:wallet-copy/original-native-history";
            string revision = staging ? StagingWalletSnapshot.LegacyRevision : "66371a23a1c8149dc5bdf89a0355aa689d454dba";
            var sourceOptions = new NpgsqlConnectionStringBuilder(sourceConnection); var targetOptions = new NpgsqlConnectionStringBuilder(targetConnection); var destination = SchemaManifest.ForQualification(targetConnection);
            if (sourceOptions.Host != host || sourceOptions.Database != database || sourceOptions.Username != "teleg_source_reader" || sourceOptions.Port != 5432 || sourceOptions.IncludeErrorDetail || sourceOptions.LogParameters || sourceOptions.Enlist || sourceOptions.Multiplexing || targetOptions.IncludeErrorDetail || targetOptions.LogParameters || targetOptions.Enlist || targetOptions.Multiplexing || !Path.IsPathFullyQualified(host) || Path.GetFileName(host) != "socket" || Path.GetFileName(Path.GetDirectoryName(host))?.StartsWith("teleg-source-qualification-", StringComparison.Ordinal) != true) throw new MigrationRejectedException();
            byte[] schema = Convert.FromHexString(plan.GetProperty("schema_sha256").GetString() ?? throw new MigrationRejectedException());
            byte[] inputHash = SHA256.HashData(Encoding.UTF8.GetBytes(job + "\n" + sourcePlan + "\n" + sourceConnection + "\n" + targetConnection + "\n" + Convert.ToHexStringLower(SHA256.HashData(register))));
            string statePath = Path.Combine(directory, "state.json"), manifestPath = Path.Combine(directory, "wallet.plan"); Guid batch; DateTime observed;
            if (File.Exists(statePath))
            {
                using var state = JsonDocument.Parse(read(statePath)); var value = state.RootElement;
                if (value.GetProperty("version").GetInt32() != 1 || value.GetProperty("input_sha256").GetString() != Convert.ToHexStringLower(inputHash)) throw new MigrationRejectedException();
                batch = value.GetProperty("batch").GetGuid(); observed = DateTime.ParseExact(value.GetProperty("observed_at").GetString()!, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            else
            {
                batch = Guid.NewGuid(); long ticks = DateTime.UtcNow.Ticks; observed = new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
                await WriteAsync(statePath, JsonBody(w => { w.WriteNumber("version", 1); w.WriteString("input_sha256", Convert.ToHexStringLower(inputHash)); w.WriteString("batch", batch); w.WriteString("observed_at", observed.ToString("O", CultureInfo.InvariantCulture)); }), ct);
            }
            if (batch == Guid.Empty || observed.Kind != DateTimeKind.Utc) throw new MigrationRejectedException();
            await SourceCopyInspection.RunAsync(sourceConnection, sourcePlan, Path.Combine(directory, "source-start-" + Guid.NewGuid().ToString("N") + ".json"), ct);
            Guid brand = staging ? StagingWalletSnapshot.TargetBrand :
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("protected-wallet-copy-brand-v1:" + identity.Brand)).AsSpan(0, 16));
            var registry = new BotBrandRegistry([new("protected-wallet-copy", "qualification", brand, 101, batch, 1, true)]);
            if (!registry.TryResolveAuthenticated("protected-wallet-copy", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "qualification")], "protected-wallet-copy")), out var context)) throw new MigrationRejectedException();
            await using var sourceData = new NpgsqlSlimDataSourceBuilder(sourceConnection).Build(); await using var targetData = new NpgsqlSlimDataSourceBuilder(targetConnection).Build();
            await using (var check = targetData.CreateCommand("SELECT current_database()=$1 AND current_user='shop_test_runner' AND current_setting('server_version_num')::integer=180006 AND pg_get_userbyid(datdba)='shop_test_runner' AND shobj_description(oid,'pg_database')=$2 FROM pg_database WHERE datname=current_database()"))
            { check.Parameters.AddWithValue(destination.Database); check.Parameters.AddWithValue((staging ? "teleg-staging-wallet-qualification:" : "teleg-wallet-qualification:") + identity.Archive); if (await check.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException(); }
            var retained = LegacyWalletRetention.OpenAll(retentionReference, SHA256.HashData(register), register, identity.Brand, identity.Archive);
            var source = new LegacySourceReader(sourceData, new(host, 5432, database, "teleg_source_reader"));
            var rows = await source.PlanWalletsAsync(schema, batch, brand, retained, observed, ct);
            byte[] body = VerifiedImportManifest.EncodePlan(destination, identity.Brand, brand, revision, schema, "protected:wallet-copy/source-archive", Convert.FromHexString(identity.Archive), batch, rows.SelectMany(r => r.Proofs(brand)), rows.Sum(r => r.BalanceUsd));
            if (File.Exists(manifestPath)) { if (new FileInfo(manifestPath).LinkTarget is not null || (File.GetUnixFileMode(manifestPath) & (UnixFileMode)63) != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, ct)), SHA256.HashData(body))) throw new MigrationRejectedException(); }
            else await WriteAsync(manifestPath, body, ct);
            var manifest = VerifiedImportManifest.Open("protected:wallet-copy/" + batch.ToString("N"), SHA256.HashData(body), body);
            await Platform.Persistence.SchemaRunner.ApplyAsync(targetData, destination, ct);
            await using (var role = targetData.CreateCommand("DO $$ BEGIN CREATE ROLE shop_legacy_import NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; GRANT shop_legacy_import TO shop_test_runner")) await role.ExecuteNonQueryAsync(ct);
            var sessions = new DbSessionFactory(targetData);
            await SeedAsync(sessions, brand, identity.Brand, ct);
            async Task<ReadOnlyMemory<byte>> ReadRetention(string reference, CancellationToken token)
            { if (!OperatingSystem.IsLinux() || reference != retentionReference || new FileInfo(registerPath).LinkTarget is not null || (File.GetUnixFileMode(registerPath) & (UnixFileMode)63) != 0) throw new MigrationRejectedException(); return await File.ReadAllBytesAsync(registerPath, token); }
            var workflow = new ImportWorkflow(source, sessions);
            var first = await workflow.ImportWalletsAsync(context!, manifest, rows, ReadRetention, ct);
            var replay = await workflow.ImportWalletsAsync(context!, manifest, rows, ReadRetention, ct);
            var aggregate = await new ReconciliationReport(sessions).ReadAsync(manifest, ct);
            if (!first.Matched || first != replay || !aggregate.Complete || aggregate.SaleableInventory != 0 || aggregate.PaidUnfulfilledOrders != 0 || aggregate.HeldCouponUnits != 0 || aggregate.TargetCiphertexts != 0) throw new MigrationRejectedException();
            await using (var scope = await sessions.BeginReadOnlyAsync(ct))
            {
                var tables = await scope.QueryManyAsync(new DbStatement("SELECT tablename FROM pg_tables WHERE schemaname='shop' ORDER BY tablename"), r => r.GetString(0), 1000, ct);
                foreach (string table in tables.Where(t => t is not ("brands" or "users" or "wallets" or "wallet_entries" or "legacy_id_map" or "audit_events"))) if (await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop." + table), r => r.GetInt64(0), ct) != 0) throw new MigrationRejectedException();
                if (await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.audit_events"), r => r.GetInt64(0), ct) != rows.Count || await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries"), r => r.GetInt64(0), ct) != aggregate.OpeningPostings + aggregate.NonPostingHistory) throw new MigrationRejectedException();
                if (await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.users"), r => r.GetInt64(0), ct) != rows.Count || await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallets"), r => r.GetInt64(0), ct) != rows.Count || await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.legacy_id_map"), r => r.GetInt64(0), ct) != manifest.Proofs.Count) throw new MigrationRejectedException();
            }
            await SourceCopyInspection.RunAsync(sourceConnection, sourcePlan, Path.Combine(directory, "source-end-" + Guid.NewGuid().ToString("N") + ".json"), ct);
            if (!retained.First().Value.Matches((await ReadRetention(retentionReference, ct)).Span)) throw new MigrationRejectedException();
            await WriteAsync(receipt, JsonBody(w => { w.WriteString("scope", "protected_copy_wallet_projection_only"); w.WriteString("source_brand", identity.Brand); w.WriteString("archive_sha256", identity.Archive); w.WriteNumber("accounts", rows.Count); w.WriteNumber("retained_reversed_membership_clocks", rows.Count(r => r.SourceClock is not null)); w.WriteNumber("opening_postings", aggregate.OpeningPostings); w.WriteNumber("nonposting_usd_history", aggregate.NonPostingHistory); w.WriteNumber("manual_irr_accounts", first.ManualIrrAccounts); w.WriteNumber("retained_history", first.RetainedHistoryRows); w.WriteNumber("retained_irr_history", first.RetainedIrrHistory); w.WriteNumber("retained_zero_history", first.RetainedZeroHistory); w.WriteBoolean("matched", true); w.WriteBoolean("replay_identical", true); w.WriteBoolean("source_unchanged", true); w.WriteBoolean("release_ready", false); w.WriteBoolean("history_orders_imported", false); w.WriteBoolean("target_reencryption_executed", false); }), ct);
        }
        finally { CryptographicOperations.ZeroMemory(register); }
    }
    internal static async Task SeedAsync(DbSessionFactory sessions, Guid brand, string brandName, CancellationToken ct)
    {
        await using (var seed = await sessions.BeginAsync(ct))
        {
            long existing = await seed.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.brands"), r => r.GetInt64(0), ct);
            if (existing == 0)
            {
                foreach (string table in new[] { "users", "wallets", "wallet_entries", "legacy_id_map", "audit_events" }) if (await seed.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop." + table), r => r.GetInt64(0), ct) != 0) throw new MigrationRejectedException();
                await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active) VALUES($1,$2,$2,$2,'international','en',$2,101,$3,'qualification-unusable',false)", DbValue.Uuid(brand), DbValue.Text("qual-" + brandName), DbValue.Binary([1])), ct);
            }
            else if (existing != 1 || !await seed.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brands WHERE id=$1 AND code=$2 AND bot_id=101 AND NOT active AND bot_token_key_id='qualification-unusable' AND bot_token_ciphertext=$3)", DbValue.Uuid(brand), DbValue.Text("qual-" + brandName), DbValue.Binary([1])), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
            await seed.CommitAsync(ct);
        }
    }
    internal static async Task SeedSharedAsync(DbSessionFactory sessions, Guid brand, string brandName, CancellationToken ct)
    {
        string[] names = ["acc-abad", "kuala", "zikotech"]; int position = Array.IndexOf(names, brandName);
        if (position < 0 || brand != new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("protected-wallet-copy-brand-v1:" + brandName)).AsSpan(0, 16))) throw new MigrationRejectedException();
        await using var seed = await sessions.BeginAsync(ct);
        long existing = await seed.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.brands"), r => r.GetInt64(0), ct);
        if (existing != position && existing != names.Length) throw new MigrationRejectedException();
        foreach (string name in existing == names.Length ? names : names.Take(position))
        {
            Guid known = new(SHA256.HashData(Encoding.UTF8.GetBytes("protected-wallet-copy-brand-v1:" + name)).AsSpan(0, 16));
            long bot = 1101 + Array.IndexOf(names, name);
            if (!await seed.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.brands WHERE id=$1 AND code=$2 AND bot_id=$3 AND NOT active AND bot_token_key_id='qualification-unusable' AND bot_token_ciphertext=$4)", DbValue.Uuid(known), DbValue.Text("qual-" + name), DbValue.Bigint(bot), DbValue.Binary([1])), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        }
        if (existing == position)
        {
            if (existing == 0) foreach (string table in new[] { "users", "wallets", "wallet_entries", "legacy_id_map", "audit_events", "orders", "payments", "products", "brand_products" })
                if (await seed.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop." + table), r => r.GetInt64(0), ct) != 0) throw new MigrationRejectedException();
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token_ciphertext,bot_token_key_id,active) VALUES($1,$2,$2,$2,'international','en',$2,$3,$4,'qualification-unusable',false)", DbValue.Uuid(brand), DbValue.Text("qual-" + brandName), DbValue.Bigint(1101 + position), DbValue.Binary([1])), ct);
        }
        await seed.CommitAsync(ct);
    }
    internal static byte[] JsonBody(Action<Utf8JsonWriter> body)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); body(writer); writer.WriteEndObject(); } return stream.ToArray(); }
    internal static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        await using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }
}
