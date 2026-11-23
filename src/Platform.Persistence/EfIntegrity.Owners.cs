using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Platform.Persistence.Entities;

namespace Platform.Persistence;

internal sealed partial class EfIntegrity
{
    private static void RequireImmutableOwner(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return;
        string[] properties = entry.Entity switch
        {
            User => [nameof(User.BrandId), nameof(User.TelegramId), nameof(User.FirstSeenAt)],
            Order => [nameof(Order.UserId)],
            Payment => [nameof(Payment.OrderId), nameof(Payment.TopupUserId)],
            Fulfillment => [nameof(Fulfillment.OrderId)],
            SupplierRequest => [nameof(SupplierRequest.OrderId)],
            SupplierRequestAttempt => [nameof(SupplierRequestAttempt.SupplierRequestId)],
            Campaign => [nameof(Campaign.BrandId)],
            TelegramDelivery => [nameof(TelegramDelivery.BrandId), nameof(TelegramDelivery.UserId)],
            Discount => [nameof(Discount.BrandId)],
            SupportTicket => [nameof(SupportTicket.UserId)],
            ProductStockSubscription => [nameof(ProductStockSubscription.UserId)],
            BotActivityEvent => [nameof(BotActivityEvent.UserId)],
            _ => []
        };
        if (properties.Any(name => entry.Property(name).IsModified)) throw new InvalidOperationException("An EF entity owner cannot change.");
    }
    internal void RequireMutation(object before, object after)
    {
        if (before is Wallet oldWallet && after is Wallet newWallet && newWallet.Version < oldWallet.Version)
        {
            Reject();
            throw new InvalidOperationException("EF commit rejected: wallet_posting_balance.");
        }
        if (before is User previous && after is User current &&
            (previous.BrandId != current.BrandId || previous.TelegramId != current.TelegramId || previous.FirstSeenAt != current.FirstSeenAt))
        {
            Reject();
            throw new InvalidOperationException("An EF member identity cannot change.");
        }
    }
}
