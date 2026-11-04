using System.Text;
using System.Text.Json;
using Platform.Core;

namespace Platform.Fulfillment.Providers;

public sealed class RoboticVnAdapter : ISupplierAdapter, IDisposable
{
	private readonly HttpClient client;
	private readonly string apiKey;
	private readonly string baseUrl;
	public string Provider => "roboticvn";
	public string Account { get; }
	public bool ProvenIntentLookup => false;
	public RoboticVnAdapter(string account, string apiKey, HttpMessageHandler? handler = null, string? baseUrl = null)
	{
		Account = account ?? throw new ArgumentNullException(nameof(account));
		this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
		this.baseUrl = (baseUrl ?? "https://api.roboticvn.com").TrimEnd('/');
		client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
	}
	public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
	{
		string target = $"{baseUrl}/api/v2/items?product={binding.Product}" + (binding.Variant is not null ? $"&variant={binding.Variant}" : "");
		using var request = new HttpRequestMessage(HttpMethod.Get, target);
		request.Headers.Add("Authorization", "Token " + apiKey);
		using var response = await client.SendAsync(request, ct);
		if (!response.IsSuccessStatusCode) throw new HttpRequestException("RoboticVN inspection failed.");
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		using var doc = JsonDocument.Parse(raw);
		var root = doc.RootElement;
		decimal price = root.GetProperty("cost").GetDecimal();
		int available = root.GetProperty("available_units").GetInt32();
		return new(Provider, Account, binding.Product, binding.Variant, "USD", price, available > 0, true, DateTime.UtcNow, "protected:roboticvn/quote/" + binding.Product);
	}
	public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/orders");
		request.Headers.Add("Authorization", "Token " + apiKey);
		string body = $"{{\"product\":\"{binding.Product}\",\"variant\":{(binding.Variant is null ? "null" : $"\"{binding.Variant}\"")},\"quantity\":{binding.Quantity},\"client_ref\":\"{binding.Intent}\"}}";
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode)
		{
			var kind = (int)response.StatusCode >= 500 ? SupplierOutcomeKind.Unknown : SupplierOutcomeKind.ConfirmedNoEffect;
			return new(kind, null, null, null, [], raw);
		}
		using var doc = JsonDocument.Parse(raw);
		string orderId = doc.RootElement.GetProperty("order_id").GetString() ?? "";
		return await FetchCredentialsAsync(orderId, binding.Quantity, raw, ct);
	}
	public async Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(originalPurchaseReference)) return new(SupplierOutcomeKind.Unknown, null, null, null, [], []);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/orders/{originalPurchaseReference}");
		request.Headers.Add("Authorization", "Token " + apiKey);
		using var response = await client.SendAsync(request, ct);
		byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
		if (!response.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, originalPurchaseReference, null, null, [], raw);
		return await FetchCredentialsAsync(originalPurchaseReference, binding.Quantity, raw, ct);
	}
	private async Task<SupplierOutcome> FetchCredentialsAsync(string orderId, int requestedQuantity, byte[] orderRaw, CancellationToken ct)
	{
		using var credRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/orders/{orderId}/credentials");
		credRequest.Headers.Add("Authorization", "Token " + apiKey);
		using var credResponse = await client.SendAsync(credRequest, ct);
		byte[] credRaw = await credResponse.Content.ReadAsByteArrayAsync(ct);
		if (!credResponse.IsSuccessStatusCode) return new(SupplierOutcomeKind.Unknown, orderId, null, null, [], credRaw);
		using var doc = JsonDocument.Parse(credRaw);
		var items = new List<SupplierItem>();
		if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			int count = 0;
			foreach (var el in arr.EnumerateArray())
			{
				string secret = el.GetProperty("credential").GetString() ?? "";
				var disp = count < requestedQuantity ? SupplierItemDisposition.Customer : SupplierItemDisposition.Bonus;
				items.Add(new SupplierItem("roboticvn-" + orderId + "-" + count, disp, "account", Encoding.UTF8.GetBytes(secret)));
				count++;
			}
		}
		decimal charge = doc.RootElement.TryGetProperty("total_usd", out var t) ? t.GetDecimal() : 0m;
		return new(items.Count >= requestedQuantity ? SupplierOutcomeKind.Completed : SupplierOutcomeKind.Unknown, orderId, "USD", charge, items.ToArray(), credRaw);
	}
	public void Dispose() => client.Dispose();
}
