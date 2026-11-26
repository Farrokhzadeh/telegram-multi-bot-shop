using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment.Providers;

public sealed record ProdSellerProductApproval(string ProductId, string ContentKind);
public sealed record ProdSellerProduct(string Id, string Name, decimal UnitPrice, int? Stock, string? DeliveryType, bool? RequiresEmailActivation)
{
    public string Currency => "USDT";
    public bool Ready => DeliveryType == "instant" && RequiresEmailActivation == false && Stock is >= 0;
}
public sealed class ProdSellerOrderReceipt : IDisposable
{
    private readonly byte[][] items;
    public string Reference { get; }
    public string Status { get; }
    public decimal Amount { get; }
    public IReadOnlyList<ReadOnlyMemory<byte>> Items => items.Select(i => new ReadOnlyMemory<byte>(i)).ToArray();
    internal ProdSellerOrderReceipt(string reference, string status, decimal amount, byte[][] items)
    { Reference = reference; Status = status; Amount = amount; this.items = items; }
    public void Dispose() { foreach (var item in items) CryptographicOperations.ZeroMemory(item); }
    public override string ToString() => "ProdSellerOrderReceipt [redacted]";
}
public sealed class ProdSellerSupplierAdapter : ISupplierAdapter, IDisposable
{
    private readonly HttpClient http;
    private readonly Dictionary<string, ProdSellerProductApproval> approved = new(StringComparer.Ordinal);
    private readonly SupplierPricePermits permits = new();
    private readonly Func<SupplierBinding, ProdSellerOrderReceipt, SupplierItem[]> verify;
    public string Provider => "prodseller";
    public string Account { get; }
    public bool ProvenIntentLookup => false;
    public bool UsesPurchaseKey => true;
    public ProdSellerSupplierAdapter(SupplierCredentialGrant grant, SupplierCredentialSnapshot credentials, string account, IEnumerable<ProdSellerProductApproval> approvals,
        Func<SupplierBinding, ProdSellerOrderReceipt, SupplierItem[]> verifyItems, HttpMessageHandler? transport = null)
        : this(credentials.ForGrant(grant), account, approvals, verifyItems, transport, grant.DeploymentId, grant.Service) { }

