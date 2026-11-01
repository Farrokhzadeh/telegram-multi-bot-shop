using Platform.Persistence;

namespace Platform.Telegram;

public static class OrderDeliveryRouter
{
    public static Task RecordAnchorAsync(DbSession session, VerifiedTelegramUpdate update, Guid order, CancellationToken ct = default)
    {
        if (!update.IsCallback) throw new TelegramIngressRejectedException();
        return DeliveryStore.RecordAnchorAsync(session, update.Context, update.ActorId, order, update.MessageId, ct);
    }
}
