using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.SchemaRunner;

internal sealed record WalletOrderCopyResult(int Imported, int Review, int Topups, int Direct, int Catalog);
internal static class WalletOrderCopyQualification
{
    internal const string SharedMarker = "teleg-wallet-order-shared-qualification:v1";
    internal static long SharedBotId(string source) => source switch { "acc-abad" => 1101, "kuala" => 1102, "zikotech" => 1103, _ => throw new MigrationRejectedException() };
    internal static async Task<WalletOrderCopyResult> RunAsync(string connection, string directory, BrandContext context, SecretSnapshot secrets, LegacySourceReader source, VerifiedImportManifest archive, VerifiedImportManifest wallet, IReadOnlyList<LegacyWalletSnapshot> wallets, IReadOnlyList<ArchivedLegacyRecord> records, string registerPath, Func<string, string> read, CancellationToken ct, bool shared = false)
    {
        bool staging = StagingWalletSnapshot.Matches(archive);
        if (staging && (shared || !StagingWalletSnapshot.Matches(wallet) ||
            Environment.GetEnvironmentVariable("SHOP_ALLOW_STAGING_LEGACY_IMPORT") != "1")) throw new MigrationRejectedException();
        string marker = staging ? StagingWalletSnapshot.OrderTargetMarker :
            shared ? SharedMarker : "teleg-wallet-order-qualification:" + archive.SnapshotDigest;
        string retentionReference = staging ? StagingWalletSnapshot.RetentionReference : "protected:wallet-copy/original-native-history";
        var options = new NpgsqlConnectionStringBuilder(connection); var destination = SchemaManifest.ForQualification(connection);
        if (options.IncludeErrorDetail || options.LogParameters || options.Enlist || options.Multiplexing) throw new MigrationRejectedException();
        await using var data = new NpgsqlSlimDataSourceBuilder(connection).EnableArrays().Build();
        await using (var check = data.CreateCommand("SELECT current_database()=$1 AND current_user='shop_test_runner' AND current_setting('server_version_num')::integer=180006 AND pg_get_userbyid(datdba)='shop_test_runner' AND shobj_description(oid,'pg_database')=$2 FROM pg_database WHERE datname=current_database()"))
        { check.Parameters.AddWithValue(destination.Database); check.Parameters.AddWithValue(marker); if (await check.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException(); }
        var files = new LegacyArchiveFiles(directory, secrets);
        if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException();
        string zeroDirectory = Path.Combine(directory, shared ? "teleg-history-archive-shared-catalog" : "teleg-history-archive-direct-catalog"); Directory.CreateDirectory(zeroDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var zeroFiles = new LegacyArchiveFiles(zeroDirectory, secrets);
        var plan = await LegacyWalletOrderPlan.CreateWithZeroOrdersAsync(context, secrets, archive, wallet, destination, "protected:wallet-orders/" + wallet.Batch.ToString("N"), "Legacy source " + archive.SourceBrand + " (historical public name unknown)", wallets, records, (manifest, zero, target, token) => zeroFiles.WriteZeroAsync(context, manifest, zero, target, token), ct, true, true, true);
        var disposition = LegacyRecordDispositionReport.Verify(plan);
        async Task Freeze(string name, byte[] body)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) { if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(read(path))), SHA256.HashData(body))) throw new MigrationRejectedException(); }
            else await WalletCopyQualification.WriteAsync(path, body, ct);
        }
        await Freeze(shared ? "wallet-shared-direct-catalog-plan.sha256" : "wallet-zero-topup-direct-catalog-plan.sha256", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(plan.Manifest.Encode()))));
        await Freeze(shared ? "wallet-shared-direct-catalog-target.sha256" : "wallet-zero-topup-direct-catalog-target.sha256", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(connection)))));
        string reviewName = shared ? "wallet-shared-direct-catalog-review.json" : "wallet-zero-topup-direct-catalog-review.json";
        await Freeze(reviewName, WalletCopyQualification.JsonBody(w =>
        {
            w.WriteString("archive_manifest_sha256", Convert.ToHexStringLower(archive.Digest.Span)); w.WriteString("typed_manifest_sha256", Convert.ToHexStringLower(plan.Manifest.Digest.Span)); w.WriteNumber("original_records", disposition.Archived); w.WriteNumber("typed_orders", disposition.Orders); w.WriteNumber("typed_payments", disposition.Payments); w.WriteNumber("native_zero_proofs", disposition.NativeZero); w.WriteNumber("inactive_master_catalog", disposition.Catalog); w.WriteNumber("reviewed_records", disposition.Reviewed); w.WriteStartObject("review_reason_counts"); foreach (var reason in disposition.ReviewReasons.OrderBy(r => r.Key, StringComparer.Ordinal)) w.WriteNumber(reason.Key, reason.Value); w.WriteEndObject(); w.WriteStartArray("review");
            foreach (var row in plan.Review) { w.WriteStartObject(); w.WriteString("projection", row.Projection.ToString()); w.WriteString("legacy_id", row.LegacyId); w.WriteString("target", row.Target); w.WriteString("reason", row.Reason); w.WriteEndObject(); }
            w.WriteEndArray();
        }));
        _ = LegacyOriginalReviewQualification.Verify(archive, read(Path.Combine(directory, reviewName)), Convert.ToHexStringLower(plan.Manifest.Digest.Span), plan.Set.Orders.Count);
        await Platform.Persistence.SchemaRunner.ApplyPlaintextAsync(data, ct);
        await using (var role = data.CreateCommand("DO $$ BEGIN CREATE ROLE shop_legacy_import NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; GRANT shop_legacy_import TO shop_test_runner")) await role.ExecuteNonQueryAsync(ct);
        var sessions = new DbSessionFactory(data); if (shared) await WalletCopyQualification.SeedSharedAsync(sessions, context.BrandId, archive.SourceBrand, ct);
        else await WalletCopyQualification.SeedAsync(sessions, context.BrandId, archive.SourceBrand, ct);
        var byReference = records.ToDictionary(r => r.Reference, StringComparer.Ordinal);
        var derived = plan.Set.Delivery.ToDictionary(r => r.Reference, StringComparer.Ordinal);
        async Task<ProtectedLegacyEvidence> ReadArchive(string reference, CancellationToken token)
        {
            if (derived.TryGetValue(reference, out var zero)) return (await zeroFiles.ReadZeroAsync(context, plan.Manifest, zero.Target, token)).Evidence;
            if (!byReference.TryGetValue(reference, out var row)) throw new MigrationRejectedException();
            return (await files.ReadAsync(context, archive, row.Target, token)).Evidence;
        }
        Task<ReadOnlyMemory<byte>> ReadRetention(string reference, CancellationToken token)
        { if (reference != retentionReference) throw new MigrationRejectedException(); return Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(read(registerPath))); }
        var workflow = new ImportWorkflow(source, sessions); var report = new LegacyRecordReconciliationReport(data, secrets);
        var first = await workflow.ImportWalletOrdersAsync(context, secrets, plan, ReadArchive, ReadRetention, report, ct);
        var replay = await workflow.ImportWalletOrdersAsync(context, secrets, plan, ReadArchive, ReadRetention, report, ct);
        if (!first.Matched || first != replay || first.ReleaseReady || first.ComparedRecords != wallets.Count + plan.Set.Orders.Count + plan.Set.Payments.Count + plan.Set.Catalog.Count + plan.Set.Delivery.Count || plan.Review.Count + plan.Set.Orders.Count + plan.Set.Payments.Count + plan.Set.Catalog.Count + plan.Set.Delivery.Count != records.Count) throw new MigrationRejectedException();
        await using (var scope = await sessions.BeginReadOnlyAsync(ct))
        {
            var tables = await scope.QueryManyAsync(new DbStatement("SELECT tablename FROM pg_tables WHERE schemaname='shop' ORDER BY tablename"), r => r.GetString(0), 1000, ct);
            foreach (string table in tables.Where(t => t is not ("brands" or "users" or "wallets" or "wallet_entries" or "legacy_id_map" or "audit_events" or "bot_activity_events" or "orders" or "payments" or "products" or "brand_products"))) if (await scope.RunEfStepAsync(false, (ef, token) => SnapshotCopyQueries.CountAsync(ef.Data, table, token), ct) != 0) throw new MigrationRejectedException();
            if (shared)
            {
                bool valid = await scope.RunEfStepAsync(false, async (ef, token) =>
                {
                    var users = ef.Data.Set<User>().Where(u => u.BrandId == context.BrandId);
                    var orders = ef.Data.Set<Order>().Where(o => users.Any(u => u.Id == o.UserId));
                    return await users.LongCountAsync(token) == wallets.Count && await ef.Data.Set<Wallet>().LongCountAsync(w => users.Any(u => u.Id == w.UserId), token) == wallets.Count && await orders.LongCountAsync(token) == plan.Set.Orders.Count && await ef.Data.Set<Payment>().LongCountAsync(p => users.Any(u => u.Id == p.TopupUserId || orders.Any(o => o.Id == p.OrderId && o.UserId == u.Id)), token) == plan.Set.Payments.Count && await ef.Data.Set<WalletEntry>().LongCountAsync(e => e.IsPosting && users.Any(u => u.Id == e.UserId), token) == plan.Manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletOpening) && await ef.Data.Set<BrandProduct>().LongCountAsync(b => b.BrandId == context.BrandId, token) == plan.Set.Catalog.Count && await ef.Data.Set<LegacyIdMap>().LongCountAsync(m => m.SourceBrand == archive.SourceBrand, token) == plan.Manifest.Proofs.Count && !await ef.Data.Set<Product>().AnyAsync(p => p.Active || !ef.Data.Set<BrandProduct>().Any(b => b.ProductId == p.Id), token) && !await ef.Data.Set<BrandProduct>().AnyAsync(b => b.Visible, token);
                }, ct);
                if (!valid) throw new MigrationRejectedException();
            }
            else
            {
                bool valid = await scope.RunEfStepAsync(false, async (ef, token) => await ef.Data.Set<Order>().LongCountAsync(token) == plan.Set.Orders.Count && await ef.Data.Set<WalletEntry>().LongCountAsync(e => e.IsPosting, token) == plan.Manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletOpening) && !await ef.Data.Set<Product>().AnyAsync(p => p.Active, token) && !await ef.Data.Set<BrandProduct>().AnyAsync(b => b.Visible, token) && await ef.Data.Set<Product>().LongCountAsync(token) == plan.Set.Catalog.Count && await ef.Data.Set<BrandProduct>().LongCountAsync(token) == plan.Set.Catalog.Count && await ef.Data.Set<Payment>().LongCountAsync(token) == plan.Set.Payments.Count && await ef.Data.Set<LegacyIdMap>().LongCountAsync(token) == plan.Manifest.Proofs.Count, ct);
                if (!valid) throw new MigrationRejectedException();
            }
        }
        int direct = plan.Set.Payments.Count(p => p.Order is not null);
        if (direct != plan.Set.Orders.Count(o => o.NativePaymentSettledAt is not null)) throw new MigrationRejectedException();
        return new(plan.Set.Orders.Count, plan.Review.Count, plan.Set.Payments.Count - direct, direct, plan.Set.Catalog.Count);
    }
}
