using System.Text.Json;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;

public sealed class LegacyDeliveryImportWorkflow(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task ImportAsync(BrandContext context, VerifiedImportManifest manifest, ArchivedLegacyRecord row, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand || !new LegacyRecordArchive(secrets).Matches(context, row, row.Evidence)) throw new MigrationRejectedException();
        var proof = row.Source.Proof(row.Target, context, secrets); manifest.Require(proof);
        await using var session = await sessions.BeginAsync(ct); await LegacyMapStore.RequireDestinationAsync(session, manifest, ct);
        await RequireParentAsync(session, context, manifest, row, ct);
        if (await LegacyMapStore.EnsureAsync(session, manifest, proof, ct)) await LegacyValues.AuditAsync(session, manifest, row, "order", ct);
        if (!await LegacyValues.MatchesAuditAsync(session, manifest, row, "order", ct)) throw new MigrationRejectedException();
        await session.CommitAsync(ct);
    }
    internal static async Task RequireParentAsync(DbSession session, BrandContext context, VerifiedImportManifest manifest, ArchivedLegacyRecord row, CancellationToken ct)
    {
        using var document = row.Source.Open(); var source = document.RootElement;
        var (parent, field) = row.Source.Projection switch
        {
            LegacyProjection.InventoryUnit or LegacyProjection.Delivery or LegacyProjection.PreorderBatch or LegacyProjection.SupplierFulfillment or LegacyProjection.SupplierRefundArchive or LegacyProjection.PreorderState or LegacyProjection.PreorderRefundArchive or LegacyProjection.ServiceJob => (LegacyProjection.OrderRecord, "order_id"),
            LegacyProjection.ZeroTransaction => (LegacyProjection.OrderRecord, "id"),
            LegacyProjection.SupplierItem or LegacyProjection.SupplierAttempt => (LegacyProjection.SupplierFulfillment, "fulfillment_id"),
            LegacyProjection.DeliveryPart or LegacyProjection.DeliveryAttempt => (LegacyProjection.Delivery, "delivery_id"),
            LegacyProjection.PreorderPart => (LegacyProjection.PreorderBatch, "batch_id"),
            LegacyProjection.PreorderItem => (LegacyProjection.PreorderBatch, "preorderdeliverybatch_id"),
            _ => throw new MigrationRejectedException()
        };
        string id = source.GetProperty(field).ValueKind == JsonValueKind.String ? LegacyValues.Text(source, field) : source.GetProperty(field).GetRawText();
        var expected = manifest.Proofs.SingleOrDefault(p => p.Projection == parent && p.LegacyId == id && p.Target == row.Target) ?? throw new MigrationRejectedException();
        bool linked = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.legacy_id_map WHERE source_brand=$1 AND source_table=$2 AND source_key_kind='primary' AND legacy_id=$3 AND target_id=$4 AND import_batch=$5 AND import_manifest_ref=$6)", DbValue.Text(manifest.SourceBrand), DbValue.Text(expected.SourceTable), DbValue.Text(id), DbValue.Uuid(row.Target), DbValue.Uuid(manifest.Batch), DbValue.Text(manifest.Reference)), r => r.GetBoolean(0), ct);
        if (!linked || !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.orders o JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE o.id=$1 AND b.id=$2 AND b.bot_id=$3 AND o.fulfillment_status='requires_admin' AND o.review_code='credential_recovery' AND o.release_authorized_at IS NULL AND o.delivery_generation IS NULL)", DbValue.Uuid(row.Target), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        if (row.Source.Projection == LegacyProjection.ZeroTransaction && (LegacyValues.Text(source, "currency") != "USD" || !await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.orders o JOIN shop.users u ON u.id=o.user_id WHERE o.id=$1 AND u.telegram_id=$2 AND o.payment_status='not_required')", DbValue.Uuid(row.Target), DbValue.Bigint(source.GetProperty("user_id").GetInt64())), r => r.GetBoolean(0), ct))) throw new MigrationRejectedException();
        if (row.Source.Projection == LegacyProjection.PreorderItem)
        {
            string inventory = source.GetProperty("inventoryitem_id").GetRawText();
            if (!manifest.Proofs.Any(p => p.Projection == LegacyProjection.InventoryUnit && p.LegacyId == inventory && p.Target == row.Target)) throw new MigrationRejectedException();
        }
    }
}
