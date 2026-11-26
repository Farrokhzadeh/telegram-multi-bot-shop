using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
using System.Text.Json.Serialization;
using Platform.Core;
using Platform.Persistence;

namespace Platform.Migration;

public sealed record MigrationReconciliation(Guid Batch, long ExpectedMappings, long CommittedMappings, long MissingMappings, long MismatchedMappings,
    decimal OpeningUsd, decimal ExpectedOpeningUsd, decimal TargetWalletUsd, decimal WalletDifferenceUsd, long OpeningPostings, long NonPostingHistory, long PaidUnfulfilledOrders, long HeldCouponUnits, long TargetCiphertexts, long SaleableInventory, bool Complete, long UnexpectedMappings = 0)
{
    public string Scope => "qualified_wallet_projection_only";
    public bool ReleaseReady => false;
    public override string ToString() => "MigrationReconciliation [redacted]";
}
public sealed record RedactedMigrationReconciliation(long ExpectedMappings, long CommittedMappings, long MissingMappings, long MismatchedMappings, long UnexpectedMappings,
    long OpeningPostings, long NonPostingHistory, long PaidUnfulfilledOrders, long HeldCouponUnits, long TargetCiphertexts, long SaleableInventory, bool WalletAmountsMatched, bool WalletProjectionMatched)
{
    public string Scope => "qualified_wallet_projection_only";
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
public sealed record RedactedLegacyRecordReconciliation(long ExpectedRecords, long ComparedRecords, long MismatchedRecords, long InvalidArchives, long MissingMappings, long MismatchedMappings, long UnexpectedMappings, bool WalletMatched, long SaleableInventory, long ExpectedEncryptedFields, long ReencryptedFields, long InvalidRetentions, long ManualIrrAccounts, long RetainedHistoryRows, long RetainedIrrHistory, long RetainedZeroHistory, long UnexpectedActivityEvents, bool ProjectionMatched)
{
    public string Scope => "source_bound_legacy_projections_and_protected_records";
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
public sealed record RedactedLegacySnapshotBrand(string SourceBrand, long SourceTables, long SourceRows, long OriginalArchiveRecords, long RecoveredCipherFields, RedactedLegacyRecordReconciliation Projection, long OriginalRecords, long TypedOrders, long TypedPayments, long OriginalZeroProofs, long CatalogRecords, long QuarantinedRecords, long DerivedZeroFiles, IReadOnlyDictionary<string, int> QuarantineReasons)
{
    public bool PreservationMatched => SourceTables == 56 && OriginalArchiveRecords == OriginalRecords && OriginalRecords == TypedOrders + TypedPayments + OriginalZeroProofs + CatalogRecords + QuarantinedRecords && DerivedZeroFiles == OriginalZeroProofs && Projection.ExpectedRecords >= TypedOrders + TypedPayments + OriginalZeroProofs + CatalogRecords && Projection.ExpectedRecords == Projection.ComparedRecords && Projection.MismatchedRecords == 0 && Projection.InvalidArchives == 0 && Projection.MissingMappings == 0 && Projection.MismatchedMappings == 0 && Projection.UnexpectedMappings == 0 && Projection.WalletMatched && Projection.SaleableInventory == 0 && Projection.ExpectedEncryptedFields == Projection.ReencryptedFields && Projection.InvalidRetentions == 0 && Projection.UnexpectedActivityEvents == 0 && Projection.ProjectionMatched && RecoveredCipherFields >= Projection.ReencryptedFields;
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
public sealed record RedactedLegacySnapshotReconciliation(IReadOnlyList<RedactedLegacySnapshotBrand> Brands)
{
    public string Scope => "frozen_snapshot_preservation_and_quarantine_only";
    public string MediaScope => "preserved_original_references_and_quarantine_only";
    public bool MediaAvailabilityVerified => false;
    public bool PreservationMatched => Brands.Count == 3 && Brands.Select(b => b.SourceBrand).SequenceEqual(new[] { "acc-abad", "kuala", "zikotech" }, StringComparer.Ordinal) && Brands.All(b => b.PreservationMatched);
    public bool FinancialObligationsVerified => false;
    public bool ReleaseReady => false;
}
public sealed class ReconciliationReport(DbSessionFactory sessions, ReadOnlyReconciliationTarget? target = null)
{
    public static RedactedLegacySnapshotReconciliation RedactSnapshot(IReadOnlyList<RedactedLegacySnapshotBrand> brands)
    {
        ArgumentNullException.ThrowIfNull(brands);
        foreach (var brand in brands)
        {
            ArgumentNullException.ThrowIfNull(brand); ArgumentNullException.ThrowIfNull(brand.Projection); ArgumentNullException.ThrowIfNull(brand.QuarantineReasons);
            long[] counts = [brand.SourceTables, brand.SourceRows, brand.OriginalArchiveRecords, brand.RecoveredCipherFields, brand.OriginalRecords, brand.TypedOrders, brand.TypedPayments, brand.OriginalZeroProofs, brand.CatalogRecords, brand.QuarantinedRecords, brand.DerivedZeroFiles];
            if (counts.Any(n => n < 0) || brand.QuarantineReasons.Any(p => p.Value < 0 || string.IsNullOrEmpty(p.Key) || p.Key.Any(c => c is not (>= 'a' and <= 'z') and not '_')) || brand.QuarantineReasons.Sum(p => (long)p.Value) != brand.QuarantinedRecords) throw new MigrationRejectedException();
        }
        try { foreach (var brand in brands) _ = checked(brand.TypedOrders + brand.TypedPayments + brand.OriginalZeroProofs + brand.CatalogRecords + brand.QuarantinedRecords); }
        catch (OverflowException) { throw new MigrationRejectedException(); }
        return new(Array.AsReadOnly(brands.Select(b => b with { QuarantineReasons = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(b.QuarantineReasons.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)) }).ToArray()));
    }
    public static RedactedLegacyRecordReconciliation RedactRecords(LegacyRecordReconciliation report)
    {
        ArgumentNullException.ThrowIfNull(report);
        long[] counts = [report.ExpectedRecords, report.ComparedRecords, report.MismatchedRecords, report.InvalidArchives, report.MissingMappings, report.MismatchedMappings, report.UnexpectedMappings, report.SaleableInventory, report.ExpectedEncryptedFields, report.ReencryptedFields, report.InvalidRetentions, report.ManualIrrAccounts, report.RetainedHistoryRows, report.RetainedIrrHistory, report.RetainedZeroHistory, report.UnexpectedActivityEvents];
        if (counts.Any(count => count < 0)) throw new MigrationRejectedException();
        return new(report.ExpectedRecords, report.ComparedRecords, report.MismatchedRecords, report.InvalidArchives, report.MissingMappings, report.MismatchedMappings, report.UnexpectedMappings, report.WalletMatched, report.SaleableInventory, report.ExpectedEncryptedFields, report.ReencryptedFields, report.InvalidRetentions, report.ManualIrrAccounts, report.RetainedHistoryRows, report.RetainedIrrHistory, report.RetainedZeroHistory, report.UnexpectedActivityEvents, report.Matched);
    }
    public static RedactedMigrationReconciliation Redact(MigrationReconciliation report)
    {
        ArgumentNullException.ThrowIfNull(report);
        long[] counts = [report.ExpectedMappings, report.CommittedMappings, report.MissingMappings, report.MismatchedMappings, report.UnexpectedMappings, report.OpeningPostings, report.NonPostingHistory, report.PaidUnfulfilledOrders, report.HeldCouponUnits, report.TargetCiphertexts, report.SaleableInventory];
        if (counts.Any(count => count < 0)) throw new MigrationRejectedException();
        bool amounts = report.OpeningUsd == report.ExpectedOpeningUsd && report.TargetWalletUsd == report.ExpectedOpeningUsd && report.WalletDifferenceUsd == 0;
        bool matched = report.Complete && amounts && report.ExpectedMappings == report.CommittedMappings && report.MissingMappings == 0 && report.MismatchedMappings == 0 && report.UnexpectedMappings == 0;
        return new(report.ExpectedMappings, report.CommittedMappings, report.MissingMappings, report.MismatchedMappings, report.UnexpectedMappings, report.OpeningPostings, report.NonPostingHistory, report.PaidUnfulfilledOrders, report.HeldCouponUnits, report.TargetCiphertexts, report.SaleableInventory, amounts, matched);
    }
    public async Task<WalletRecordReconciliation> CompareWalletsAsync(VerifiedImportManifest manifest, IReadOnlyList<LegacyWalletSnapshot> source, CancellationToken ct = default, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention = null)
    {
        var rows = source.Select(r => r with { History = Array.AsReadOnly(r.History.ToArray()) }).ToArray();
        if (rows.Select(r => r.TargetUser).Distinct().Count() != rows.Length) throw new MigrationRejectedException();
        foreach (var row in rows) foreach (var proof in row.Proofs(manifest.TargetBrand)) manifest.Require(proof);
        var retained = rows.Where(r => r.Retention is not null).Select(r => r.Retention!).ToArray();
        foreach (var retention in retained) retention.Require(manifest);
        var verification = await WalletRetentionVerification.ReadAsync(retained, readRetention, ct);
        long invalidRetentions = retained.LongCount(r => !verification.Matches(r));
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        if (target is null) await LegacyMapStore.RequireDestinationAsync(session, manifest, ct); else await target.RequireAsync(session, manifest, ct);
        long mismatch = 0, missingMaps = 0, mismatchedMaps = 0, foundMaps = 0;
        foreach (var row in rows)
        {
            if (!await OpeningBalanceImportWorkflow.MatchesWalletAsync(session, manifest, row, ct)) mismatch++;
            foreach (var history in row.History)
            {
                bool found = await OpeningBalanceImportWorkflow.MatchesHistoryAsync(session, manifest.Batch, row.TargetUser, history, ct);
                if (!found) mismatch++;
            }
            foreach (var proof in row.Proofs(manifest.TargetBrand))
            {
                var maps = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().Where(m => m.SourceBrand == manifest.SourceBrand && m.SourceTable == proof.SourceTable && m.SourceKeyKind == proof.KeyKind && m.LegacyId == proof.LegacyId).Select(m => new { Kind = m.TargetKind, Id = m.TargetId, Brand = m.TargetBrandId ?? Guid.Empty, Digest = m.SourceFingerprint, Batch = m.ImportBatch, Reference = m.ImportManifestRef }).Take(1).ToListAsync(token), ct);
                if (maps.Count == 0) missingMaps++;
                else { var value = maps[0]; if (value.Batch == manifest.Batch && value.Reference == manifest.Reference) foundMaps++; if (value.Kind != proof.TargetKind || value.Id != proof.Target || value.Brand != proof.Brand || value.Batch != manifest.Batch || value.Reference != manifest.Reference || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(value.Digest, proof.Fingerprint.Span)) mismatchedMaps++; }
            }
        }
        long expected = manifest.Proofs.LongCount(p => p.Projection == LegacyProjection.WalletSnapshot);
        long expectedHistory = manifest.Proofs.LongCount(p => p.Projection == LegacyProjection.Transaction);
        long expectedOpenings = manifest.Proofs.LongCount(p => p.Projection == LegacyProjection.WalletOpening);
        long actualEntries = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<WalletEntry>().LongCountAsync(w => w.LegacyImportBatch == manifest.Batch && (w.MovementKind == "opening_balance" || w.MovementKind == "legacy_history"), token), ct);
        long expectedEntries = expectedHistory + expectedOpenings;
        long uncovered = Math.Abs(expectedHistory - rows.Sum(r => (long)r.History.Count)) + Math.Abs(expectedOpenings - rows.LongCount(r => r.BalanceUsd > 0)) + Math.Abs(expectedEntries - actualEntries);
        long walletMaps = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().LongCountAsync(m => m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference && (m.SourceTable == "users" || m.SourceTable == "users.balance.snapshot" || m.SourceTable == "users.balance.opening" || m.SourceTable == "transactions"), token), ct);
        return new(manifest.Batch, expected, rows.LongLength, mismatch, missingMaps, mismatchedMaps, uncovered, Math.Max(0, walletMaps - foundMaps), invalidRetentions, rows.LongCount(r => r.Retention?.ManualIrrBalance == true), rows.Sum(r => r.Retention?.HistoryRows ?? 0), rows.Sum(r => r.Retention?.IrrHistoryRows ?? 0), rows.Sum(r => r.Retention?.ZeroHistoryRows ?? 0));
    }
    public async Task<HistoricalOrderReconciliation> CompareOrdersAsync(VerifiedImportManifest manifest, IReadOnlyList<LegacyHistoricalOrder> source, CancellationToken ct = default)
    {
        var rows = source.ToArray();
        if (rows.Select(r => r.Target).Distinct().Count() != rows.Length) throw new MigrationRejectedException();
        foreach (var row in rows) foreach (var proof in row.Proofs(manifest.TargetBrand)) manifest.Require(proof);
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        if (target is null) await LegacyMapStore.RequireDestinationAsync(session, manifest, ct); else await target.RequireAsync(session, manifest, ct);
        long mismatched = 0;
        foreach (var row in rows) if (!await HistoricalOrderImportWorkflow.MatchesAsync(session, row, ct)) mismatched++;
        long expected = manifest.Proofs.LongCount(p => p.Projection == LegacyProjection.HistoricalOrder);
        long unmatched = expected - rows.LongLength;
        var mappings = await ReadAsync(session, manifest, ct, target);
        long batchMappings = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().LongCountAsync(m => m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference, token), ct);
        long expectedKeysInBatch = 0;
        foreach (var proof in manifest.Proofs)
            if (await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().AnyAsync(m => m.SourceBrand == manifest.SourceBrand && m.SourceTable == proof.SourceTable && m.SourceKeyKind == proof.KeyKind && m.LegacyId == proof.LegacyId && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference, token), ct)) expectedKeysInBatch++;
        long unexpected = Math.Max(0, batchMappings - expectedKeysInBatch);
        return new(manifest.Batch, expected, rows.LongLength, mismatched, unmatched, mappings.MissingMappings, mappings.MismatchedMappings, unexpected);
    }
    public async Task<LegacyAggregateReconciliation> CompareAsync(VerifiedImportManifest manifest, LegacyAggregateExpectations expected, CancellationToken ct = default) =>
        LegacyAggregateComparison.Compare(manifest, expected, await ReadAsync(manifest, ct));
    public async Task<MigrationReconciliation> ReadAsync(VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginReadOnlyAsync(ct);
        return await ReadAsync(session, manifest, ct, target);
    }
    internal static async Task<MigrationReconciliation> ReadAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct, ReadOnlyReconciliationTarget? target = null)
    {
        if (target is null) await LegacyMapStore.RequireDestinationAsync(session, manifest, ct); else await target.RequireAsync(session, manifest, ct);
        var coverage = await LegacyMapStore.ReadCoverageAsync(session, manifest, ct, target);
        var money = await session.RunEfStepAsync(false, async (data, token) => { var query = data.Data.Set<WalletEntry>().Where(w => w.LegacyImportBatch == manifest.Batch); return (Opening: await query.Where(w => w.MovementKind == "opening_balance" && w.IsPosting).SumAsync(w => (decimal?)w.SignedAmount, token) ?? 0m, Postings: await query.LongCountAsync(w => w.MovementKind == "opening_balance" && w.IsPosting, token), History: await query.LongCountAsync(w => w.MovementKind == "legacy_history" && !w.IsPosting, token)); }, ct);
        var liabilities = await session.RunEfStepAsync(false, async (data, token) =>
        {
            var orders = from o in data.Data.Set<Order>() join u in data.Data.Set<User>() on o.UserId equals u.Id where u.BrandId == manifest.TargetBrand select o;
            long paid = await orders.LongCountAsync(o => (o.PaymentStatus == "paid" || o.PaymentStatus == "not_required") && o.FulfillmentStatus != "completed" && o.FulfillmentStatus != "cancelled" && o.FulfillmentStatus != "compensated", token);
            long held = await (from d in data.Data.Set<DiscountUse>() join o in orders on d.OrderId equals o.Id where d.Status == "held" select (long?)d.UnitsCovered).SumAsync(token) ?? 0L;
            long ciphers = await (from f in data.Data.Set<Fulfillment>() join o in orders on f.OrderId equals o.Id where f.DeliveredContent != null select f.Id).LongCountAsync(token);
            ciphers += await data.Data.Set<Discount>().LongCountAsync(d => d.BrandId == manifest.TargetBrand && d.CodeContent != null, token);
            ciphers += await (from m in data.Data.Set<SupportMessage>() join t in data.Data.Set<SupportTicket>() on m.TicketId equals t.Id join u in data.Data.Set<User>() on t.UserId equals u.Id where u.BrandId == manifest.TargetBrand && m.BodyContent != null select m.Id).LongCountAsync(token);
            ciphers += await (from p in data.Data.Set<Payment>() join o in orders on p.OrderId equals o.Id where p.BankDestination != null select p.Id).LongCountAsync(token);
            return (Paid: paid, Held: held, Ciphers: ciphers, Stock: await data.Data.Set<InventoryItem>().LongCountAsync(i => i.State == "available", token));
        }, ct);
        long mapped = coverage.BatchCount;
        var balances = await session.RunEfStepAsync(false, async (data, token) => { var query = from w in data.Data.Set<Wallet>() join m in data.Data.Set<LegacyIdMap>() on w.UserId equals m.TargetId where m.TargetKind == "wallet" && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference select w; return (Total: await query.SumAsync(w => (decimal?)w.Balance, token) ?? 0m, Count: await query.LongCountAsync(token)); }, ct);
        bool complete = money.Opening == manifest.ExpectedOpeningUsd && balances.Total == manifest.ExpectedOpeningUsd && balances.Count == manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletSnapshot) && coverage.Missing == 0 && coverage.Mismatched == 0 && coverage.Unexpected == 0 && mapped == manifest.Proofs.Count &&
            money.Postings == manifest.Proofs.Count(p => p.Projection == LegacyProjection.WalletOpening) && money.History == manifest.Proofs.Count(p => p.Projection == LegacyProjection.Transaction);
        return new(manifest.Batch, manifest.Proofs.Count, coverage.Committed, coverage.Missing, coverage.Mismatched, money.Opening, manifest.ExpectedOpeningUsd, balances.Total, balances.Total - manifest.ExpectedOpeningUsd, money.Postings, money.History, liabilities.Paid, liabilities.Held, liabilities.Ciphers, liabilities.Stock, complete, coverage.Unexpected);
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MigrationReconciliation))]
[JsonSerializable(typeof(RedactedMigrationReconciliation))]
[JsonSerializable(typeof(LegacyAggregateReconciliation))]
[JsonSerializable(typeof(HistoricalOrderReconciliation))]
[JsonSerializable(typeof(WalletRecordReconciliation))]
[JsonSerializable(typeof(LegacyRecordReconciliation))]
[JsonSerializable(typeof(RedactedLegacyRecordReconciliation))]
[JsonSerializable(typeof(ProtectedLegacyEvidence))]
[JsonSerializable(typeof(RedactedLegacySnapshotBrand))]
[JsonSerializable(typeof(RedactedLegacySnapshotReconciliation))]
public sealed partial class MigrationReportJsonContext : JsonSerializerContext;
public sealed record WalletRecordReconciliation(Guid Batch, long ExpectedWallets, long ComparedWallets, long MismatchedRecords, long MissingMappings, long MismatchedMappings, long UnmatchedEntries, long UnexpectedMappings, long InvalidRetentions = 0, long ManualIrrAccounts = 0, long RetainedHistoryRows = 0, long RetainedIrrHistory = 0, long RetainedZeroHistory = 0)
{
    public bool Matched => ExpectedWallets == ComparedWallets && MismatchedRecords == 0 && MissingMappings == 0 && MismatchedMappings == 0 && UnmatchedEntries == 0 && UnexpectedMappings == 0 && InvalidRetentions == 0;
    public bool ReleaseReady => false;
    public string Scope => "source_bound_wallet_records_only";
    public override string ToString() => "WalletRecordReconciliation [redacted]";
}
public sealed record HistoricalOrderReconciliation(Guid Batch, long ExpectedOrders, long ComparedOrders, long MismatchedOrders, long UnmatchedOrders, long MissingMappings, long MismatchedMappings, long UnexpectedMappings)
{
    public bool Matched => ExpectedOrders == ComparedOrders && MismatchedOrders == 0 && UnmatchedOrders == 0 && MissingMappings == 0 && MismatchedMappings == 0 && UnexpectedMappings == 0;
    public bool ReleaseReady => false;
    public string Scope => "source_bound_wallet_paid_order_snapshots";
    public override string ToString() => "HistoricalOrderReconciliation [redacted]";
}
