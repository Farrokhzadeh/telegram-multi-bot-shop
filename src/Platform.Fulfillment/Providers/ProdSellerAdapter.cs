using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed class ProdSellerAdapter : ISupplierAdapter, IDisposable
{
	private readonly HttpClient client;
	private readonly string apiKey;
	private readonly string baseUrl;
	public string Provider => "prodseller";
	public string Account { get; }
	public bool ProvenIntentLookup => true;
	public ProdSellerAdapter(string account, string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
	{
		Account = account ?? throw new ArgumentNullException(nameof(account));
		this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		this.baseUrl = (baseUrl ?? "https://api.prodseller.io").TrimEnd('/');
		client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
	}
	public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/products/{binding.Product}");
		request.Headers.Add("X-Api-Key", apiKey);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("ProdSeller inspection failed.");
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		decimal price = root.GetProperty("price_usd").GetDecimal();
		int stock = root.GetProperty("stock").GetInt32();
		bool instant = root.TryGetProperty("is_instant", out var inst) && inst.GetBoolean();
		return new(Provider, Account, binding.Product, binding.Variant, "USD", price, instant && stock > 0, true, DateTime.UtcNow, "protected:prodseller/quote/" + binding.Product);
	}
	public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/orders");
		request.Headers.Add("X-Api-Key", apiKey);
		request.Headers.Add("Idempotency-Key", binding.Intent);
		string body = JsonSerializer.Serialize(new { product_id = binding.Product, count = binding.Quantity });
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			var kind = (int)response.StatusCode >= 500 ? SupplierOutcomeKind.Unknown : SupplierOutcomeKind.ConfirmedNoEffect;
			return new(kind, null, null, null, [], raw);
		}
		return ParseOutcome(raw, binding.Quantity);
	}
	public async Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct)
	{
		string target = $"{baseUrl}/api/v1/orders/{originalPurchaseReference ?? binding.Intent}";
		using var request = new HttpRequestMessage(HttpMethod.Get, target);
		request.Headers.Add("X-Api-Key", apiKey);
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, originalPurchaseReference, null, null, [], raw);
		return ParseOutcome(raw, binding.Quantity);
	}
	private static SupplierOutcome ParseOutcome(byte[] raw, int requestedQuantity)
	{
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		string orderId = root.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
		decimal charged = root.TryGetProperty("total_charge", out var tc) ? tc.GetDecimal() : 0m;
		var items = new List<SupplierItem>();
		if (root.TryGetProperty("delivered_keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			int count = 0;
			foreach (var el in arr.EnumerateArray())
			{
				string key = el.GetString() ?? "";
				var disp = count < requestedQuantity ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus;
				items.Add(new SupplierItem("prodseller-" + orderId + "-" + count, disp, "key", Encoding.UTF8.GetBytes(key)));
				count++;
			}
		}
		return new(items.Count >= requestedQuantity ? SupplierOutcomeKind.Completed : SupplierOutcomeKind.Unknown, orderId, "USD", charged, items.ToArray(), raw);
	}
	public void Dispose() => client.Dispose();
}
