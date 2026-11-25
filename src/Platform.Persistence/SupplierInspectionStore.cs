using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public sealed record SupplierInspectionMapping(string External, long ProductRevision);
public sealed class SupplierInspectionStore(DbSessionFactory sessions)
{
    public async Task<bool> IsCurrentProductAsync(AuthenticatedAdmin actor, Guid product, long revision, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct); await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => data.Data.Set<Entities.Product>().AnyAsync(row => row.Id == product && row.Revision == revision && row.FulfillmentKind == "supplier", token), ct);
    }
    public async Task<SupplierInspectionMapping?> ReadAsync(AuthenticatedAdmin actor, Guid product, Guid source, long revision, CancellationToken ct)
    {
        await using var session = await sessions.BeginAsync(ct); await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => (from s in data.Data.Set<Entities.ProductSupplierSource>() join p in data.Data.Set<Entities.Product>() on s.ProductId equals p.Id where s.Id == source && s.ProductId == product && s.Revision == revision && s.ProviderCode == "akunding" && s.ExternalVariantId == null && p.FulfillmentKind == "supplier" select new SupplierInspectionMapping(s.ExternalProductId, p.Revision)).SingleOrDefaultAsync(token), ct);
    }
}
