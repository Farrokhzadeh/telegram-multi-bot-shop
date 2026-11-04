using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed class AkundingAdapter : ISupplierAdapter, IDisposable
{
	private readonly HttpClient client;
	private readonly string apiKey;
	private readonly string baseUrl;
	public string Provider => "akunding";
	public string Account { get; }
	public bool ProvenIntentLookup => true;
	public AkundingAdapter(string account, string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
	{
		Account = account ?? throw new ArgumentNullException(nameof(account));
		this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		this.baseUrl = (baseUrl ?? "https://api.akunding.com").TrimEnd('/');
		client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
	}
	public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/products/{binding.Product}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("Akunding inspection failed.");
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		decimal price = root.GetProperty("price").GetDecimal();
		bool inStock = root.GetProperty("in_stock").GetBoolean();
		string currency = root.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD";
		return new(Provider, Account, binding.Product, binding.Variant, currency, price, inStock, true, DateTime.UtcNow, "protected:akunding/quote/" + binding.Product);
	}
	public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/orders");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		request.Headers.Add("X-Idempotency-Key", binding.Intent);
		string body = $"{{\"product_id\":\"{binding.Product}\",\"quantity\":{binding.Quantity}}}";
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
		string target = originalPurchaseReference is not null ? $"{baseUrl}/v1/orders/{originalPurchaseReference}" : $"{baseUrl}/v1/orders?intent={binding.Intent}";
		using var request = new HttpRequestMessage(HttpMethod.Get, target);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, originalPurchaseReference, null, null, [], raw);
		return ParseOutcome(raw, binding.Quantity);
	}
	private static SupplierOutcome ParseOutcome(byte[] raw, int requestedQuantity)
	{
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		string orderId = root.GetProperty("order_id").GetString() ?? Guid.NewGuid().ToString("N");
		decimal charged = root.TryGetProperty("charged_amount", out var ca) ? ca.GetDecimal() : 0m;
		string currency = root.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "USD" : "USD";
		var items = new List<SupplierItem>();
		if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			int count = 0;
			foreach (var el in arr.EnumerateArray())
			{
				string code = el.GetProperty("code").GetString() ?? "";
				var disp = count < requestedQuantity ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus;
				items.Add(new SupplierItem("akunding-" + orderId + "-" + count, disp, "code", Encoding.UTF8.GetBytes(code)));
				count++;
			}
		}
		var status = root.TryGetProperty("status", out var s) ? s.GetString() : "completed";
		var outcomeKind = status == "completed" ? SupplierOutcomeKind.Completed : status == "processing" ? SupplierOutcomeKind.Processing : SupplierOutcomeKind.Unknown;
		return new(outcomeKind, orderId, currency, charged, items.ToArray(), raw);
	}
	public void Dispose() => client.Dispose();
}
