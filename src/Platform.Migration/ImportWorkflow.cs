using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed class ImportWorkflow(LegacySourceReader source, DbSessionFactory destination)
{
    public async Task<LegacyRecordReconciliation> ImportWalletOrdersAsync(BrandContext context, SecretSnapshot secrets, LegacyWalletOrderPlan plan, Func<string, CancellationToken, Task<ProtectedLegacyEvidence>> readArchive, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>> readRetention, LegacyRecordReconciliationReport reconciliation, CancellationToken ct = default)
    {
        var manifest = plan.Manifest; var rows = plan.Set.Wallets;
        if (context.BrandId != manifest.TargetBrand || plan.Set.Orders.Count + plan.Set.Payments.Count + plan.Set.Catalog.Count + plan.Set.Delivery.Count + plan.Review.Count != plan.Records.Count) throw new MigrationRejectedException();
        await source.VerifyWalletCoverageAsync(manifest, ct);
        foreach (var projection in LegacySourceRecord.Projections) await source.VerifyRecordCoverageAsync(context, secrets, plan.Archive, projection, ct);
        var protector = new LegacyRecordArchive(secrets);
        foreach (var row in plan.Records)
        {
            plan.Archive.Require(row.Source.Proof(row.Target, context, secrets));
            if (!protector.Matches(context, row, await readArchive(row.Reference, ct))) throw new MigrationRejectedException();
        }
        foreach (var row in plan.Set.Delivery)
        {
            if (row.Source.Projection != LegacyProjection.ZeroTransaction || !plan.Records.Any(r => r.Source == row.Source) || !plan.Set.Orders.Any(o => o.Record.Target == row.Target && o.NativeZeroSource == row.Source)) throw new MigrationRejectedException();
            manifest.Require(row.Source.Proof(row.Target, context, secrets));
            if (!protector.Matches(context, row, await readArchive(row.Reference, ct))) throw new MigrationRejectedException();
        }
        var retention = await WalletRetentionVerification.ReadAsync(rows.Select(r => r.Retention!), readRetention, ct);
        if (rows.Any(r => r.Retention is null || !retention.Matches(r.Retention))) throw new MigrationRejectedException();
        var clocks = rows.Select(r => r.State?.ObservedAt).Distinct().ToArray();
        if (clocks.Length != 1 || clocks[0] is not DateTime observed) throw new MigrationRejectedException();
        var verified = await source.PlanWalletsAsync(Convert.FromHexString(manifest.SourceSchemaDigest), manifest.Batch, context.BrandId, rows.ToDictionary(r => r.LegacyUser, r => r.Retention!, StringComparer.Ordinal), observed, ct);
        if (verified.Count != rows.Count) throw new MigrationRejectedException();
        foreach (var row in verified) foreach (var proof in row.Proofs(context.BrandId)) manifest.Require(proof);
        foreach (var product in plan.Set.Catalog) { if (plan.Archive.SourceBrand != "acc-abad" || !LegacyCatalogImportWorkflow.RequiresReview(product)) throw new MigrationRejectedException(); manifest.Require(product.Source.Proof(product.Target, context, secrets)); }
        foreach (var order in plan.Set.Orders) foreach (var proof in order.Proofs(context, secrets)) manifest.Require(proof);
        foreach (var payment in plan.Set.Payments) { _ = payment.Terms(manifest); manifest.Require(payment.Record.Source.Proof(payment.Record.Target, context, secrets)); }
        var wallets = new OpeningBalanceImportWorkflow(destination);
        foreach (var row in verified) await wallets.ImportVerifiedAsync(context, manifest, row, retention, ct);
        var catalog = new LegacyCatalogImportWorkflow(destination, secrets, context.BrandId);
        foreach (var product in plan.Set.Catalog) { var result = await catalog.ImportAsync(context, manifest, product, ct); if (!result.RequiresReview) throw new MigrationRejectedException(); }
        var orders = new LegacyOrderRecordImportWorkflow(destination, secrets);
        foreach (var row in plan.Set.Orders) await orders.ImportAsync(context, manifest, row, ct);
        var payments = new LegacyPaymentImportWorkflow(destination, secrets);
        foreach (var row in plan.Set.Payments) await payments.ImportAsync(context, manifest, row, ct);
        var delivery = new LegacyDeliveryImportWorkflow(destination, secrets);
        foreach (var row in plan.Set.Delivery) await delivery.ImportAsync(context, manifest, row, ct);
        var report = await reconciliation.CompareAsync(context, manifest, plan.Set, readArchive, ct, readRetention);
        if (!report.Matched) throw new MigrationRejectedException(); return report;
    }
    public async Task<LegacyRecordReconciliation> ImportRecordsAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest, LegacyImportSet input, Guid approvedMasterBrand, Func<string, CancellationToken, Task<ProtectedLegacyEvidence>> readArchive, LegacyRecordReconciliationReport reconciliation, CancellationToken ct = default, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention = null)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var set = new LegacyImportSet(input.Wallets.Select(w => w with { History = Array.AsReadOnly(w.History.ToArray()) }).ToArray(), input.Orders.ToArray(), input.Payments.ToArray(), input.Coupons.ToArray(), input.Catalog.ToArray(), input.Delivery.ToArray());
        var rows = set.Orders.Select(o => o.Record).Concat(set.Payments.Select(p => p.Record)).Concat(set.Coupons.Select(c => c.Record)).Concat(set.Catalog).Concat(set.Delivery).ToArray();
        if (rows.Select(r => (r.Source.Projection, r.Source.Id)).Distinct().Count() != rows.Length || rows.Length != manifest.Proofs.Count(p => LegacySourceRecord.Projections.Contains(p.Projection)) || set.Wallets.Count != manifest.Proofs.Count(p => p.Projection == LegacyProjection.User)) throw new MigrationRejectedException();
        foreach (var order in set.Orders) foreach (var proof in order.Proofs(context, secrets)) manifest.Require(proof);
        foreach (var coupon in set.Coupons) foreach (var proof in coupon.Proofs(context, secrets)) manifest.Require(proof);
        await source.VerifyWalletCoverageAsync(manifest, ct);
        foreach (var projection in LegacySourceRecord.Projections)
            if (projection != LegacyProjection.ProductRecord || context.BrandId == approvedMasterBrand) await source.VerifyRecordCoverageAsync(context, secrets, manifest, projection, ct);
        foreach (var wallet in set.Wallets)
        {
            if (wallet.Retention is not null && !await wallet.Retention.MatchesAsync(readRetention, ct)) throw new MigrationRejectedException();
            _ = await source.ReadWalletAsync(manifest, wallet.TelegramId, wallet.TargetUser, wallet.TargetOpening, wallet.History.ToDictionary(h => h.LegacyId, h => h.TargetEntry), ct, wallet.State?.ObservedAt, wallet.Retention);
        }
        var archive = new LegacyRecordArchive(secrets);
        foreach (var row in rows)
        {
            _ = await source.ReadRecordAsync(context, secrets, manifest, row.Source.Projection, row.Source.Id, row.Target, ct);
            if (!archive.Matches(context, row, await readArchive(row.Reference, ct))) throw new MigrationRejectedException();
        }
        foreach (var wallet in set.Wallets) await new OpeningBalanceImportWorkflow(destination).ImportAsync(context, manifest, wallet, ct, readRetention);
        foreach (var row in set.Catalog) await new LegacyCatalogImportWorkflow(destination, secrets, approvedMasterBrand).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Orders) await new LegacyOrderRecordImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Payments) await new LegacyPaymentImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Coupons) await new LegacyCouponImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        foreach (var row in set.Delivery.OrderBy(r => r.Source.Projection switch { LegacyProjection.InventoryUnit or LegacyProjection.Delivery or LegacyProjection.PreorderBatch or LegacyProjection.SupplierFulfillment => 0, _ => 1 })) await new LegacyDeliveryImportWorkflow(destination, secrets).ImportAsync(context, manifest, row, ct);
        var result = await reconciliation.CompareAsync(context, manifest, set, readArchive, ct, readRetention);
        if (!result.Matched) throw new MigrationRejectedException(); return result;
    }
    public async Task<WalletRecordReconciliation> ImportWalletsAsync(BrandContext context, VerifiedImportManifest manifest, IReadOnlyList<LegacyWalletSnapshot> input, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>> readRetention, CancellationToken ct = default)
    {
        var rows = input.Select(r => r with { History = Array.AsReadOnly(r.History.ToArray()) }).ToArray();
        if (manifest.TargetBrand != context.BrandId || rows.Length == 0 || rows.Select(r => r.LegacyUser).Distinct(StringComparer.Ordinal).Count() != rows.Length || rows.Select(r => r.TargetUser).Distinct().Count() != rows.Length || rows.Length != manifest.Proofs.Count(p => p.Projection == LegacyProjection.User) || rows.Sum(r => r.BalanceUsd) != manifest.ExpectedOpeningUsd || manifest.Proofs.Any(p => p.Projection is not (LegacyProjection.User or LegacyProjection.WalletSnapshot or LegacyProjection.WalletOpening or LegacyProjection.Transaction))) throw new MigrationRejectedException();
        foreach (var row in rows)
        {
            row.Validate(); row.Retention?.Require(manifest);
            if (row.Retention is null) throw new MigrationRejectedException();
            foreach (var proof in row.Proofs(context.BrandId)) manifest.Require(proof);
        }
        var retention = await WalletRetentionVerification.ReadAsync(rows.Select(r => r.Retention!), readRetention, ct);
        if (rows.Any(r => !retention.Matches(r.Retention!)) || rows.Sum(r => r.Proofs(context.BrandId).Count) != manifest.Proofs.Count) throw new MigrationRejectedException();
        var clocks = rows.Select(r => r.State?.ObservedAt).Distinct().ToArray();
        if (clocks.Length != 1 || clocks[0] is not DateTime observed) throw new MigrationRejectedException();
        var verified = await source.PlanWalletsAsync(Convert.FromHexString(manifest.SourceSchemaDigest), manifest.Batch, context.BrandId, rows.ToDictionary(r => r.LegacyUser, r => r.Retention!, StringComparer.Ordinal), observed, ct);
        if (verified.Count != rows.Length) throw new MigrationRejectedException();
        foreach (var row in verified) foreach (var proof in row.Proofs(context.BrandId)) manifest.Require(proof);
        var writer = new OpeningBalanceImportWorkflow(destination);
        foreach (var row in verified) await writer.ImportVerifiedAsync(context, manifest, row, retention, ct);
        var report = await new ReconciliationReport(destination).CompareWalletsAsync(manifest, rows, ct, readRetention);
        if (!report.Matched) throw new MigrationRejectedException(); return report;
    }
    public async Task ImportOrderAsync(BrandContext context, VerifiedImportManifest manifest, string legacyId, Guid target, Guid user, string historicalBrandName, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadOrderAsync(manifest, legacyId, target, ct);
        await new HistoricalOrderImportWorkflow(destination).ImportAsync(context, manifest, new(row, target, user, historicalBrandName), ct);
    }
    public async Task<MigrationReconciliation> ImportWalletAsync(BrandContext context, VerifiedImportManifest manifest, long telegramId, Guid targetUser, Guid targetOpening, IReadOnlyDictionary<string, Guid> historyTargets, CancellationToken ct = default, DateTime? observedAt = null, LegacyWalletRetention? retention = null, Func<string, CancellationToken, Task<ReadOnlyMemory<byte>>>? readRetention = null)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadWalletAsync(manifest, telegramId, targetUser, targetOpening, historyTargets, ct, observedAt, retention);
        await new OpeningBalanceImportWorkflow(destination).ImportAsync(context, manifest, row, ct, readRetention);
        return await new ReconciliationReport(destination).ReadAsync(manifest, ct);
    }
}
