using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed class ImportWorkflow(LegacySourceReader source, DbSessionFactory destination)
{
    public async Task<LegacyRecordReconciliation> ImportRecordsAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest, LegacyImportSet input, Guid approvedMasterBrand, Func<string, CancellationToken, Task<ProtectedLegacyEvidence>> readArchive, LegacyRecordReconciliationReport reconciliation, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var set = new LegacyImportSet(input.Wallets.Select(w => w with { History = Array.AsReadOnly(w.History.ToArray()) }).ToArray(), input.Orders.ToArray(), input.Payments.ToArray(), input.Coupons.ToArray(), input.Catalog.ToArray(), input.Delivery.ToArray());
        var rows = set.Orders.Select(o => o.Record).Concat(set.Payments.Select(p => p.Record)).Concat(set.Coupons.Select(c => c.Record)).Concat(set.Catalog).Concat(set.Delivery).ToArray();
        if (rows.Select(r => (r.Source.Projection, r.Source.Id)).Distinct().Count() != rows.Length || rows.Length != manifest.Proofs.Count(p => LegacySourceRecord.Projections.Contains(p.Projection)) || set.Wallets.Count != manifest.Proofs.Count(p => p.Projection == LegacyProjection.User)) throw new MigrationRejectedException();
        await source.VerifyWalletCoverageAsync(manifest, ct);
        foreach (var projection in LegacySourceRecord.Projections)
            if (projection != LegacyProjection.ProductRecord || context.BrandId == approvedMasterBrand) await source.VerifyRecordCoverageAsync(context, secrets, manifest, projection, ct);
        foreach (var wallet in set.Wallets) _ = await source.ReadWalletAsync(manifest, wallet.TelegramId, wallet.TargetUser, wallet.TargetOpening, wallet.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry), ct, wallet.State?.ObservedAt);
        var archive = new LegacyRecordArchive(secrets);
        foreach (var row in rows)
        {
            _ = await source.ReadRecordAsync(context, secrets, manifest, row.Source.Projection, row.Source.Id, row.Target, ct);
            if (!archive.Matches(context, row, await readArchive(row.Reference, ct))) throw new MigrationRejectedException();
        }
        foreach (var wallet in set.Wallets) await new OpeningBalanceImportWorkflow(destination).ImportAsync(context, manifest, wallet, ct);
        foreach (var row in set.Catalog) await new LegacyCatalogImportWorkflow(destination, secrets, approvedMasterBrand).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Orders) await new LegacyOrderRecordImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Payments) await new LegacyPaymentImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Coupons) await new LegacyCouponImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Delivery.OrderBy(r => r.Source.Projection switch { LegacyProjection.InventoryUnit or LegacyProjection.Delivery or LegacyProjection.PreorderBatch or LegacyProjection.SupplierFulfillment => 0, _ => 1 })) await new LegacyDeliveryImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        var result = await reconciliation.CompareAsync(context, manifest, set, readArchive, ct);
        if (!result.Matched) throw new MigrationRejectedException(); return result;
    }
    public async Task ImportOrderAsync(BrandContext context, VerifiedImportManifest manifest, string legacyId, Guid target, Guid user, string historicalBrandName, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadOrderAsync(manifest, legacyId, target, ct);
        await new HistoricalOrderImportWorkflow(destination).ImportAsync(context, manifest, new(row, target, user, historicalBrandName), ct);
    }
    public async Task<MigrationReconciliation> ImportWalletAsync(BrandContext context, VerifiedImportManifest manifest, long telegramId, Guid targetUser, Guid targetOpening, IReadOnlyDictionary<string, Guid> historyTargets, CancellationToken ct = default, DateTime? observedAt = null)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadWalletAsync(manifest, telegramId, targetUser, targetOpening, historyTargets, ct, observedAt);
        await new OpeningBalanceImportWorkflow(destination).ImportAsync(context, manifest, row, ct);
        return await new ReconciliationReport(destination).ReadAsync(manifest, ct);
    }
}
