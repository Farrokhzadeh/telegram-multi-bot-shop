using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
using Platform.Security;
namespace Platform.SchemaRunner;

internal sealed record ReadOnlyTargetEvidence(string SourceBrand, RedactedLegacyRecordReconciliation Projection, LegacyRecordDisposition Disposition, LegacyOriginalReviewAudit OriginalReview, long DerivedZeroProofs)
{
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
internal static class ReadOnlyTargetReconciliation
{
    internal static async Task<ReadOnlyTargetEvidence> VerifyAsync(LegacySourceReader source, VerifiedImportManifest archive, BrandContext context, SecretSnapshot secrets, string job, string destinationFile, string connectionFile, string backupFile, Func<string, string> read, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(job); var root = document.RootElement;
        string Value(string name) => root.GetProperty(name).GetString() ?? throw new MigrationRejectedException();
        string directory = Value("work_directory"); byte[] walletBytes = ArchivePrivateFile.Read(Value("wallet_plan_file"), 128), register = Encoding.UTF8.GetBytes(read(Value("register_file")));
        try
        {
            var wallet = VerifiedImportManifest.Open("protected:history-archive/verified-wallet-plan", SHA256.HashData(walletBytes), walletBytes);
            if (wallet.SourceBrand != archive.SourceBrand || wallet.TargetBrand != context.BrandId || wallet.SourceSchemaDigest != archive.SourceSchemaDigest || wallet.SourceRevision != archive.SourceRevision || wallet.SnapshotDigest != archive.SnapshotDigest) throw new MigrationRejectedException();
            using var state = JsonDocument.Parse(read(Value("wallet_state_file"))); var stateRoot = state.RootElement;
            if (stateRoot.GetProperty("version").GetInt32() != 1 || stateRoot.GetProperty("batch").GetGuid() != wallet.Batch) throw new MigrationRejectedException();
            DateTime observed = DateTime.ParseExact(stateRoot.GetProperty("observed_at").GetString() ?? "", "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            const string retentionReference = "protected:wallet-copy/original-native-history";
            var retained = LegacyWalletRetention.OpenAll(retentionReference, SHA256.HashData(register), register, archive.SourceBrand, archive.SnapshotDigest);
            var wallets = await source.PlanWalletsAsync(Convert.FromHexString(wallet.SourceSchemaDigest), wallet.Batch, context.BrandId, retained, observed, ct);
            var files = new LegacyArchiveFiles(directory, secrets); var records = new List<ArchivedLegacyRecord>(); long bytes = 0;
            foreach (var proof in archive.Proofs.Where(p => LegacySourceRecord.Projections.Contains(p.Projection)).OrderBy(p => p.Projection).ThenBy(p => p.LegacyId, StringComparer.Ordinal))
            {
                var row = await files.ReadAsync(context, archive, proof.Target, ct); bytes = checked(bytes + Encoding.UTF8.GetByteCount(row.Source.Body));
                if (records.Count >= 100000 || bytes > 67108864) throw new MigrationRejectedException(); records.Add(row);
            }
            string zeroDirectory = Path.Combine(directory, "teleg-history-archive-shared-catalog"); var zeroFiles = new LegacyArchiveFiles(zeroDirectory, secrets);
            var zeroDigests = new Dictionary<Guid, byte[]>(); var destination = SchemaManifest.Parse(read(destinationFile));
            var plan = await LegacyWalletOrderPlan.CreateWithZeroOrdersAsync(context, secrets, archive, wallet, destination, "protected:wallet-orders/" + wallet.Batch.ToString("N"), "Legacy source " + archive.SourceBrand + " (historical public name unknown)", wallets, records, async (manifest, zero, target, token) =>
            {
                byte[] original = ArchivePrivateFile.Read(Path.Combine(zeroDirectory, target.ToString("N") + ".zero.archive.json"), 128);
                try { if (!zeroDigests.TryAdd(target, SHA256.HashData(original))) throw new MigrationRejectedException(); return await zeroFiles.ReadZeroAsync(context, manifest, target, token); } finally { CryptographicOperations.ZeroMemory(original); }
            }, ct, true, true, true);
            string frozenHash = read(Path.Combine(directory, "wallet-shared-direct-catalog-plan.sha256"));
            if (frozenHash != Convert.ToHexStringLower(plan.Manifest.Digest.Span)) throw new MigrationRejectedException();
            var audit = ReadOnlyQuarantineVerification.Verify(plan, read(Path.Combine(directory, "wallet-shared-direct-catalog-review.json")));
            string connection = read(connectionFile).Trim(), backup = read(backupFile).Trim();
            await using var target = new NpgsqlSlimDataSourceBuilder(connection).Build();
            var grant = await ReadOnlyReconciliationTarget.VerifyAsync(target, plan.Manifest, backup, true, ct);
            var byReference = records.ToDictionary(r => r.Reference, StringComparer.Ordinal); var derived = plan.Set.Delivery.ToDictionary(r => r.Reference, StringComparer.Ordinal);
            async Task<ProtectedLegacyEvidence> ReadArchive(string reference, CancellationToken token)
            {
                if (derived.TryGetValue(reference, out var zero)) return (await zeroFiles.ReadZeroAsync(context, plan.Manifest, zero.Target, token)).Evidence;
                if (!byReference.TryGetValue(reference, out var row)) throw new MigrationRejectedException();
                return (await files.ReadAsync(context, archive, row.Target, token)).Evidence;
            }
            Task<ReadOnlyMemory<byte>> ReadRetention(string reference, CancellationToken token)
            { token.ThrowIfCancellationRequested(); if (reference != retentionReference) throw new MigrationRejectedException(); return Task.FromResult<ReadOnlyMemory<byte>>(register); }
            var projection = await new LegacyRecordReconciliationReport(target, secrets, grant).CompareAsync(context, plan.Manifest, plan.Set, ReadArchive, ct, ReadRetention);
            if (!projection.Matched || projection.ReleaseReady) throw new MigrationRejectedException();
            await RequireInactiveTargetAsync(target, grant, context, plan, ct);
            if (zeroDigests.Count != plan.Set.Delivery.Count) throw new MigrationRejectedException();
            foreach (var item in zeroDigests)
            {
                byte[] original = ArchivePrivateFile.Read(Path.Combine(zeroDirectory, item.Key.ToString("N") + ".zero.archive.json"), 128);
                try { if (!CryptographicOperations.FixedTimeEquals(item.Value, SHA256.HashData(original))) throw new MigrationRejectedException(); } finally { CryptographicOperations.ZeroMemory(original); }
            }
            return new(archive.SourceBrand, ReconciliationReport.RedactRecords(projection), LegacyRecordDispositionReport.Verify(plan), audit, plan.Set.Delivery.Count);
        }
        finally { CryptographicOperations.ZeroMemory(walletBytes); CryptographicOperations.ZeroMemory(register); }
    }
    private static async Task RequireInactiveTargetAsync(NpgsqlDataSource target, ReadOnlyReconciliationTarget grant, BrandContext context, LegacyWalletOrderPlan plan, CancellationToken ct)
    {
        await using var session = await new DbSessionFactory(target).BeginReadOnlyAsync(ct); await grant.RequireAsync(session, plan.Manifest, ct);
        var tables = await session.QueryManyAsync(new DbStatement("SELECT tablename FROM pg_tables WHERE schemaname='shop' ORDER BY tablename"), r => r.GetString(0), 1000, ct);
        string[] populated = ["brands", "users", "wallets", "wallet_entries", "legacy_id_map", "audit_events", "bot_activity_events", "orders", "payments", "products", "brand_products"];
        if (tables.Count != 28) throw new MigrationRejectedException();
        foreach (string table in tables.Where(t => !populated.Contains(t, StringComparer.Ordinal))) if (await session.RunEfStepAsync(false, (data, token) => SnapshotCopyQueries.CountAsync(data.Data, table, token), ct) != 0) throw new MigrationRejectedException();
        bool valid = await session.RunEfStepAsync(false, async (data, token) =>
        {
            var users = data.Data.Set<User>().Where(u => u.BrandId == context.BrandId);
            var orders = data.Data.Set<Order>().Where(o => users.Any(u => u.Id == o.UserId));
            return await users.LongCountAsync(token) == plan.Set.Wallets.Count && await data.Data.Set<Wallet>().LongCountAsync(w => users.Any(u => u.Id == w.UserId), token) == plan.Set.Wallets.Count && await orders.LongCountAsync(token) == plan.Set.Orders.Count && await data.Data.Set<Payment>().LongCountAsync(p => users.Any(u => u.Id == p.TopupUserId || orders.Any(o => o.Id == p.OrderId && o.UserId == u.Id)), token) == plan.Set.Payments.Count && await data.Data.Set<BrandProduct>().LongCountAsync(b => b.BrandId == context.BrandId, token) == plan.Set.Catalog.Count && !await data.Data.Set<Brand>().AnyAsync(b => b.Active || b.BotToken != new byte[] { 1 }, token) && await data.Data.Set<Brand>().LongCountAsync(token) == 3 && !await data.Data.Set<Product>().AnyAsync(p => p.Active || !data.Data.Set<BrandProduct>().Any(b => b.ProductId == p.Id), token) && !await data.Data.Set<BrandProduct>().AnyAsync(b => b.Visible, token) && !await data.Data.Set<Order>().AnyAsync(o => (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && (o.FulfillmentStatus != "requires_admin" || o.ReleaseAuthorizedAt != null), token) && !await data.Data.Set<LegacyIdMap>().AnyAsync(m => m.SourceBrand != "acc-abad" && m.SourceBrand != "kuala" && m.SourceBrand != "zikotech", token) && await data.Data.Set<Brand>().AnyAsync(b => b.Id == context.BrandId && b.BotId == context.BotId && !b.Active && b.BotToken == new byte[] { 1 }, token);
        }, ct);
        if (!valid) throw new MigrationRejectedException();
        await grant.RequireAsync(session, plan.Manifest, ct);
    }
}
