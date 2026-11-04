using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed class RapidXAdapter : ISupplierAdapter, IDisposable
{
	private readonly HttpClient client;
	private readonly string apiKey;
	private readonly string baseUrl;
	public string Provider => "rapidx";
	public string Account { get; }
	public bool ProvenIntentLookup => false;
	public RapidXAdapter(string account, string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
	{
		Account = account ?? throw new ArgumentNullException(nameof(account));
		this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		this.baseUrl = (baseUrl ?? "https://api.rapidx.net").TrimEnd('/');
		client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
	}
	public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/products/{binding.Product}");
		request.Headers.Add("X-RapidX-Key", apiKey);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("RapidX inspection failed.");
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		decimal price = root.GetProperty("price").GetDecimal();
		int stock = root.GetProperty("stock").GetInt32();
		return new(Provider, Account, binding.Product, binding.Variant, "USD", price, stock > 0, true, DateTime.UtcNow, "protected:rapidx/quote/" + binding.Product);
	}
	public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/transactions");
		request.Headers.Add("X-RapidX-Key", apiKey);
		string body = $"{{\"sku\":\"{binding.Product}\",\"count\":{binding.Quantity},\"reference\":\"{binding.Intent}\"}}";
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
		if (string.IsNullOrWhiteSpace(originalPurchaseReference)) return new(SupplierOutcomeKind.Unknown, null, null, null, [], []);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/transactions/{originalPurchaseReference}");
		request.Headers.Add("X-RapidX-Key", apiKey);
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, originalPurchaseReference, null, null, [], raw);
		return ParseOutcome(raw, binding.Quantity);
	}
	private static SupplierOutcome ParseOutcome(byte[] raw, int requestedQuantity)
	{
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		string txId = root.GetProperty("transaction_id").GetString() ?? Guid.NewGuid().ToString("N");
		decimal amount = root.TryGetProperty("total_amount", out var am) ? am.GetDecimal() : 0m;
		var items = new List<SupplierItem>();
		if (root.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			int count = 0;
			foreach (var el in arr.EnumerateArray())
			{
				string key = el.GetString() ?? "";
				var disp = count < requestedQuantity ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus;
				items.Add(new SupplierItem("rapidx-" + txId + "-" + count, disp, "key", Encoding.UTF8.GetBytes(key)));
				count++;
			}
		}
		return new(items.Count >= requestedQuantity ? SupplierOutcomeKind.Completed : SupplierOutcomeKind.Unknown, txId, "USD", amount, items.ToArray(), raw);
	}
	public void Dispose() => client.Dispose();
}
