using System.Data;
using Npgsql;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyImportSet(IReadOnlyList<LegacyWalletSnapshot> Wallets, IReadOnlyList<LegacyHistoricalRecord> Orders, IReadOnlyList<LegacyPaymentRecord> Payments, IReadOnlyList<LegacyCouponRecord> Coupons, IReadOnlyList<ArchivedLegacyRecord> Catalog, IReadOnlyList<ArchivedLegacyRecord> Delivery)
{
    public override string ToString() => "LegacyImportSet [redacted]";
}
public sealed record LegacyRecordReconciliation(Guid Batch, long ExpectedRecords, long ComparedRecords, long MismatchedRecords, long InvalidArchives, long MissingMappings, long MismatchedMappings, long UnexpectedMappings, bool WalletMatched, long SaleableInventory, long ExpectedEncryptedFields, long ReencryptedFields)
{
    public bool Matched => ExpectedRecords == ComparedRecords && MismatchedRecords == 0 && InvalidArchives == 0 && MissingMappings == 0 && MismatchedMappings == 0 && UnexpectedMappings == 0 && WalletMatched && SaleableInventory == 0 && ExpectedEncryptedFields == ReencryptedFields;
    public bool ReleaseReady => false;
    public string Scope => "source_bound_legacy_projections_and_protected_records";
    public override string ToString() => "LegacyRecordReconciliation [redacted]";
}
public sealed class LegacyRecordReconciliationReport(NpgsqlDataSource destination, SecretSnapshot secrets)
{
    private readonly DbSessionFactory sessions = new(destination, new DbSessionOptions { ReadOnly = true, Isolation = IsolationLevel.RepeatableRead });
    public async Task<LegacyRecordReconciliation> CompareAsync(BrandContext context, VerifiedImportManifest manifest, LegacyImportSet source, Func<string, CancellationToken, Task<ProtectedLegacyEvidence>> readArchive, CancellationToken ct = default)
    {
        if (manifest.TargetBrand != context.BrandId) throw new MigrationRejectedException();
        var orders = source.Orders.ToArray(); var payments = source.Payments.ToArray(); var coupons = source.Coupons.ToArray(); var catalog = source.Catalog.ToArray(); var delivery = source.Delivery.ToArray();
        var wallets = source.Wallets.Select(w => w with { History = Array.AsReadOnly(w.History.ToArray()) }).ToArray();
        var records = orders.Select(o => o.Record).Concat(payments.Select(p => p.Record)).Concat(coupons.Select(c => c.Record)).Concat(catalog).Concat(delivery).ToArray();
        if (records.Select(r => (r.Source.Projection, r.Source.Id)).Distinct().Count() != records.Length || wallets.Select(w => w.TargetUser).Distinct().Count() != wallets.Length) throw new MigrationRejectedException();
        foreach (var row in records) manifest.Require(row.Source.Proof(row.Target, context, secrets));
        foreach (var row in orders) foreach (var proof in row.Proofs(context, secrets)) manifest.Require(proof);
        foreach (var row in coupons) foreach (var proof in row.Proofs(context, secrets)) manifest.Require(proof);
        foreach (var row in wallets) foreach (var proof in row.Proofs(context.BrandId)) manifest.Require(proof);
        long invalidArchives = 0, reencrypted = 0; var archive = new LegacyRecordArchive(secrets);
        foreach (var row in records)
        {
            try { var evidence = await readArchive(row.Reference, ct); if (!archive.Matches(context, row, evidence)) invalidArchives++; else reencrypted += evidence.ReencryptedFields?.Count ?? 0; }
            catch (Exception error) when (error is IOException or KeyNotFoundException) { invalidArchives++; }
        }
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        var mapping = await ReconciliationReport.ReadAsync(session, manifest, ct); long mismatched = 0;
        foreach (var row in orders) if (!await LegacyOrderRecordImportWorkflow.MatchesAsync(session, row, ct)) mismatched++;
        foreach (var row in payments) if (!await LegacyPaymentImportWorkflow.MatchesAsync(session, manifest, row, ct)) mismatched++;
        var couponStore = new LegacyCouponImportWorkflow(sessions, secrets);
        foreach (var row in coupons)
        {
            try { if (!await couponStore.MatchesAsync(session, context, manifest, row, ct)) mismatched++; }
            catch (CipherRejectedException) { mismatched++; }
        }
        foreach (var row in catalog) if (!await LegacyCatalogImportWorkflow.MatchesAsync(session, manifest, row, ct)) mismatched++;
        foreach (var row in delivery)
        {
            try { await LegacyDeliveryImportWorkflow.RequireParentAsync(session, context, manifest, row, ct); }
            catch (MigrationRejectedException) { mismatched++; }
        }
        foreach (var row in records)
        {
            string kind = row.Source.Projection switch { LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit => "payment", LegacyProjection.ProductRecord => "product", LegacyProjection.DiscountRecord => "discount", _ => "order" };
            if (!await LegacyValues.MatchesAuditAsync(session, manifest, row, kind, ct)) mismatched++;
        }
        bool walletMatched = mapping.OpeningUsd == manifest.ExpectedOpeningUsd && mapping.TargetWalletUsd == manifest.ExpectedOpeningUsd && mapping.WalletDifferenceUsd == 0 &&
            mapping.OpeningPostings == manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletOpening) && mapping.NonPostingHistory == manifest.Proofs.Count(p => p.Projection == LegacyProjection.Transaction) && wallets.Length == manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletSnapshot);
        foreach (var wallet in wallets)
        {
            if (!await OpeningBalanceImportWorkflow.MatchesWalletAsync(session, manifest, wallet, ct)) walletMatched = false;
            foreach (var history in wallet.History) if (!await OpeningBalanceImportWorkflow.MatchesHistoryAsync(session, manifest.Batch, wallet.TargetUser, history, ct)) walletMatched = false;
        }
        long batchMaps = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.legacy_id_map WHERE import_batch=$1 AND import_manifest_ref=$2", DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference)), r => r.GetInt64(0), ct);
        long knownMaps = 0;
        foreach (var proof in manifest.Proofs)
            if (await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.legacy_id_map WHERE source_brand=$1 AND source_table=$2 AND source_key_kind=$3 AND legacy_id=$4 AND import_batch=$5 AND import_manifest_ref=$6)", DbValue.Text(manifest.SourceBrand), DbValue.Text(proof.SourceTable), DbValue.Text(proof.KeyKind), DbValue.Text(proof.LegacyId), DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference)), r => r.GetBoolean(0), ct)) knownMaps++;
        long expected = manifest.Proofs.LongCount(p => p.Projection is LegacyProjection.WalletSnapshot or LegacyProjection.OrderRecord or LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit or LegacyProjection.InventoryUnit or LegacyProjection.Delivery or LegacyProjection.DeliveryPart or LegacyProjection.PreorderBatch or LegacyProjection.PreorderPart or LegacyProjection.PreorderItem or LegacyProjection.DiscountRecord or LegacyProjection.ProductRecord or LegacyProjection.SupplierFulfillment or LegacyProjection.SupplierItem or LegacyProjection.SupplierAttempt or LegacyProjection.SupplierRefundArchive or LegacyProjection.PreorderState or LegacyProjection.PreorderRefundArchive or LegacyProjection.ServiceJob or LegacyProjection.ZeroTransaction);
        return new(manifest.Batch, expected, records.LongLength + wallets.LongLength, mismatched, invalidArchives, mapping.MissingMappings, mapping.MismatchedMappings, Math.Max(0, batchMaps - knownMaps), walletMatched, mapping.SaleableInventory, records.Sum(r => (long)LegacyRecordArchive.EncryptedFields(r.Source).Count), reencrypted);
    }
}
