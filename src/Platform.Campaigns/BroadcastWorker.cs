using Platform.Persistence;
using Platform.Security;
using Platform.Telegram;

namespace Platform.Campaigns;

public sealed class BroadcastWorker(CampaignStore store, TimeSpan spacing, long? trustedStaffChat, IReadOnlyDictionary<string, string> supplierAccounts)
{
    public async Task RunAsync(BrandContext context, JobLease lease, Func<NoticeDispatch, string?, CancellationToken, Task<NoticeOutcome>> transport, CancellationToken ct = default)
    {
        NoticeDispatch? dispatch = await store.BeginNoticeAsync(context, lease, spacing, trustedStaffChat, supplierAccounts, ct);
        if (dispatch is null) return;
        string? text = dispatch.Purpose == "restock_notice" ? Localization.Text(dispatch.Language, CustomerText.RestockAvailable) + "\n" + dispatch.Text : dispatch.Text;
        NoticeOutcome outcome;
        try { outcome = await transport(dispatch, text, ct); }
        catch (Exception) { outcome = new(NoticeOutcomeKind.Unknown); }
        await store.RecordNoticeAsync(context, lease, outcome, CancellationToken.None);
    }
}
