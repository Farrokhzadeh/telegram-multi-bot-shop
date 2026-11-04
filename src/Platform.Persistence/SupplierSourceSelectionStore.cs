using System.Diagnostics;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SupplierSourceMetadata(Guid Id, string Provider, string Product, string? Variant, bool Enabled, long Revision, bool Selected);
public sealed class SupplierSourceSelectionStore(DbSessionFactory sessions)
{
    public async Task<AdminPage<SupplierSourceMetadata>> SourcesAsync(AuthenticatedAdmin actor, Guid product, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("""
            SELECT s.id,s.provider_code,s.external_product_id,s.external_variant_id,s.enabled,s.revision,COALESCE(p.active_supplier_source_id=s.id,false)
            FROM shop.product_supplier_sources s JOIN shop.products p ON p.id=s.product_id
            WHERE s.product_id=$1 AND ($2 IS NULL OR s.id>$2) ORDER BY s.id LIMIT $3
            """, DbValue.Uuid(product), AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new SupplierSourceMetadata(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetInt64(5), r.GetBoolean(6)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public Task<long> CreateAsync(AuthenticatedAdmin actor, Guid product, Guid source, string provider, string externalProduct, string? variant, Guid command, CancellationToken ct = default)
        => CreateCoreAsync(actor, product, source, provider, externalProduct, variant, command, null, ct);
    public Task<long> CreateAtRevisionAsync(AuthenticatedAdmin actor, Guid product, Guid source, string provider, string externalProduct, string? variant, Guid command, long productRevision, CancellationToken ct = default)
    {
        AdminCommand.Revision(productRevision);
        if (productRevision == 0) throw new ArgumentException("Invalid product revision.");
        return CreateCoreAsync(actor, product, source, provider, externalProduct, variant, command, productRevision, ct);
    }
    private async Task<long> CreateCoreAsync(AuthenticatedAdmin actor, Guid product, Guid source, string provider, string externalProduct, string? variant, Guid command, long? productRevision, CancellationToken ct)
    {
        if (product == Guid.Empty || source == Guid.Empty || !Label(provider) || !Label(externalProduct) || (variant is not null && !Label(variant))) throw new ArgumentException("Invalid supplier mapping.");
        byte[] hash = productRevision is long expected
            ? AdminCommand.Hash("supplier-source-create-at-revision-v1", product.ToString("D"), provider, externalProduct, variant, expected.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : AdminCommand.Hash("supplier-source-create-v1", product.ToString("D"), provider, externalProduct, variant);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Create, AuditEntity.ProductSupplierSource, source, null, AuditReason.ConfigurationChange, command, hash, 0, 1), ct)) return 1;
            var current = await session.QuerySingleAsync(new DbStatement("SELECT fulfillment_kind,revision FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => (Kind: r.GetString(0), Revision: r.GetInt64(1)), ct);
            if (current.Kind != "supplier" || (productRevision.HasValue && current.Revision != productRevision.Value)) throw new InvalidOperationException("Supplier product revision conflict.");
            await session.ExecuteAsync(new DbStatement("INSERT INTO shop.product_supplier_sources(id,product_id,provider_code,external_product_id,external_variant_id) VALUES ($1,$2,$3,$4,$5)", DbValue.Uuid(source), DbValue.Uuid(product), DbValue.Text(provider), DbValue.Text(externalProduct), AdminCommand.Text(variant)), ct);
            await session.CommitAsync(ct); return 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<long> SetEnabledAsync(AuthenticatedAdmin actor, Guid product, Guid source, bool enabled, long revision, Guid command, CatalogStore? catalog = null, CancellationToken ct = default)
    {
        AdminCommand.Revision(revision);
        if (product == Guid.Empty || source == Guid.Empty || revision == 0) throw new ArgumentException("Invalid source lifecycle command.");
        var audit = new AuditCommand(command, enabled ? AuditAction.Enable : AuditAction.Disable, AuditEntity.ProductSupplierSource, source, null, AuditReason.ConfigurationChange, command,
            AdminCommand.Hash("supplier-source-enabled-v1", product.ToString("D"), enabled.ToString()), revision, revision + 1);
        Source? before = null;
        (string Account, SupplierCatalogResult Result)? inspected = null;
        long issued = 0;
        if (enabled)
        {
            await using (var probe = await sessions.BeginAsync(ct))
            {
                if (!await AdminAuditStore.AppendAsync(probe, actor, audit, ct)) return revision + 1;
                before = await ReadSourceAsync(probe, product, source, revision, enabled, ct);
            }
            ArgumentNullException.ThrowIfNull(catalog);
            issued = Stopwatch.GetTimestamp();
            inspected = await catalog.InspectSourceAsync(before.Provider, before.Product, before.Variant, ct);
        }
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, audit, ct)) return revision + 1;
            Source current = await ReadSourceAsync(session, product, source, revision, enabled, ct);
            if (enabled && (before!.ProductRevision != current.ProductRevision || Stopwatch.GetElapsedTime(issued) > TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Source inspection expired or changed.");
            await session.ExecuteAsync(new DbStatement("UPDATE shop.product_supplier_sources SET enabled=$1,revision=revision+1,updated_at=clock_timestamp() WHERE id=$2", DbValue.Boolean(enabled), DbValue.Uuid(source)), ct);
            if (inspected is { } observation) await CatalogStore.WriteObservationAsync(session, source, revision + 1, current.Observation, observation.Account, observation.Result, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, ct);
            if (enabled && Stopwatch.GetElapsedTime(issued) > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("Source inspection expired.");
            await session.CommitAsync(ct); return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
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
    private static async Task<Source> ReadSourceAsync(DbSession session, Guid product, Guid source, long revision, bool enabled, CancellationToken ct)
    {
        var master = await session.QuerySingleAsync(new DbStatement("SELECT revision,fulfillment_kind FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => (Revision: r.GetInt64(0), Kind: r.GetString(1)), ct);
        var row = await session.QuerySingleAsync(new DbStatement("SELECT provider_code,external_product_id,external_variant_id,revision,enabled,observation_revision FROM shop.product_supplier_sources WHERE id=$1 AND product_id=$2 FOR UPDATE", DbValue.Uuid(source), DbValue.Uuid(product)), r => (Provider: r.GetString(0), Product: r.GetString(1), Variant: r.IsDBNull(2) ? null : r.GetString(2), Revision: r.GetInt64(3), Enabled: r.GetBoolean(4), Observation: r.GetInt64(5)), ct);
        if (row.Revision != revision || row.Enabled == enabled || (enabled && master.Kind != "supplier")) throw new InvalidOperationException("Source revision or lifecycle conflict.");
        return new(row.Provider, row.Product, row.Variant, row.Observation, master.Revision);
    }
    private static bool Label(string? value) => value is { Length: > 0 and <= 160 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private sealed record Source(string Provider, string Product, string? Variant, long Observation, long ProductRevision);
}
