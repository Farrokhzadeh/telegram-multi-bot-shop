using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed record AkundingCatalogProduct(long Id, string Name, string UnitLabel, decimal UnitPrice, int Stock, bool Available)
{
    public string Currency => "USDT";
    public string? EligibilityFingerprint { get; init; }
    public string? Description { get; init; }
    public string? Features { get; init; }
}
public sealed record AkundingCatalogPage(AkundingCatalogProduct[] Items, long? Next);
public sealed class AkundingCatalogClient(HttpClient client)
{
    public async Task<AkundingCatalogProduct> GetProductAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        using var doc = await ReadAsync("products/" + productId.ToString(CultureInfo.InvariantCulture), ct);
        var result = Parse(doc.RootElement);
        if (result.Id != productId) throw new InvalidOperationException("Supplier catalog is unavailable.");
        return result;
    }
    public async Task<AkundingCatalogPage> ListProductsAsync(string search, long? after, int limit, CancellationToken ct)
    {
        if (search is null || search.Length > 80 || search.Any(char.IsControl) || after is <= 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid supplier catalog query.");
        using var doc = await ReadAsync("products?include_out_of_stock=true", ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 1000) throw new InvalidOperationException("Supplier catalog is unavailable.");
        var products = doc.RootElement.EnumerateArray().Select(Parse).ToArray();
        if (products.Select(p => p.Id).Distinct().Count() != products.Length) throw new InvalidOperationException("Supplier catalog is unavailable.");
        string term = search.Trim();
        var page = products.Where(p => (!after.HasValue || p.Id > after.Value) && (p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal)))
            .OrderBy(p => p.Id).Take(limit + 1).ToArray();
        return new(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Id : null);
    }
    private async Task<JsonDocument> ReadAsync(string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://akunding.shop/api/v1/" + path);
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
            return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { throw new InvalidOperationException("Supplier catalog is unavailable."); }
    }
    private static AkundingCatalogProduct Parse(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()) throw new FormatException();
            long id = root.GetProperty("id").GetInt64();
            string? name = root.GetProperty("name").GetString(), label = root.GetProperty("unit_label").GetString();
            decimal price = ExactDecimal.Parse(root.GetProperty("your_price").GetRawText());
            int stock = root.GetProperty("stock").GetInt32();
            bool available = root.GetProperty("available").GetBoolean();
            if (id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 1000 || string.IsNullOrWhiteSpace(label) || label.Length > 100 || price <= 0 || stock < 0 || available != (stock > 0)) throw new FormatException();
            string? fingerprint = null;
            string? descriptionText = null, featuresText = null;
            if (root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String && root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.String)
            {
                descriptionText = description.GetString(); featuresText = features.GetString();
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write("akunding-eligibility-v1"); writer.Write(id); writer.Write(name); writer.Write(label); writer.Write(description.GetString()!); writer.Write(features.GetString()!); }
                fingerprint = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
            }
            return new(id, name, label, price, stock, available) { EligibilityFingerprint = fingerprint, Description = descriptionText, Features = featuresText };
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new InvalidOperationException("Supplier catalog is unavailable."); }
    }
}
