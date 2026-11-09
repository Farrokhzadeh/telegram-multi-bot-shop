namespace Platform.Telegram;

public sealed record CustomerHistoryDeliveryReceipt(int ConfirmedMessages, CustomerHistorySendState FinalState)
{
    public bool Completed => FinalState == CustomerHistorySendState.Confirmed;
    public override string ToString() => "CustomerHistoryDeliveryReceipt [redacted]";
}
public sealed class CustomerHistoryDeliveryPipeline(CustomerHistoryCommandRouter router, CustomerHistoryTelegramSender sender)
{
    public async Task<CustomerHistoryDeliveryReceipt> SendAsync(string token, VerifiedTelegramUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var page = await router.ReadAsync(update, ct);
        var messages = CustomerHistoryMessageRenderer.Render(page);
        int confirmed = 0;
        foreach (var message in messages)
        {
            var result = await sender.SendAsync(token, update, message, ct);
            if (result.State != CustomerHistorySendState.Confirmed) return new(confirmed, result.State);
            confirmed++;
        }
        return new(confirmed, CustomerHistorySendState.Confirmed);
    }
}
