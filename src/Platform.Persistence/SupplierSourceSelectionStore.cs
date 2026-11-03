using Platform.Security;

namespace Platform.Persistence;

public sealed class SupplierSourceSelectionStore(DbSessionFactory sessions)
{
    public async Task<long> SelectAsync(AuthenticatedAdmin actor, Guid product, Guid source, long productRevision, long sourceRevision, Guid command, CancellationToken ct = default)
    {
        AdminCommand.Revision(productRevision); AdminCommand.Revision(sourceRevision);
        if (product == Guid.Empty || source == Guid.Empty || sourceRevision == 0) throw new ArgumentException("Invalid supplier source selection.");
        byte[] hash = AdminCommand.Hash("supplier-source-selection-v1", source.ToString("D"), sourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.SelectSource, AuditEntity.Product, product, null, AuditReason.ConfigurationChange, command, hash, productRevision, productRevision + 1), ct)) return productRevision + 1;
            var current = await session.QuerySingleAsync(new DbStatement("SELECT revision,fulfillment_kind FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => (Revision: r.GetInt64(0), Kind: r.GetString(1)), ct);
            if (current.Revision != productRevision || current.Kind != "supplier") throw new InvalidOperationException("Product revision or supplier policy conflict.");
            bool valid = await session.QuerySingleAsync(new DbStatement("""
                SELECT s.id IS NOT NULL FROM (SELECT 1) seed LEFT JOIN LATERAL
                    (SELECT id FROM shop.product_supplier_sources WHERE id=$1 AND product_id=$2 AND revision=$3 AND enabled FOR UPDATE) s ON true
                """, DbValue.Uuid(source), DbValue.Uuid(product), DbValue.Bigint(sourceRevision)), r => r.GetBoolean(0), ct);
            if (!valid) throw new InvalidOperationException("Supplier source revision or eligibility conflict.");
            await session.ExecuteAsync(new DbStatement("UPDATE shop.products SET active_supplier_source_id=$1,revision=revision+1,updated_at=clock_timestamp() WHERE id=$2", DbValue.Uuid(source), DbValue.Uuid(product)), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, ct);
            await session.CommitAsync(ct); return productRevision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
}
