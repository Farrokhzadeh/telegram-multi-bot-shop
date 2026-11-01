using Platform.Persistence;
using Platform.Security;

namespace Platform.Campaigns;

public sealed class AudienceMaterializer(CampaignStore store)
{
    public Task<int> RunAsync(BrandContext context, JobLease lease, CancellationToken ct = default) => store.MaterializeAsync(context, lease, ct);
}
