using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
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
        await using var model = new PostgresAkantiDbContext(new DbContextOptionsBuilder<PostgresAkantiDbContext>().UseNpgsql(target).Options);
        if (await model.Set<Brand>().LongCountAsync(b => !b.Active && (b.BotId == 1101 || b.BotId == 1102 || b.BotId == 1103) && b.BotTokenKeyId == "qualification-unusable", ct) != 3 || await model.Set<Brand>().LongCountAsync(ct) != 3 || await model.Set<User>().LongCountAsync(ct) != users || await model.Set<Wallet>().LongCountAsync(ct) != users || await model.Set<Order>().LongCountAsync(ct) != orders || await model.Set<Payment>().LongCountAsync(ct) != payments || await model.Set<Product>().LongCountAsync(ct) != catalog || await model.Set<BrandProduct>().LongCountAsync(ct) != catalog || await model.Set<Product>().AnyAsync(p => p.Active, ct) || await model.Set<BrandProduct>().AnyAsync(b => b.Visible, ct) || await model.Set<InventoryItem>().AnyAsync(i => i.State == "available", ct) || await model.Set<Fulfillment>().AnyAsync(ct) || await model.Set<Job>().AnyAsync(ct) || await model.Set<Discount>().AnyAsync(d => d.Active, ct) || await model.Set<Order>().AnyAsync(o => (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && (o.FulfillmentStatus != "requires_admin" || o.ReleaseAuthorizedAt != null), ct) || await (from p in model.Set<Payment>() join o in model.Set<Order>() on p.OrderId equals o.Id join u in model.Set<User>() on o.UserId equals u.Id where p.TopupUserId != null && p.TopupUserId != u.Id select p.Id).AnyAsync(ct) || await model.Set<LegacyIdMap>().AnyAsync(m => m.SourceBrand != "acc-abad" && m.SourceBrand != "kuala" && m.SourceBrand != "zikotech", ct)) throw new MigrationRejectedException();
        await using (var check = target.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname='shop'")) if (Convert.ToInt64(await check.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 28) throw new MigrationRejectedException();
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
