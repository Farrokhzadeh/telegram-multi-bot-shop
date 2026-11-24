using Microsoft.EntityFrameworkCore;
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
        var rows = await session.RunEfStepAsync(false, (data, token) => (from source in data.Data.Set<Entities.ProductSupplierSource>() join master in data.Data.Set<Entities.Product>() on source.ProductId equals master.Id where source.ProductId == product && (after == null || source.Id.CompareTo(after.Value) > 0) orderby source.Id select new SupplierSourceMetadata(source.Id, source.ProviderCode, source.ExternalProductId, source.ExternalVariantId, source.Enabled, source.Revision, master.ActiveSupplierSourceId == source.Id)).Take(limit + 1).ToListAsync(token), ct);
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
            var current = await MasterAsync(session, product, ct);
            await session.AdmitSharedWriterAsync(ct);
            if (current.Kind != "supplier" || (productRevision.HasValue && current.Revision != productRevision.Value)) throw new InvalidOperationException("Supplier product revision conflict.");
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireAdminAsync(actor, null, token);
                data.Data.Add(new Entities.ProductSupplierSource { Id = source, ProductId = product, ProviderCode = provider, ExternalProductId = externalProduct, ExternalVariantId = variant }); return true;
            }, ct);
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
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireAdminAsync(actor, null, token);
                var row = await data.Data.Set<Entities.ProductSupplierSource>().SingleAsync(value => value.Id == source, token);
                row.Enabled = enabled; row.Revision++; row.UpdatedAt = await data.ClockAsync(data.Data.Set<Entities.ProductSupplierSource>().Where(value => value.Id == source), token); return true;
            }, ct);
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
            var current = await MasterAsync(session, product, ct);
            await session.AdmitSharedWriterAsync(ct);
            if (current.Revision != productRevision || current.Kind != "supplier") throw new InvalidOperationException("Product revision or supplier policy conflict.");
            bool valid = await session.RunEfStepAsync(false, async (data, token) =>
            {
                await data.LockSourceAsync(source, token);
                return await data.Data.Set<Entities.ProductSupplierSource>().AnyAsync(value => value.Id == source && value.ProductId == product && value.Revision == sourceRevision && value.Enabled, token);
            }, ct);
            if (!valid) throw new InvalidOperationException("Supplier source revision or eligibility conflict.");
            await session.RunEfStepAsync(true, async (data, token) =>
            {
                await data.RequireAdminAsync(actor, null, token);
                var row = await data.Data.Set<Entities.Product>().SingleAsync(value => value.Id == product, token);
                row.ActiveSupplierSourceId = source; row.Revision++; row.UpdatedAt = await data.ClockAsync(data.Data.Set<Entities.Product>().Where(value => value.Id == product), token); return true;
            }, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, ct);
            await session.CommitAsync(ct); return productRevision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static async Task<Source> ReadSourceAsync(DbSession session, Guid product, Guid source, long revision, bool enabled, CancellationToken ct)
    {
        var master = await MasterAsync(session, product, ct);
        await session.AdmitSharedWriterAsync(ct);
        var row = await session.RunEfStepAsync(false, async (data, token) =>
        {
            await data.LockSourceAsync(source, token);
            return await data.Data.Set<Entities.ProductSupplierSource>().Where(value => value.Id == source && value.ProductId == product).Select(value => new { Provider = value.ProviderCode, Product = value.ExternalProductId, Variant = value.ExternalVariantId, value.Revision, value.Enabled, Observation = value.ObservationRevision }).SingleAsync(token);
        }, ct);
        if (row.Revision != revision || row.Enabled == enabled || (enabled && master.Kind != "supplier")) throw new InvalidOperationException("Source revision or lifecycle conflict.");
        return new(row.Provider, row.Product, row.Variant, row.Observation, master.Revision);
    }
    private static Task<Master> MasterAsync(DbSession session, Guid product, CancellationToken ct) => session.RunEfStepAsync(false, async (data, token) =>
    {
        await data.LockProductAsync(product, token);
        return await data.Data.Set<Entities.Product>().Where(value => value.Id == product).Select(value => new Master(value.Revision, value.FulfillmentKind)).SingleAsync(token);
    }, ct);
    private sealed record Master(long Revision, string Kind);
    private static bool Label(string? value) => value is { Length: > 0 and <= 160 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private sealed record Source(string Provider, string Product, string? Variant, long Observation, long ProductRevision);
}
