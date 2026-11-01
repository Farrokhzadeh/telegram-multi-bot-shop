using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment;

public sealed class LocalDeliveryWorker(DeliveryStore store)
{
    public async Task RunAsync(BrandContext context, JobLease lease, Func<DeliveryDispatch, CancellationToken, Task<DeliveryOutcome>> transport, CancellationToken ct = default)
    {
        using DeliveryDispatch? dispatch = await store.BeginAsync(context, lease, ct);
        if (dispatch is null) return;
        DeliveryOutcome outcome;
        try { outcome = await transport(dispatch, ct); }
        catch (Exception) { outcome = new(DeliveryOutcomeKind.Unknown); }
        await store.RecordOutcomeAsync(context, lease, outcome, CancellationToken.None);
    }
}
