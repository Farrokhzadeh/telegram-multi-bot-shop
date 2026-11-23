using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.SchemaRunner;

internal static class SharedWalletOrderQualification
{
    internal static async Task RunAsync(string[] jobs, string targetFile, string receipt, Func<string, string> read, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || jobs.Length != 3 || jobs.Distinct(StringComparer.Ordinal).Count() != 3 ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SHARED_WALLET_ORDER_QUALIFICATION") != "1" ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_WALLET_ORDER_QUALIFICATION") != "1" ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_ARCHIVE_QUALIFICATION") != "1" ||
            !Path.IsPathFullyQualified(receipt) || File.Exists(receipt)) throw new MigrationRejectedException();
        string? directory = Path.GetDirectoryName(receipt);
        if (directory is null || !Path.GetFileName(directory).StartsWith("teleg-wallet-shared-qualification-", StringComparison.Ordinal) ||
            !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null || (File.GetUnixFileMode(directory) & (UnixFileMode)63) != 0) throw new MigrationRejectedException();
        string connection = read(targetFile).Trim(); var identity = SchemaManifest.ForQualification(connection);
        var parts = new List<(string Job, string Directory, string Brand)>();
        string[] brands = ["acc-abad", "kuala", "zikotech"];
        foreach (string path in jobs)
        {
            string job = read(path); using var doc = JsonDocument.Parse(job); var data = doc.RootElement;
            string work = data.GetProperty("work_directory").GetString() ?? throw new MigrationRejectedException();
            string plan = read(data.GetProperty("source_plan_file").GetString() ?? throw new MigrationRejectedException());
            using var source = JsonDocument.Parse(plan);
            string database = source.RootElement.GetProperty("database").GetString() ?? throw new MigrationRejectedException();
            string digest = source.RootElement.GetProperty("archive_sha256").GetString() ?? throw new MigrationRejectedException();
            parts.Add((job, work, WalletCopyQualification.SourceBrand(database, digest)));
        }
        if (!parts.Select(p => p.Brand).SequenceEqual(brands, StringComparer.Ordinal) || parts.Select(p => p.Directory).Distinct(StringComparer.Ordinal).Count() != 3) throw new MigrationRejectedException();
        await using var target = new NpgsqlSlimDataSourceBuilder(connection).EnableArrays().Build();
        await using (var check = target.CreateCommand("SELECT current_database()=$1 AND current_user='shop_test_runner' AND current_setting('server_version_num')::integer=180006 AND pg_get_userbyid(datdba)='shop_test_runner' AND shobj_description(oid,'pg_database')=$2 AND to_regclass('shop.brands') IS NULL FROM pg_database WHERE datname=current_database()"))
        {
            check.Parameters.AddWithValue(identity.Database); check.Parameters.AddWithValue(WalletOrderCopyQualification.SharedMarker);
            if (await check.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
        }
        long users = 0, orders = 0, payments = 0, catalog = 0, reviewed = 0, zero = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i]; string ownedReceipt = Path.Combine(part.Directory, "wallet-shared-canonical-v1.receipt.json");
            if (File.Exists(ownedReceipt)) throw new MigrationRejectedException();
            await ArchiveCopyQualification.RunAsync(part.Job, ownedReceipt, read, ct, targetFile, shared: true);
            using var result = JsonDocument.Parse(read(ownedReceipt)); var root = result.RootElement;
            if (root.GetProperty("source_brand").GetString() != part.Brand || !root.GetProperty("replay_identical").GetBoolean() || !root.GetProperty("source_unchanged").GetBoolean() || root.GetProperty("release_ready").GetBoolean() || !root.GetProperty("typed_wallet_order_parity").GetBoolean()) throw new MigrationRejectedException();
            users += root.GetProperty("memberships").GetInt64(); orders += root.GetProperty("typed_wallet_orders").GetInt64();
            payments += root.GetProperty("typed_historical_topups").GetInt64() + root.GetProperty("typed_paid_direct_orders").GetInt64();
            catalog += root.GetProperty("typed_inactive_master_catalog").GetInt64(); reviewed += root.GetProperty("review_records").GetInt64();
            using var plan = JsonDocument.Parse(read(Path.Combine(part.Directory, "wallet-shared-direct-catalog-review.json")));
            zero += plan.RootElement.GetProperty("native_zero_proofs").GetInt64();
        }
        async Task<long> Count(string query)
        {
            await using var command = target.CreateCommand(query); return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (await Count("SELECT count(*) FROM shop.brands WHERE NOT active AND bot_id IN (1101,1102,1103) AND bot_token_key_id='qualification-unusable'") != 3 ||
            await Count("SELECT count(*) FROM shop.brands") != 3 ||
            await Count("SELECT count(*) FROM shop.users") != users ||
            await Count("SELECT count(*) FROM shop.wallets") != users ||
            await Count("SELECT count(*) FROM shop.orders") != orders ||
            await Count("SELECT count(*) FROM shop.payments") != payments ||
            await Count("SELECT count(*) FROM shop.products") != catalog ||
            await Count("SELECT count(*) FROM shop.brand_products") != catalog ||
            await Count("SELECT count(*) FROM shop.products WHERE active") != 0 ||
            await Count("SELECT count(*) FROM shop.brand_products WHERE visible") != 0 ||
            await Count("SELECT count(*) FROM shop.inventory_items WHERE state='available'") != 0 ||
            await Count("SELECT count(*) FROM shop.fulfillments") != 0 ||
            await Count("SELECT count(*) FROM shop.jobs") != 0 ||
            await Count("SELECT count(*) FROM shop.discounts WHERE active") != 0 ||
            await Count("SELECT count(*) FROM shop.orders WHERE payment_status IN ('paid','not_required') AND (fulfillment_status!='requires_admin' OR release_authorized_at IS NOT NULL)") != 0 ||
            await Count("SELECT count(*) FROM shop.payments p JOIN shop.orders o ON o.id=p.order_id JOIN shop.users u ON u.id=o.user_id WHERE p.topup_user_id IS NOT NULL AND p.topup_user_id!=u.id") != 0 ||
            await Count("SELECT count(*) FROM shop.legacy_id_map WHERE source_brand NOT IN ('acc-abad','kuala','zikotech')") != 0 ||
            await Count("SELECT count(*) FROM pg_tables WHERE schemaname='shop'") != 28) throw new MigrationRejectedException();
        await WalletCopyQualification.WriteAsync(receipt, WalletCopyQualification.JsonBody(writer =>
        {
            writer.WriteString("scope", "canonical_three_brand_same_disposable_target");
            writer.WriteNumber("brands", 3); writer.WriteNumber("memberships", users); writer.WriteNumber("historical_orders", orders);
            writer.WriteNumber("historical_payments", payments); writer.WriteNumber("inactive_catalog", catalog);
            writer.WriteNumber("original_zero_proofs", zero); writer.WriteNumber("retained_review_records", reviewed);
            writer.WriteBoolean("all_brand_imports_and_replays_matched", true);
            writer.WriteBoolean("same_database", true); writer.WriteBoolean("source_unchanged", true);
            writer.WriteBoolean("release_ready", false);
        }), ct);
    }
}
