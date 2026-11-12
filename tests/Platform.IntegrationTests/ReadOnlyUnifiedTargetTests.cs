using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.SchemaRunner;
using Platform.Testing;
namespace Platform.IntegrationTests;

[Collection("PostgreSQL races")]
public sealed class ReadOnlyUnifiedTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task Write(string path, byte[] bytes) { if (!OperatingSystem.IsLinux()) throw new MigrationRejectedException(); await File.WriteAllBytesAsync(path, bytes, Ct); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private static string Review(LegacyWalletOrderPlan plan, bool changed)
    {
        var d = LegacyRecordDispositionReport.Verify(plan); var reasons = new JsonObject(); var rows = new JsonArray();
        foreach (var group in plan.Review.GroupBy(r => changed ? "outside_wallet_order_scope" : r.Reason)) reasons[group.Key] = group.Count();
        foreach (var r in plan.Review) rows.Add(new JsonObject { ["projection"] = r.Projection.ToString(), ["legacy_id"] = r.LegacyId, ["target"] = r.Target.ToString(), ["reason"] = changed ? "outside_wallet_order_scope" : r.Reason });
        return new JsonObject { ["archive_manifest_sha256"] = Convert.ToHexStringLower(plan.Archive.Digest.Span), ["typed_manifest_sha256"] = Convert.ToHexStringLower(plan.Manifest.Digest.Span), ["original_records"] = d.Archived, ["typed_orders"] = d.Orders, ["typed_payments"] = d.Payments, ["native_zero_proofs"] = d.NativeZero, ["inactive_master_catalog"] = d.Catalog, ["reviewed_records"] = d.Reviewed, ["review_reason_counts"] = reasons, ["review"] = rows }.ToJsonString();
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("plan_hash")]
    [InlineData("review_reason")]
    [InlineData("target_row")]
    [InlineData("fingerprint_row")]
    public async Task ReadOnlyTargetWorkflowDerivesActualResultsAndRejectsChangedInputs(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var world = await LegacyWalletRetentionFixture.CreateAsync(Ct, false); var f = world.Fixture; var context = f.Context(0);
        await using (var setup = f.Database.Data.CreateCommand("UPDATE shop.brands SET active=false,bot_token_key_id='qualification-unusable'; CREATE TABLE public.orders(id text PRIMARY KEY,user_id bigint,product_id bigint,qty integer,total numeric,currency text,created_at timestamptz,checkout_snapshot jsonb); INSERT INTO public.orders VALUES('SYNTHETIC-HELD',99001234,456,1,20,'USD','2024-02-01 00:00Z','{}')")) await setup.ExecuteNonQueryAsync(Ct);
        string directory = Path.Combine(Path.GetTempPath(), "teleg-history-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string zeroDirectory = Path.Combine(directory, "teleg-history-archive-shared-catalog"); Directory.CreateDirectory(zeroDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            const string retentionReference = "protected:wallet-copy/original-native-history"; var retained = LegacyWalletRetention.OpenAll(retentionReference, SHA256.HashData(world.Register), world.Register, "acc-abad", LegacyWalletRetentionFixture.Archive);
            DateTime observed = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc); Guid batch = Guid.NewGuid(); byte[] schema = await world.Source.InspectSchemaAsync(Ct);
            var wallets = await world.Source.PlanWalletsAsync(schema, batch, context.BrandId, retained, observed, Ct); var destination = SchemaManifest.ForQualification(f.Database.Data.ConnectionString);
            VerifiedImportManifest Manifest(IEnumerable<LegacyMapProof> proofs, Guid id, decimal opening)
            {
                byte[] body = VerifiedImportManifest.EncodePlan(destination, "acc-abad", context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema, "protected:synthetic/frozen-unified", Convert.FromHexString(LegacyWalletRetentionFixture.Archive), id, proofs, opening);
                return VerifiedImportManifest.Open("protected:synthetic/unified-plan", SHA256.HashData(body), body);
            }
            var wallet = Manifest(wallets.SelectMany(w => w.Proofs(context.BrandId)), batch, wallets.Sum(w => w.BalanceUsd));
            string originalBody; await using (var original = f.Database.Data.CreateCommand("SELECT row_to_json(t)::text FROM public.orders t")) originalBody = (string)(await original.ExecuteScalarAsync(Ct))!;
            var source = new LegacySourceRecord(LegacyProjection.OrderRecord, "SYNTHETIC-HELD", originalBody); Guid target = Guid.NewGuid();
            var archive = Manifest(wallet.Proofs.Where(p => p.Projection == LegacyProjection.User).Append(source.Proof(target, context, f.Secrets)), Guid.NewGuid(), 0);
            var files = new LegacyArchiveFiles(directory, f.Secrets); var record = await files.WriteAsync(context, archive, source, target, [], Ct);
            var plan = await LegacyWalletOrderPlan.CreateWithZeroOrdersAsync(context, f.Secrets, archive, wallet, destination, "protected:wallet-orders/" + wallet.Batch.ToString("N"), "Legacy source acc-abad (historical public name unknown)", wallets, [record], (_, _, _, _) => throw new MigrationRejectedException(), Ct, true, true, true);
            Assert.Single(plan.Review); await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(context, plan.Manifest, wallets.Single(), Ct, (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(world.Register));
            await Write(Path.Combine(directory, "wallet.plan"), wallet.Encode()); await Write(Path.Combine(directory, "register.json"), world.Register);
            await Write(Path.Combine(directory, "wallet.state.json"), Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 1, ["batch"] = batch.ToString(), ["observed_at"] = observed.ToString("O") }.ToJsonString()));
            await Write(Path.Combine(directory, "original.schema"), Encoding.UTF8.GetBytes(destination.ToFileText()));
            await Write(Path.Combine(directory, "wallet-shared-direct-catalog-plan.sha256"), Encoding.UTF8.GetBytes(fault == "plan_hash" ? new string('0', 64) : Convert.ToHexStringLower(plan.Manifest.Digest.Span)));
            await Write(Path.Combine(directory, "wallet-shared-direct-catalog-review.json"), Encoding.UTF8.GetBytes(Review(plan, fault == "review_reason")));
            string job = new JsonObject { ["work_directory"] = directory, ["wallet_plan_file"] = Path.Combine(directory, "wallet.plan"), ["wallet_state_file"] = Path.Combine(directory, "wallet.state.json"), ["register_file"] = Path.Combine(directory, "register.json") }.ToJsonString();
            await ReadOnlyReconciliationTargetTests.WithReader(f, async reader =>
            {
                var endpoint = new NpgsqlConnectionStringBuilder(reader.ConnectionString) { Password = "synthetic-target-reader-only", Enlist = false };
                await Write(Path.Combine(directory, "reader.connection"), Encoding.UTF8.GetBytes(endpoint.ConnectionString)); await Write(Path.Combine(directory, "backup.sha256"), Encoding.UTF8.GetBytes(new string('a', 64)));
                var fingerprints = new JsonObject(); await using (var tables = f.Database.Data.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname='shop' ORDER BY tablename"))
                {
                    await using var rows = await tables.ExecuteReaderAsync(Ct); while (await rows.ReadAsync(Ct)) fingerprints[rows.GetString(0)] = "pending";
                }
                foreach (string table in fingerprints.Select(p => p.Key).ToArray())
                {
                    await using var q = f.Database.Data.CreateCommand("SELECT count(*)::text||':'||encode(sha256(convert_to(coalesce(string_agg(encode(sha256(convert_to(row_to_json(t)::text,'UTF8')),'hex'),'' ORDER BY encode(sha256(convert_to(row_to_json(t)::text,'UTF8')),'hex')),''),'UTF8')),'hex') FROM shop." + table + " t"); fingerprints[table] = (string)(await q.ExecuteScalarAsync(Ct))!;
                }
                await ReadOnlyTargetFingerprints.VerifyAsync(endpoint.ConnectionString, new string('a', 64), fingerprints.ToJsonString(), Ct);
                if (fault is "target_row" or "fingerprint_row") { await using var change = f.Database.Data.CreateCommand("UPDATE shop.users SET language='fa'"); await change.ExecuteNonQueryAsync(Ct); }
                Task<ReadOnlyTargetEvidence> Run() => ReadOnlyTargetReconciliation.VerifyAsync(world.Source, archive, context, f.Secrets, job, Path.Combine(directory, "original.schema"), Path.Combine(directory, "reader.connection"), Path.Combine(directory, "backup.sha256"), ArchivePrivateFile.ReadText, Ct);
                if (fault == "fingerprint_row") await Assert.ThrowsAsync<MigrationRejectedException>(() => ReadOnlyTargetFingerprints.VerifyAsync(endpoint.ConnectionString, new string('a', 64), fingerprints.ToJsonString(), Ct));
                else if (fault == "valid") { var result = await Run(); Assert.True(result.Projection.ProjectionMatched); Assert.Equal(1, result.OriginalReview.ReviewedRecords); Assert.False(result.FinancialObligationsVerified); Assert.False(result.ReleaseReady); }
                else await Assert.ThrowsAsync<MigrationRejectedException>(() => Run());
                Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM shop.wallets", Ct)); Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM shop.orders", Ct));
            });
        }
        finally { Directory.Delete(directory, true); }
    }
}
