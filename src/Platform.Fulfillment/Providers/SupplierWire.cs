using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

internal sealed class SupplierPricePermits
{
    private readonly Dictionary<Guid, (SupplierBinding Binding, SupplierQuote Quote, long Issued)> entries = [];
    private readonly object gate = new();
    public void Remember(SupplierBinding binding, SupplierQuote quote)
    {
        lock (gate)
        {
            foreach (var id in entries.Where(p => Stopwatch.GetElapsedTime(p.Value.Issued) > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) entries.Remove(id);
            if (entries.Count >= 1000 && !entries.ContainsKey(binding.RequestId)) throw new InvalidOperationException("Supplier inspection capacity is unavailable.");
            entries[binding.RequestId] = (binding, quote, Stopwatch.GetTimestamp());
        }
    }
    public bool Consume(SupplierBinding binding, decimal threshold)
    {
        lock (gate)
            return entries.Remove(binding.RequestId, out var permit) && permit.Binding == binding && Stopwatch.GetElapsedTime(permit.Issued) <= TimeSpan.FromSeconds(30) &&
                threshold == SupplierRules.UnitCeiling(binding) && SupplierRules.Preflight(binding, permit.Quote, DateTime.UtcNow) is null;
    }
    public void Clear() { lock (gate) entries.Clear(); }
}
internal sealed class SupplierWireResponse(bool success, bool json, byte[] evidence) : IDisposable
{
    public bool Success { get; } = success;
    public bool Json { get; } = json;
    public ReadOnlyMemory<byte> Evidence => evidence;
    public void Dispose() => CryptographicOperations.ZeroMemory(evidence);
}
internal static class SupplierWire
{
    public static async Task<SupplierWireResponse> ReadAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.Content.Headers.ContentLength > 1048576) throw new FormatException();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > 1048576) throw new FormatException();
                bytes.Write(buffer, 0, count);
            }
            return new(response.IsSuccessStatusCode, string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase), bytes.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(bytes.GetBuffer());
        }
    }
    public static bool PurchaseKey(string? value, int limit) => value is { Length: > 0 } && value.Length <= limit && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    public static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) throw new FormatException();
    }
    public static bool VerifiedItems(SupplierItem[] items, IReadOnlyList<ReadOnlyMemory<byte>> raw, string kind) =>
        items.Length == raw.Count && items.All(i => i.Disposition == SupplierItemDisposition.Customer && i.ContentKind == kind) &&
        items.Select(i => i.Reference).Distinct(StringComparer.Ordinal).Count() == items.Length &&
        raw.Select(v => Convert.ToHexString(SHA256.HashData(v.Span))).Order(StringComparer.Ordinal).SequenceEqual(items.Select(i => Convert.ToHexString(SHA256.HashData(i.Content.Span))).Order(StringComparer.Ordinal));
}
