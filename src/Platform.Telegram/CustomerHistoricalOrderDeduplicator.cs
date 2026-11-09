using Platform.Security;

namespace Platform.Telegram;

/// <summary>Unifies original archive entries and manifest-verified typed historical aliases; never guesses ownership.</summary>
public static class CustomerHistoricalOrderDeduplicator
{
    public static CustomerHistoricalPage Page(IReadOnlyList<CustomerHistoricalOrder> archived,
        IReadOnlyList<CustomerHistoricalOrder> importedHistorical, int offset, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(importedHistorical);
        if (offset < 0 || offset > 10000 || limit is < 1 or > 50 ||
            archived.Count > 10000 || importedHistorical.Count > 10000)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var originals = new Dictionary<string, CustomerHistoricalOrder>(StringComparer.Ordinal);
        foreach (var order in archived) Add(order, false);
        foreach (var order in importedHistorical) Add(order, true);
        var all = originals.Values.OrderByDescending(o => o.CreatedAt)
            .ThenBy(o => o.OriginalReference, StringComparer.Ordinal).ToArray();
        if (offset > all.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var page = all.Skip(offset).Take(limit).ToArray();
        return new(Array.AsReadOnly(page), offset + page.Length < all.Length ? offset + page.Length : null);

        void Add(CustomerHistoricalOrder order, bool imported)
        {
            if (order is null || !order.ReadOnly || order.PaymentStatus != "unknown" || order.DeliveryStatus != "unknown" ||
                string.IsNullOrWhiteSpace(order.OriginalReference) || order.OriginalReference.Length > 80 ||
                order.OriginalReference.Any(char.IsControl) || order.CreatedAt.Kind != DateTimeKind.Utc ||
                order.CreatedAt == DateTime.MinValue || order.Quantity <= 0 || order.OriginalTotal < 0 ||
                order.OriginalCurrency is not ("USD" or "IRR"))
                throw new UnauthorizedAccessException("Unverified historical order.");
            if (!originals.TryGetValue(order.OriginalReference, out var present))
            {
                if (!originals.TryAdd(order.OriginalReference, order)) throw new UnauthorizedAccessException("Historical order collision.");
                return;
            }
            if (!imported ||
                present.CreatedAt != order.CreatedAt || present.Quantity != order.Quantity ||
                present.OriginalTotal != order.OriginalTotal || present.OriginalCurrency != order.OriginalCurrency ||
                (present.SavedTitle is not null && order.SavedTitle is not null && present.SavedTitle != order.SavedTitle) ||
                (present.SavedPaymentMethod is not null && order.SavedPaymentMethod is not null && present.SavedPaymentMethod != order.SavedPaymentMethod))
                throw new UnauthorizedAccessException("Historical order alias conflict.");
            originals[order.OriginalReference] = present with
            {
                SavedTitle = present.SavedTitle ?? order.SavedTitle,
                SavedPaymentMethod = present.SavedPaymentMethod ?? order.SavedPaymentMethod,
                IncompleteTerms = present.IncompleteTerms && order.IncompleteTerms
            };
        }
    }
}
