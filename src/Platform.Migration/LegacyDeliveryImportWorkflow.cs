using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Entities;
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
        bool linked = await session.RunEfStepAsync(false, (data, token) => data.Data.Set<LegacyIdMap>().AnyAsync(m => m.SourceBrand == manifest.SourceBrand && m.SourceTable == expected.SourceTable && m.SourceKeyKind == "primary" && m.LegacyId == id && m.TargetId == row.Target && m.ImportBatch == manifest.Batch && m.ImportManifestRef == manifest.Reference, token), ct);
        if (!linked || !await session.RunEfStepAsync(false, (data, token) => (from o in data.Data.Set<Order>() join u in data.Data.Set<User>() on o.UserId equals u.Id join b in data.Data.Set<Brand>() on u.BrandId equals b.Id where o.Id == row.Target && b.Id == context.BrandId && b.BotId == context.BotId && o.FulfillmentStatus == "requires_admin" && o.ReviewCode == "credential_recovery" && o.ReleaseAuthorizedAt == null && o.DeliveryGeneration == null select o.Id).AnyAsync(token), ct)) throw new MigrationRejectedException();
        if (row.Source.Projection == LegacyProjection.ZeroTransaction)
        {
            long telegram = source.GetProperty("user_id").GetInt64();
            if (LegacyValues.Text(source, "currency") != "USD" || !await session.RunEfStepAsync(false, (data, token) => (from o in data.Data.Set<Order>() join u in data.Data.Set<User>() on o.UserId equals u.Id where o.Id == row.Target && u.TelegramId == telegram && o.PaymentStatus == "not_required" select o.Id).AnyAsync(token), ct)) throw new MigrationRejectedException();
        }
        if (row.Source.Projection == LegacyProjection.PreorderItem)
        {
            string inventory = source.GetProperty("inventoryitem_id").GetRawText();
            if (!manifest.Proofs.Any(p => p.Projection == LegacyProjection.InventoryUnit && p.LegacyId == inventory && p.Target == row.Target)) throw new MigrationRejectedException();
        }
    }
}
