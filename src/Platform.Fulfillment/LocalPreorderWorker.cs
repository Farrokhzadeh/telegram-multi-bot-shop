using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment;

public sealed class LocalPreorderWorker(LocalPreorderStore store, IReadOnlyList<BrandContext> contexts)
{
    public Task<Guid?> RunAsync(JobLease lease, CancellationToken ct = default) => store.DrainAsync(lease, contexts, ct);
}
