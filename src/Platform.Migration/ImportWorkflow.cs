using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed class ImportWorkflow(LegacySourceReader source, DbSessionFactory destination)
{
    public async Task ImportOrderAsync(BrandContext context, VerifiedImportManifest manifest, string legacyId, Guid target, Guid user, string historicalBrandName, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadOrderAsync(manifest, legacyId, target, ct);
        await new HistoricalOrderImportWorkflow(destination).ImportAsync(context, manifest, new(row, target, user, historicalBrandName), ct);
    }
    public async Task<MigrationReconciliation> ImportWalletAsync(BrandContext context, VerifiedImportManifest manifest, long telegramId, Guid targetUser, Guid targetOpening, IReadOnlyDictionary<string, Guid> historyTargets, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        var row = await source.ReadWalletAsync(manifest, telegramId, targetUser, targetOpening, historyTargets, ct);
        await new OpeningBalanceImportWorkflow(destination).ImportAsync(context, manifest, row, ct);
        return await new ReconciliationReport(destination).ReadAsync(manifest, ct);
    }
}
