using System.Globalization;

namespace Platform.Core;

public static class PaymentDeadlinePolicy
{
    public static bool CanExpire(string state, DateTime due, DateTime now, bool evidence)
    {
        if (due.Kind != DateTimeKind.Utc || now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC payment clocks are required.");
        return state == "awaiting_payment" && !evidence && now > due;
    }
    public static string Text(Guid order, string state, string? resolution, DateTime due, string language)
    {
        if (order == Guid.Empty || due.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid payment instruction identity.");
        bool fa = language.Split('-')[0].Equals("fa", StringComparison.OrdinalIgnoreCase);
        string status = state switch
        {
            "awaiting_payment" => fa ? "پرداخت تا ۳۰ دقیقه معتبر است" : "Payment valid for 30 minutes",
            "expired" => fa ? "ناموفق/منقضی" : "Failed/Expired",
            "cancelled" when resolution == "replaced" => fa ? "لغو شد — جایگزین شده" : "Cancelled — replaced",
            "cancelled" => fa ? "لغو شد" : "Cancelled",
            "confirmation_pending" => fa ? "پرداخت دریافت شد. در حال پردازش آن هستیم." : "Payment received. We’re processing it.",
            "under_review" => fa ? "پرداخت در حال بررسی است" : "Payment under review",
            "paid" or "not_required" => fa ? "پرداخت تایید شده است" : "Payment accepted",
            _ => throw new ArgumentException("Unsupported payment instruction state.")
        };
        return status + "\n" + (fa ? "سفارش: " : "Order: ") + order.ToString("D") + "\n" +
            (fa ? "مهلت (UTC): " : "Deadline (UTC): ") + due.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }
}
