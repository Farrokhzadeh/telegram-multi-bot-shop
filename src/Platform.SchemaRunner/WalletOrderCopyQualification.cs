using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.SchemaRunner;

internal sealed record WalletOrderCopyResult(int Imported, int Review);
internal static class WalletOrderCopyQualification
{
    internal static async Task<WalletOrderCopyResult> RunAsync(string connection, string directory, BrandContext context, SecretSnapshot secrets, LegacySourceReader source, VerifiedImportManifest archive, VerifiedImportManifest wallet, IReadOnlyList<LegacyWalletSnapshot> wallets, IReadOnlyList<ArchivedLegacyRecord> records, string registerPath, Func<string, string> read, CancellationToken ct)
    {
        var options = new NpgsqlConnectionStringBuilder(connection); var destination = SchemaManifest.ForQualification(connection);
        if (options.IncludeErrorDetail || options.LogParameters || options.Enlist || options.Multiplexing) throw new MigrationRejectedException();
        await using var data = new NpgsqlSlimDataSourceBuilder(connection).Build();
        await using (var check = data.CreateCommand("SELECT current_database()=$1 AND current_user='shop_test_runner' AND current_setting('server_version_num')::integer=180006 AND pg_get_userbyid(datdba)='shop_test_runner' AND shobj_description(oid,'pg_database')=$2 FROM pg_database WHERE datname=current_database()"))
        { check.Parameters.AddWithValue(destination.Database); check.Parameters.AddWithValue("teleg-wallet-order-qualification:" + archive.SnapshotDigest); if (await check.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException(); }
        var plan = LegacyWalletOrderPlan.Create(context, secrets, archive, wallet, destination, "protected:wallet-orders/" + wallet.Batch.ToString("N"), "Legacy source " + archive.SourceBrand + " (historical public name unknown)", wallets, records);
        async Task Freeze(string name, byte[] body)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) { if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(read(path))), SHA256.HashData(body))) throw new MigrationRejectedException(); }
            else await WalletCopyQualification.WriteAsync(path, body, ct);
        }
        await Freeze("wallet-order-plan.sha256", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(plan.Manifest.Encode()))));
        await Freeze("wallet-order-target.sha256", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(connection)))));
        await Freeze("wallet-order-review.json", WalletCopyQualification.JsonBody(w =>
        {
            w.WriteString("archive_manifest_sha256", Convert.ToHexStringLower(archive.Digest.Span)); w.WriteString("typed_manifest_sha256", Convert.ToHexStringLower(plan.Manifest.Digest.Span)); w.WriteStartArray("review");
            foreach (var row in plan.Review) { w.WriteStartObject(); w.WriteString("projection", row.Projection.ToString()); w.WriteString("legacy_id", row.LegacyId); w.WriteString("target", row.Target); w.WriteString("reason", row.Reason); w.WriteEndObject(); }
            w.WriteEndArray();
        }));
        await Platform.Persistence.SchemaRunner.ApplyAsync(data, destination, ct);
        await using (var role = data.CreateCommand("DO $$ BEGIN CREATE ROLE shop_legacy_import NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; GRANT shop_legacy_import TO shop_test_runner")) await role.ExecuteNonQueryAsync(ct);
        var sessions = new DbSessionFactory(data); await WalletCopyQualification.SeedAsync(sessions, context.BrandId, archive.SourceBrand, ct);
        var byReference = records.ToDictionary(r => r.Reference, StringComparer.Ordinal);
        async Task<ProtectedLegacyEvidence> ReadArchive(string reference, CancellationToken token)
        {
            if (!byReference.TryGetValue(reference, out var row)) throw new MigrationRejectedException();
            return (await new LegacyArchiveFiles(directory, secrets).ReadAsync(context, archive, row.Target, token)).Evidence;
        }
        Task<ReadOnlyMemory<byte>> ReadRetention(string reference, CancellationToken token)
        { if (reference != "protected:wallet-copy/original-native-history") throw new MigrationRejectedException(); return Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(read(registerPath))); }
        var workflow = new ImportWorkflow(source, sessions); var report = new LegacyRecordReconciliationReport(data, secrets);
        var first = await workflow.ImportWalletOrdersAsync(context, secrets, plan, ReadArchive, ReadRetention, report, ct);
        var replay = await workflow.ImportWalletOrdersAsync(context, secrets, plan, ReadArchive, ReadRetention, report, ct);
        if (!first.Matched || first != replay || first.ReleaseReady || first.ComparedRecords != wallets.Count + plan.Set.Orders.Count || plan.Review.Count + plan.Set.Orders.Count != records.Count) throw new MigrationRejectedException();
        await using (var scope = await sessions.BeginReadOnlyAsync(ct))
        {
            var tables = await scope.QueryManyAsync(new DbStatement("SELECT tablename FROM pg_tables WHERE schemaname='shop' ORDER BY tablename"), r => r.GetString(0), 1000, ct);
            foreach (string table in tables.Where(t => t is not ("brands" or "users" or "wallets" or "wallet_entries" or "legacy_id_map" or "audit_events" or "orders"))) if (await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop." + table), r => r.GetInt64(0), ct) != 0) throw new MigrationRejectedException();
            if (await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.orders"), r => r.GetInt64(0), ct) != plan.Set.Orders.Count || await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.wallet_entries WHERE is_posting"), r => r.GetInt64(0), ct) != plan.Manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletOpening) || await scope.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.legacy_id_map"), r => r.GetInt64(0), ct) != plan.Manifest.Proofs.Count) throw new MigrationRejectedException();
        }
        return new(plan.Set.Orders.Count, plan.Review.Count);
    }
}
