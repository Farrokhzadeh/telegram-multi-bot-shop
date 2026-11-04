using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed class CanbosoAdapter : ISupplierAdapter, IDisposable
{
	private readonly HttpClient client;
	private readonly string apiKey;
	private readonly string baseUrl;
	public string Provider => "canboso";
	public string Account { get; }
	public bool ProvenIntentLookup => false;
	public CanbosoAdapter(string account, string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
	{
		Account = account ?? throw new ArgumentNullException(nameof(account));
		this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		this.baseUrl = (baseUrl ?? "https://api.canboso.com").TrimEnd('/');
		client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
	}
	public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/catalog/{binding.Product}");
		request.Headers.Add("X-Auth-Token", apiKey);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("Canboso inspection failed.");
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		decimal cost = root.GetProperty("unit_price").GetDecimal();
		int stock = root.GetProperty("stock_count").GetInt32();
		bool ready = root.TryGetProperty("is_ready", out var r) ? r.GetBoolean() : stock > 0;
		return new(Provider, Account, binding.Product, binding.Variant, "USD", cost, ready && stock > 0, true, DateTime.UtcNow, "protected:canboso/quote/" + binding.Product);
	}
	public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/purchases");
		request.Headers.Add("X-Auth-Token", apiKey);
		request.Headers.Add("Idempotency-Key", binding.Intent);
		string body = $"{{\"product_code\":\"{binding.Product}\",\"quantity\":{binding.Quantity}}}";
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			var kind = (int)response.StatusCode >= 500 ? SupplierOutcomeKind.Unknown : SupplierOutcomeKind.ConfirmedNoEffect;
			return new(kind, null, null, null, [], raw);
		}
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		string purchaseId = root.GetProperty("purchase_id").GetString() ?? Guid.NewGuid().ToString("N");
		decimal charged = root.TryGetProperty("total_paid", out var tp) ? tp.GetDecimal() : 0m;
		var items = new List<SupplierItem>();
		if (root.TryGetProperty("cards", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			int count = 0;
			foreach (var el in arr.EnumerateArray())
			{
				string card = el.GetString() ?? "";
				var disp = count < binding.Quantity ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus;
				items.Add(new SupplierItem("canboso-" + purchaseId + "-" + count, disp, "code", Encoding.UTF8.GetBytes(card)));
				count++;
			}
		}
		return new(items.Count >= binding.Quantity ? SupplierOutcomeKind.Completed : SupplierOutcomeKind.Unknown, purchaseId, "USD", charged, items.ToArray(), raw);
	}
	public async Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(originalPurchaseReference)) return new(SupplierOutcomeKind.Unknown, null, null, null, [], []);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/purchases/{originalPurchaseReference}");
		request.Headers.Add("X-Auth-Token", apiKey);
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, originalPurchaseReference, null, null, [], raw);
		using var doc = JsonDocument.Parse(raw);
		string status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
		var kind = status == "completed" ? SupplierOutcomeKind.Completed : status == "failed" ? SupplierOutcomeKind.ConfirmedNoEffect : SupplierOutcomeKind.Unknown;
		return new(kind, originalPurchaseReference, "USD", 0m, [], raw);
	}
	public void Dispose() => client.Dispose();
}