    private ProdSellerSupplierAdapter(SupplierCredentialSnapshot credentials, string account, IEnumerable<ProdSellerProductApproval> approvals,
        Func<SupplierBinding, ProdSellerOrderReceipt, SupplierItem[]> verifyItems, HttpMessageHandler? transport, Guid deployment, SecretService service)
    {
        if (credentials.DeploymentId != deployment || credentials.Service != service) throw new UnauthorizedAccessException("Supplier credential scope is unavailable.");
        credentials.UseCredential("prodseller", account, key => key.Length);
        ArgumentNullException.ThrowIfNull(verifyItems);
        foreach (var item in approvals)
            if (!ProductId(item.ProductId) || item.ContentKind is not ("account" or "key" or "code") || !approved.TryAdd(item.ProductId, item))
                throw new ArgumentException("Exact reviewed ProdSeller products are required.");
        Account = account; verify = verifyItems;
        http = new(new Authorization(credentials, account, transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
    }
    public async Task<ProdSellerProduct> GetProductAsync(string product, CancellationToken ct)
    {
        if (!ProductId(product)) throw new ArgumentException("Canonical ProdSeller product ID is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://prodseller.com/v1/products/" + product);
        try
        {
            using var response = await SupplierWire.ReadAsync(http, request, ct);
            if (!response.Success || !response.Json) throw new FormatException();
            using var doc = JsonDocument.Parse(response.Evidence, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement; SupplierWire.Object(root);
            string? id = root.GetProperty("id").GetString(), name = root.GetProperty("name").GetString();
            decimal price = ExactDecimal.Parse(root.GetProperty("price").GetRawText());
            int? stock = root.TryGetProperty("stock", out var quantity) && quantity.ValueKind != JsonValueKind.Null ? quantity.GetInt32() : null;
            string? type = null;
            if (root.TryGetProperty("delivery", out var delivery))
            {
                SupplierWire.Object(delivery);
                if (delivery.TryGetProperty("type", out var field)) type = field.GetString();
            }
            bool? activation = root.TryGetProperty("requiresEmailActivation", out var flag) ? flag.GetBoolean() : null;
            if (id != product || string.IsNullOrWhiteSpace(name) || name.Length > 1000 || price <= 0 || stock < 0) throw new FormatException();
            return new(id!, name, price, stock, type, activation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new InvalidOperationException("Supplier catalog is unavailable."); }
    }
    public async Task<SupplierCatalogResult> InspectCatalogAsync(SupplierCatalogRequest request, CancellationToken ct)
    {
        if (request.Provider != Provider || request.AccountRef != Account || request.VariantRef is not null || request.Quantity is < 1 or > 1000) throw new ArgumentException("Exact ProdSeller catalog scope is required.");
        var product = await GetProductAsync(request.ProductRef, ct);
        bool ready = product.Ready && approved.ContainsKey(product.Id);
        return new(!ready ? SupplierCatalogStatus.Ineligible : product.Stock > 0 ? SupplierCatalogStatus.Available : SupplierCatalogStatus.OutOfStock,
            product.Stock ?? 0, product.UnitPrice, "USDT", "prodseller:product/" + product.Id, ready);
    }
    public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
    {
        Require(binding);
        var product = await GetProductAsync(binding.Product, ct);
        var quote = new SupplierQuote(Provider, Account, product.Id, null, "USDT", product.UnitPrice,
            product.Ready && product.Stock >= binding.Quantity && approved.ContainsKey(product.Id), false, DateTime.UtcNow, "prodseller:product/" + product.Id);
        permits.Remember(binding, quote);
        return quote;
    }
    public Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct) => SubmitAttemptAsync(binding, null, maximumUnitCharge, ct);
    public async Task<SupplierOutcome> SubmitAttemptAsync(SupplierBinding binding, string? purchaseKey, decimal maximumUnitCharge, CancellationToken ct)
    {
        Require(binding);
        if (!permits.Consume(binding, maximumUnitCharge) || !SupplierWire.PurchaseKey(purchaseKey, 100))
            return new(SupplierOutcomeKind.ConfirmedNoEffect, null, "USDT", 0, [], "prodseller:local-prepurchase-rejection"u8);
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        { writer.WriteStartObject(); writer.WriteString("productId", binding.Product); writer.WriteNumber("quantity", binding.Quantity); writer.WriteEndObject(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://prodseller.com/v1/orders") { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Idempotency-Key", purchaseKey);
        return await SendAsync(binding, request, null, ct);
    }
    public async Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct)
    {
        Require(binding);
        if (!OrderId(originalPurchaseReference)) return Unknown("prodseller:original-order-reference-required"u8);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://prodseller.com/v1/orders/" + originalPurchaseReference);
        return await SendAsync(binding, request, originalPurchaseReference, ct);
    }
    private void Require(SupplierBinding binding)
    {
        if (binding.Provider != Provider || binding.Account != Account || binding.Variant is not null || binding.Currency != "USDT" || !ProductId(binding.Product) ||
            binding.RequestId == Guid.Empty || binding.Quantity is < 1 or > 1000) throw new ArgumentException("Original ProdSeller binding is unavailable.");
    }
    private async Task<SupplierOutcome> SendAsync(SupplierBinding binding, HttpRequestMessage request, string? expectedOrder, CancellationToken ct)
    {
        byte[] evidence = [];
        SupplierItem[]? acquired = null;
        try
        {
            using var response = await SupplierWire.ReadAsync(http, request, ct);
            evidence = response.Evidence.ToArray();
            if (!response.Success || !response.Json) return Unknown(evidence);
            using var doc = JsonDocument.Parse(evidence, new JsonDocumentOptions { MaxDepth = 16 });
            using var receipt = Parse(doc.RootElement, binding, expectedOrder);
            if (receipt.Status == "failed") return new(SupplierOutcomeKind.Unknown, receipt.Reference, "USDT", receipt.Amount, [], evidence);
            if (receipt.Status != "delivered") return new(SupplierOutcomeKind.Processing, receipt.Reference, "USDT", receipt.Amount, [], evidence);
            var items = acquired = verify(binding, receipt);
            if (items.Length != 0 && (!approved.TryGetValue(binding.Product, out var approval) || !SupplierWire.VerifiedItems(items, receipt.Items, approval.ContentKind))) return Unknown(evidence);
            var result = new SupplierOutcome(SupplierOutcomeKind.Completed, receipt.Reference, "USDT", receipt.Amount, items, evidence);
            acquired = null; return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Unknown(evidence); }
        finally
        {
            if (acquired is not null) foreach (var item in acquired) item?.Dispose();
            CryptographicOperations.ZeroMemory(evidence);
        }
    }
    private static ProdSellerOrderReceipt Parse(JsonElement root, SupplierBinding binding, string? expectedOrder)
    {
        SupplierWire.Object(root);
        string? reference = root.GetProperty("orderId").GetString(), status = root.GetProperty("status").GetString();
        var product = root.GetProperty("product"); SupplierWire.Object(product);
        decimal amount = ExactDecimal.Parse(root.GetProperty("amount").GetRawText());
        if (!OrderId(reference) || (expectedOrder is not null && reference != expectedOrder) || product.GetProperty("id").GetString() != binding.Product ||
            root.GetProperty("quantity").GetInt32() != binding.Quantity || amount < 0 || status is not ("pending" or "paid" or "delivered" or "failed") ||
            root.TryGetProperty("activation", out var activation) && activation.ValueKind != JsonValueKind.Null) throw new FormatException();
        if (root.TryGetProperty("activationInstructions", out var instructions) && (instructions.ValueKind != JsonValueKind.Array || instructions.GetArrayLength() != 0)) throw new FormatException();
        string? single = root.TryGetProperty("deliveredKey", out var key) && key.ValueKind != JsonValueKind.Null ? key.GetString() : null;
        var values = new List<string>();
        if (root.TryGetProperty("deliveredKeys", out var keys) && keys.ValueKind != JsonValueKind.Null)
        {
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > binding.Quantity) throw new FormatException();
            foreach (var item in keys.EnumerateArray()) values.Add(item.GetString() ?? throw new FormatException());
        }
        if (single is not null)
        {
            if (binding.Quantity != 1 || values.Count > 1 || (values.Count == 1 && values[0] != single)) throw new FormatException();
            if (values.Count == 0) values.Add(single);
        }
        if (status == "delivered" ? values.Count != binding.Quantity : values.Count != 0) throw new FormatException();
        if (values.Any(v => string.IsNullOrWhiteSpace(v) || Encoding.UTF8.GetByteCount(v) > 65536)) throw new FormatException();
        return new(reference!, status!, amount, values.Select(Encoding.UTF8.GetBytes).ToArray());
    }
    private static bool ProductId(string? id) => id is { Length: 24 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static bool OrderId(string? id) => id is { Length: > 0 and <= 160 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static SupplierOutcome Unknown(ReadOnlySpan<byte> evidence) => new(SupplierOutcomeKind.Unknown, null, null, null, [], evidence.IsEmpty ? "prodseller:unconfirmed-transport-outcome"u8 : evidence);
    public void Dispose() { http.Dispose(); permits.Clear(); }
    private sealed class Authorization(SupplierCredentialSnapshot credentials, string account, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { Scheme: "https", Host: "prodseller.com", Port: 443 } uri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !(request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/orders" && request.Content is not null ||
                    request.Method == HttpMethod.Get && request.Content is null && (uri.AbsolutePath.StartsWith("/v1/products/", StringComparison.Ordinal) && ProductId(uri.AbsolutePath["/v1/products/".Length..]) ||
                        uri.AbsolutePath.StartsWith("/v1/orders/", StringComparison.Ordinal) && OrderId(uri.AbsolutePath["/v1/orders/".Length..])))) throw new UnauthorizedAccessException("Supplier transport is unavailable.");
            credentials.UseCredential("prodseller", account, key =>
            {
                if (key.Length is < 1 or > 512) throw new InvalidOperationException("Supplier credential is unavailable.");
                foreach (byte value in key) if (!char.IsAsciiLetterOrDigit((char)value) && value != (byte)'_' && value != (byte)'-') throw new InvalidOperationException("Supplier credential is unavailable.");
                request.Headers.Add("X-API-Key", Encoding.ASCII.GetString(key)); return true;
            });
            try { return await base.SendAsync(request, ct); }
            finally { request.Headers.Remove("X-API-Key"); }
        }
    }
}
