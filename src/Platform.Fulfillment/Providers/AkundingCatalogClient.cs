using System.Globalization;
using System.Text.Json;

namespace Platform.Fulfillment.Providers;

public sealed record AkundingCatalogProduct(long Id, string Name, string UnitLabel, decimal UnitPrice, int Stock, bool Available)
{
    public string Currency => "USDT";
}
public sealed class AkundingCatalogClient(HttpClient client)
{
    public async Task<AkundingCatalogProduct> GetProductAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://akunding.shop/api/v1/products/" + productId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 1048576) throw new InvalidOperationException("Supplier catalog is unavailable.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > 1048576) throw new InvalidOperationException("Supplier catalog is unavailable.");
                bytes.Write(buffer, 0, count);
            }
            using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw new FormatException();
            long id = root.GetProperty("id").GetInt64();
            string? name = root.GetProperty("name").GetString(), label = root.GetProperty("unit_label").GetString();
            decimal price = root.GetProperty("your_price").GetDecimal();
            int stock = root.GetProperty("stock").GetInt32();
            bool available = root.GetProperty("available").GetBoolean();
            if (id != productId || string.IsNullOrWhiteSpace(name) || name.Length > 1000 || string.IsNullOrWhiteSpace(label) || label.Length > 100 || price <= 0 || stock < 0 || available != (stock > 0)) throw new FormatException();
            return new(id, name, label, price, stock, available);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new InvalidOperationException("Supplier catalog is unavailable."); }
    }
}
