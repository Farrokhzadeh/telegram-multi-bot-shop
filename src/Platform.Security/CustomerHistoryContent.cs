using System.Security.Cryptography;
using System.Text;

namespace Platform.Security;

public sealed record CustomerHistoricalOrder(string OriginalReference, DateTime CreatedAt, int Quantity, decimal OriginalTotal, string OriginalCurrency, string? SavedTitle, string? SavedPaymentMethod, bool IncompleteTerms)
{
    public bool ReadOnly => true;
    public string PaymentStatus => "unknown";
    public string DeliveryStatus => "unknown";
    public override string ToString() => "CustomerHistoricalOrder [redacted]";
}
public sealed record CustomerHistoricalPage(IReadOnlyList<CustomerHistoricalOrder> Orders, int? NextOffset)
{
    public bool ReadOnly => true;
    public override string ToString() => "CustomerHistoricalPage [redacted]";
}
public static class CustomerHistoryContent
{
    private const int MaximumOrders = 10000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(Guid member, IReadOnlyList<CustomerHistoricalOrder> original)
    {
        if (member == Guid.Empty || original is null || original.Count > MaximumOrders) throw new UnauthorizedAccessException("Historical order scope rejected.");
        var entries = original.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.OriginalReference, StringComparer.Ordinal).ToArray();
        Validate(entries);
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Utf8, true))
        {
            writer.Write("SHOP-CUSTOMER-HISTORY-1");
            writer.Write(member.ToByteArray());
            writer.Write(entries.Length);
            foreach (var order in entries)
            {
                writer.Write(order.OriginalReference); writer.Write(order.CreatedAt.Ticks); writer.Write(order.Quantity); writer.Write(order.OriginalTotal);
                writer.Write(order.OriginalCurrency); writer.Write(order.SavedTitle is not null);
                if (order.SavedTitle is not null) writer.Write(order.SavedTitle);
                writer.Write(order.SavedPaymentMethod is not null);
                if (order.SavedPaymentMethod is not null) writer.Write(order.SavedPaymentMethod);
                writer.Write(order.IncompleteTerms);
            }
            writer.Flush();
        }
        try
        {
            if (memory.Length > 1048576) throw new UnauthorizedAccessException("Historical order archive rejected.");
            return memory.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(memory.GetBuffer()); }
    }
    internal static IReadOnlyList<CustomerHistoricalOrder> Decode(Guid member, byte[] plain)
    {
        if (member == Guid.Empty || plain.Length > 1048576) throw new UnauthorizedAccessException("Historical order archive rejected.");
        try
        {
            using var stream = new MemoryStream(plain, false); using var reader = new BinaryReader(stream, Utf8);
            if (reader.ReadString() != "SHOP-CUSTOMER-HISTORY-1" || new Guid(reader.ReadBytes(16)) != member) throw new UnauthorizedAccessException("Historical order archive rejected.");
            int count = reader.ReadInt32();
            if (count is < 0 or > MaximumOrders) throw new UnauthorizedAccessException("Historical order archive rejected.");
            var entries = new CustomerHistoricalOrder[count];
            for (int i = 0; i < count; i++)
            {
                string reference = reader.ReadString(); long ticks = reader.ReadInt64(); int quantity = reader.ReadInt32(); decimal total = reader.ReadDecimal();
                string currency = reader.ReadString(); string? title = reader.ReadBoolean() ? reader.ReadString() : null;
                string? method = reader.ReadBoolean() ? reader.ReadString() : null; bool incomplete = reader.ReadBoolean();
                entries[i] = new(reference, new DateTime(ticks, DateTimeKind.Utc), quantity, total, currency, title, method, incomplete);
            }
            if (stream.Position != stream.Length) throw new UnauthorizedAccessException("Historical order archive rejected.");
            Validate(entries);
            if (!entries.SequenceEqual(entries.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.OriginalReference, StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("Historical order archive rejected.");
            return Array.AsReadOnly(entries);
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new UnauthorizedAccessException("Historical order archive rejected."); }
    }
    internal static CustomerHistoricalPage Page(IReadOnlyList<CustomerHistoricalOrder> all, int offset, int limit = 20)
    {
        if (offset < 0 || offset > all.Count || limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(offset));
        var rows = all.Skip(offset).Take(limit).ToArray();
        return new(Array.AsReadOnly(rows), offset + rows.Length < all.Count ? offset + rows.Length : null);
    }

    private static void Validate(IReadOnlyList<CustomerHistoricalOrder> entries)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in entries)
        {
            if (order is null || string.IsNullOrWhiteSpace(order.OriginalReference) || order.OriginalReference.Length > 80 || order.OriginalReference.Any(char.IsControl) ||
                !references.Add(order.OriginalReference) || order.CreatedAt.Kind != DateTimeKind.Utc || order.CreatedAt == DateTime.MinValue || order.CreatedAt == DateTime.MaxValue ||
                order.Quantity <= 0 || order.OriginalTotal < 0 || order.OriginalCurrency is not ("USD" or "IRR") ||
                order.SavedTitle is not null && (string.IsNullOrWhiteSpace(order.SavedTitle) || order.SavedTitle.Length > 160 || order.SavedTitle.Any(char.IsControl)) ||
                order.SavedPaymentMethod is not null and not ("wallet" or "bank" or "crypto"))
                throw new UnauthorizedAccessException("Historical order archive rejected.");
        }
    }
}
