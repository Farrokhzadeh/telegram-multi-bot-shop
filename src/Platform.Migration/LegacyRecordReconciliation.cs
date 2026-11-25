using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Data;
using Npgsql;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed record LegacyImportSet(IReadOnlyList<LegacyWalletSnapshot> Wallets, IReadOnlyList<LegacyHistoricalRecord> Orders, IReadOnlyList<LegacyPaymentRecord> Payments, IReadOnlyList<LegacyCouponRecord> Coupons, IReadOnlyList<ArchivedLegacyRecord> Catalog, IReadOnlyList<ArchivedLegacyRecord> Delivery)
{
    public override string ToString() => "LegacyImportSet [redacted]";
}
public sealed record LegacyRecordReconciliation(Guid Batch, long ExpectedRecords, long ComparedRecords, long MismatchedRecords, long InvalidArchives, long MissingMappings, long MismatchedMappings, long UnexpectedMappings, bool WalletMatched, long SaleableInventory, long ExpectedEncryptedFields, long ReencryptedFields, long InvalidRetentions = 0, long ManualIrrAccounts = 0, long RetainedHistoryRows = 0, long RetainedIrrHistory = 0, long RetainedZeroHistory = 0, long UnexpectedActivityEvents = 0)
{
    public bool Matched => ExpectedRecords == ComparedRecords && MismatchedRecords == 0 && InvalidArchives == 0 && MissingMappings == 0 && MismatchedMappings == 0 && UnexpectedMappings == 0 && WalletMatched && SaleableInventory == 0 && ExpectedEncryptedFields == ReencryptedFields && InvalidRetentions == 0 && UnexpectedActivityEvents == 0;
    public bool ReleaseReady => false;
    public string Scope => "source_bound_legacy_projections_and_protected_records";
    public override string ToString() => "LegacyRecordReconciliation [redacted]";
}
public sealed class LegacyRecordReconciliationReport(NpgsqlDataSource destination, SecretSnapshot secrets, ReadOnlyReconciliationTarget? target = null)
{
    private readonly DbSessionFactory sessions = new(destination, new DbSessionOptions { ReadOnly = true, Isolation = IsolationLevel.RepeatableRead });
    public async Task<LegacyRecordReconciliation> CompareAsync(BrandContext context, VerifiedImportManifest manifest, LegacyImportSet source, Func<string, CancellationToken, Task<ProtectedLegacyEvidence>> readArchive, CancellationToken ct = default, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention = null)
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
        long invalidRetentions = 0;
        var retentions = await WalletRetentionVerification.ReadAsync(wallets.Where(w => w.Retention is not null).Select(w => w.Retention!), readRetention, ct);
        foreach (var wallet in wallets.Where(w => w.Retention is not null)) { wallet.Retention!.Require(manifest); if (!retentions.Matches(wallet.Retention)) invalidRetentions++; }
        long invalidArchives = 0, reencrypted = 0; var archive = new LegacyRecordArchive(secrets);
        foreach (var row in records)
        {
            try { var evidence = await readArchive(row.Reference, ct); if (!archive.Matches(context, row, evidence)) invalidArchives++; else reencrypted += evidence.ReencryptedFields?.Count ?? 0; }
            catch (Exception error) when (error is IOException or KeyNotFoundException) { invalidArchives++; }
        }
        await using var session = await sessions.BeginAsync(ct); if (target is null) await LegacyMapStore.RequireDestinationAsync(session, manifest, ct); else await target.RequireAsync(session, manifest, ct);
        var mapping = await ReconciliationReport.ReadAsync(session, manifest, ct, target); long mismatched = 0;
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
        long unexpectedActivity = await session.RunEfStepAsync(false, (data, token) => UnexpectedActivityAsync(data, manifest, context.BrandId, token), ct);
        long expected = manifest.Proofs.LongCount(p => p.Projection is LegacyProjection.WalletSnapshot or LegacyProjection.OrderRecord or LegacyProjection.PaymentAttempt or LegacyProjection.ManualDeposit or LegacyProjection.InventoryUnit or LegacyProjection.Delivery or LegacyProjection.DeliveryPart or LegacyProjection.PreorderBatch or LegacyProjection.PreorderPart or LegacyProjection.PreorderItem or LegacyProjection.DiscountRecord or LegacyProjection.ProductRecord or LegacyProjection.SupplierFulfillment or LegacyProjection.SupplierItem or LegacyProjection.SupplierAttempt or LegacyProjection.SupplierRefundArchive or LegacyProjection.PreorderState or LegacyProjection.PreorderRefundArchive or LegacyProjection.ServiceJob or LegacyProjection.ZeroTransaction or LegacyProjection.DeliveryAttempt);
        return new(manifest.Batch, expected, records.LongLength + wallets.LongLength, mismatched, invalidArchives, mapping.MissingMappings, mapping.MismatchedMappings, mapping.UnexpectedMappings, walletMatched, mapping.SaleableInventory, records.Sum(r => (long)LegacyRecordArchive.EncryptedFields(r.Source).Count), reencrypted, invalidRetentions, wallets.LongCount(w => w.Retention?.ManualIrrBalance == true), wallets.Sum(w => w.Retention?.HistoryRows ?? 0), wallets.Sum(w => w.Retention?.IrrHistoryRows ?? 0), wallets.Sum(w => w.Retention?.ZeroHistoryRows ?? 0), unexpectedActivity);
    }
    private static async Task<long> UnexpectedActivityAsync(EfSession data, VerifiedImportManifest manifest, Guid brand, CancellationToken ct)
    {
        Guid? after = null; long unexpected = 0;
        while (true)
        {
            var rows = await (from e in data.Data.Set<BotActivityEvent>().AsNoTracking() join u in data.Data.Set<User>() on e.UserId equals u.Id where u.BrandId == brand && (after == null || e.Id.CompareTo(after.Value) > 0) orderby e.Id select e).Take(128).ToListAsync(ct);
            foreach (var row in rows)
            {
                bool valid = row.SourceKind == "system" && row.OutcomeCode == "accepted" && row.SupportTicketId == null && row.FlowRevision == null && row.TelegramUpdateId == null;
                string prefix = "workflow:" + row.ActionCode + ":";
                if (!valid || !row.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(row.IdempotencyKey[prefix.Length..], "N", out Guid id) || row.IdempotencyKey != prefix + id.ToString("N")) { unexpected++; continue; }
                if (row.EventType == "payment" && row.ActionCode == "topup_created" && row.ProductId == null && row.OrderId == null && row.QuantitySelected == null)
                    valid = await (from p in data.Data.Set<Payment>() join m in data.Data.Set<LegacyIdMap>() on p.Id equals m.TargetId where m.TargetKind == "payment" && m.SourceBrand == manifest.SourceBrand && m.SourceTable == "payment_attempts" && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference && p.Id == id && p.TopupUserId == row.UserId && p.OrderId == null && p.PaymentMethod == "crypto" && p.LegacyKind == "payment_attempt" && p.CreatedAt == row.OccurredAt select p.Id).AnyAsync(ct);
                else if (row.EventType == "checkout" && row.ActionCode == "checkout_funded" && row.ProductId != null)
                    valid = await (from o in data.Data.Set<Order>() join m in data.Data.Set<LegacyIdMap>() on o.Id equals m.TargetId where m.TargetKind == "order" && m.SourceBrand == manifest.SourceBrand && m.SourceTable == "orders.record" && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference && o.Id == id && o.Id == row.OrderId && o.UserId == row.UserId && o.ProductId == row.ProductId && o.Quantity == row.QuantitySelected && (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && o.FulfillmentStatus == "requires_admin" && o.ReleaseAuthorizedAt == null && o.PaymentConfirmedAt == row.OccurredAt select o.Id).AnyAsync(ct);
                else valid = false;
                if (!valid) unexpected++;
            }
            if (rows.Count < 128) return unexpected; after = rows[^1].Id;
        }
    }

}
