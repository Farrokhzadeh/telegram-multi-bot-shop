using System.Globalization;
using System.Text;
using Platform.Security;

namespace Platform.Telegram;

public sealed record CustomerHistoryMessage(string Text)
{
    public string? ParseMode => null;
    public bool HasActionButtons => false;
    public override string ToString() => "CustomerHistoryMessage [redacted]";
}
public static class CustomerHistoryMessageRenderer
{
    private const int MaximumLength = 3500;
    private const string Heading = "Historical orders (read-only)\nPayment and delivery verification are unknown unless separately confirmed.\n";
    public static IReadOnlyList<CustomerHistoryMessage> Render(UpdateRouteResult result)
    {
        if (result.Code != UpdateRouteCode.History || result.History is null) throw new ArgumentException("Not a historical-order result.", nameof(result));
        return Render(result.History);
    }
    public static IReadOnlyList<CustomerHistoryMessage> Render(CustomerHistoricalPage page)
    {
        if (page is null || page.Orders is null || page.Orders.Count > 20 || page.NextOffset is int nextIndex && (nextIndex is < 20 or > 10000 || nextIndex % 20 != 0))
            throw new ArgumentException("Invalid historical-order page.", nameof(page));
        var messages = new List<CustomerHistoryMessage>();
        var current = new StringBuilder(Heading);
        if (page.Orders.Count == 0) current.Append("\nNo historical orders found.\n");
        foreach (var order in page.Orders)
        {
            if (order is null || !order.ReadOnly || order.PaymentStatus != "unknown" || order.DeliveryStatus != "unknown" ||
                string.IsNullOrWhiteSpace(order.OriginalReference) || order.OriginalReference.Length > 80 || order.OriginalReference.Any(char.IsControl) ||
                order.CreatedAt.Kind != DateTimeKind.Utc || order.Quantity <= 0 || order.OriginalTotal < 0 ||
                order.OriginalCurrency is not ("USD" or "IRR") ||
                order.SavedTitle is string title && (title.Length is < 1 or > 160 || title.Any(char.IsControl)) ||
                order.SavedPaymentMethod is not null and not ("wallet" or "bank" or "crypto"))
                throw new ArgumentException("Invalid historical-order evidence.", nameof(page));
            var entry = new StringBuilder();
            entry.Append("\nOrder: ").Append(order.OriginalReference).Append("\nDate: ").Append(order.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC");
            entry.Append("\nProduct: ").Append(order.SavedTitle ?? "Unknown").Append("\nQuantity: ").Append(order.Quantity.ToString(CultureInfo.InvariantCulture));
            entry.Append("\nOriginal total: ").Append(order.OriginalTotal.ToString("G29", CultureInfo.InvariantCulture)).Append(' ').Append(order.OriginalCurrency);
            entry.Append("\nOriginal payment method: ").Append(order.SavedPaymentMethod ?? "Unknown");
            entry.Append("\nPayment status: Unknown\nDelivery status: Unknown\n");
            if (entry.Length + Heading.Length > MaximumLength) throw new ArgumentException("Historical order exceeds message limit.", nameof(page));
            if (current.Length + entry.Length > MaximumLength)
            {
                messages.Add(new(current.ToString()));
                current.Clear().Append(Heading);
            }
            current.Append(entry);
        }
        if (page.NextOffset is int offset)
        {
            string next = "\nNext page: /orders " + offset.ToString(CultureInfo.InvariantCulture);
            if (current.Length + next.Length > MaximumLength)
            {
                messages.Add(new(current.ToString()));
                current.Clear().Append(Heading);
            }
            current.Append(next);
        }
        messages.Add(new(current.ToString()));
        return messages.AsReadOnly();
    }
}
