using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public enum SqliteReceiptOutcome { Submitted, LateReview, Replayed }

public sealed class SqliteManualDepositStore(SqliteSessionFactory sessions)
{
    public Task<SqliteReceiptOutcome> SubmitAsync(BrandContext context, long revision, long actor,
        Guid payment, string protectedReceipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (revision < 0 || actor <= 0 || payment == Guid.Empty || protectedReceipt is null ||
            !protectedReceipt.StartsWith("protected:", StringComparison.Ordinal) ||
            protectedReceipt.Length is < 12 or > 1024 || protectedReceipt.Any(char.IsControl))
            throw new ArgumentException("The protected bank receipt is unavailable.");
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            var member = (await data.RequireMembershipAsync(context, actor, false, false, token, true))!;
            await data.RequireRegisteredRevisionAsync(context, revision, token);
            var origin = await (from value in data.Data.Set<Entities.Payment>()
                                join order in data.Data.Set<Entities.Order>() on value.OrderId equals order.Id
                                where value.Id == payment && order.UserId == member.Id
                                select new { Payment = value, Order = order }).SingleOrDefaultAsync(token);
            if (origin is null || origin.Payment.PaymentMethod != "bank" || origin.Order.PaymentDueAt is null) data.RejectScope("The receipt is unavailable.");
            var receipt = origin.Payment; var orderState = origin.Order;
            if (receipt.ReceiptAssetRef is not null)
            {
                if (receipt.ReceiptAssetRef != protectedReceipt) data.RejectConflict("The receipt identity was already used.");
                return SqliteReceiptOutcome.Replayed;
            }
            if (receipt.Status == "settled" || orderState.PaymentStatus is "paid" or "not_required") data.RejectConflict("The receipt was already accepted.");
            DateTime now = await data.BrandClockAsync(context.BrandId, token);
            bool timely = orderState.PaymentStatus == "awaiting_payment" && now <= orderState.PaymentDueAt;
            receipt.ReceiptAssetRef = protectedReceipt; receipt.FirstPaymentObservedAt ??= now; receipt.Status = timely ? "observed" : "review";
            if (timely) { orderState.PaymentStatus = "under_review"; orderState.Revision = checked(orderState.Revision + 1); orderState.UpdatedAt = now; }
            return timely ? SqliteReceiptOutcome.Submitted : SqliteReceiptOutcome.LateReview;
        }, ct);
    }
}
