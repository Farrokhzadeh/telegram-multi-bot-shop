using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Core;
using Platform.Persistence;
using Platform.Security;

namespace Platform.Fulfillment.Providers;

public sealed record AkundingProductApproval(long ProductId, string ContentKind, string EligibilityFingerprint);
public sealed class AkundingOrderReceipt : IDisposable
{
    private readonly byte[][] items;
    public long Id { get; }
    public string Status { get; }
    public decimal Payable { get; }
    public IReadOnlyList<ReadOnlyMemory<byte>> Items => items.Select(i => new ReadOnlyMemory<byte>(i)).ToArray();
    internal AkundingOrderReceipt(long id, string status, decimal payable, byte[][] items)
    { Id = id; Status = status; Payable = payable; this.items = items; }
    public void Dispose() { foreach (byte[] item in items) CryptographicOperations.ZeroMemory(item); }
    public override string ToString() => "AkundingOrderReceipt [redacted]";
}
public sealed class AkundingSupplierAdapter : ISupplierAdapter, IDisposable
{
    private readonly HttpClient http;
    private readonly AkundingCatalogClient catalog;
    private readonly Dictionary<long, AkundingProductApproval> approved;
    private readonly Func<SupplierBinding, AkundingOrderReceipt, SupplierItem[]> verify;
    private readonly Dictionary<Guid, (SupplierBinding Binding, SupplierQuote Quote)> permits = [];
    private readonly object gate = new();
    public string Provider => "akunding";
    public string Account { get; }
    public bool ProvenIntentLookup => false;
    public AkundingSupplierAdapter(SecretSnapshot commerce, SupplierCredentialSnapshot credentials, string account, IEnumerable<AkundingProductApproval> approvals,
        Func<SupplierBinding, AkundingOrderReceipt, SupplierItem[]> verifyItems, HttpMessageHandler? transport = null)
    {
        if (credentials.DeploymentId != commerce.DeploymentId || credentials.Service != commerce.Service) throw new UnauthorizedAccessException("Supplier credential scope is unavailable.");
        credentials.UseCredential("akunding", account, key => key.Length);
        ArgumentNullException.ThrowIfNull(verifyItems);
        approved = [];
        foreach (var item in approvals)
            if (item.ProductId <= 0 || item.ContentKind is not ("account" or "key" or "code") || item.EligibilityFingerprint.Length != 64 || !item.EligibilityFingerprint.All(char.IsAsciiHexDigit) || !approved.TryAdd(item.ProductId, item))
                throw new ArgumentException("Exact reviewed Akunding products are required.");
        Account = account; verify = verifyItems;
        http = new(new Authorization(credentials, account, transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
        catalog = new(http);
    }
    public async Task<SupplierQuote> InspectAsync(SupplierBinding binding, CancellationToken ct)
    {
        long product = Require(binding);
        var item = await catalog.GetProductAsync(product, ct);
        bool ready = approved.TryGetValue(product, out var approval) && item.EligibilityFingerprint == approval.EligibilityFingerprint && item.Stock >= binding.Quantity && item.Available;
        var quote = new SupplierQuote(Provider, Account, binding.Product, null, "USDT", item.UnitPrice, ready, false, DateTime.UtcNow, "akunding:product/" + binding.Product);
        lock (gate)
        {
            foreach (var id in permits.Where(p => DateTime.UtcNow - p.Value.Quote.ObservedAt > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) permits.Remove(id);
            if (permits.Count >= 1000 && !permits.ContainsKey(binding.RequestId)) throw new InvalidOperationException("Supplier inspection capacity is unavailable.");
            permits[binding.RequestId] = (binding, quote);
        }
        return quote;
    }
    public async Task<SupplierCatalogResult> InspectCatalogAsync(SupplierCatalogRequest request, CancellationToken ct)
    {
        if (request.Provider != Provider || request.AccountRef != Account || request.VariantRef is not null || request.Quantity is < 1 or > 1000 ||
            !long.TryParse(request.ProductRef, NumberStyles.None, CultureInfo.InvariantCulture, out long product) || product <= 0 || request.ProductRef != product.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException("Exact Akunding catalog scope is required.");
        var item = await catalog.GetProductAsync(product, ct);
        bool ready = approved.TryGetValue(product, out var approval) && item.EligibilityFingerprint == approval.EligibilityFingerprint;
        return new(!ready ? SupplierCatalogStatus.Ineligible : item.Available ? SupplierCatalogStatus.Available : SupplierCatalogStatus.OutOfStock, item.Stock, item.UnitPrice, "USDT", "akunding:product/" + request.ProductRef, ready);
    }
    public async Task<SupplierOutcome> SubmitAsync(SupplierBinding binding, decimal maximumUnitCharge, CancellationToken ct)
    {
        long product = Require(binding);
        lock (gate)
        {
            if (!permits.Remove(binding.RequestId, out var permit) || permit.Binding != binding || maximumUnitCharge != SupplierRules.UnitCeiling(binding) || SupplierRules.Preflight(binding, permit.Quote, DateTime.UtcNow) is not null)
                return new(SupplierOutcomeKind.ConfirmedNoEffect, null, "USDT", 0, [], "akunding:local-prepurchase-rejection"u8);
        }
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        { writer.WriteStartObject(); writer.WriteNumber("product_id", product); writer.WriteNumber("quantity", binding.Quantity); writer.WriteEndObject(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://akunding.shop/api/v1/orders") { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Idempotency-Key", binding.Intent);
        return await SendAsync(binding, request, null, ct);
    }
    public async Task<SupplierOutcome> LookupAsync(SupplierBinding binding, string? originalPurchaseReference, CancellationToken ct)
    {
        Require(binding);
        if (!long.TryParse(originalPurchaseReference, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 || originalPurchaseReference != id.ToString(CultureInfo.InvariantCulture))
            return new(SupplierOutcomeKind.Unknown, null, null, null, [], "akunding:original-order-reference-required"u8);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://akunding.shop/api/v1/orders/" + originalPurchaseReference);
        return await SendAsync(binding, request, id, ct);
    }
    private long Require(SupplierBinding binding)
    {
        if (binding.Provider != Provider || binding.Account != Account || binding.Variant is not null || binding.Currency != "USDT" || binding.Quantity is < 1 or > 1000 || binding.RequestId == Guid.Empty ||
            binding.Intent.Length is < 1 or > 120 || binding.Intent.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or ':' or '.')) ||
            !long.TryParse(binding.Product, NumberStyles.None, CultureInfo.InvariantCulture, out long product) || product <= 0 || binding.Product != product.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Original Akunding binding is unavailable.");
        return product;
    }
    private async Task<SupplierOutcome> SendAsync(SupplierBinding binding, HttpRequestMessage request, long? expectedOrder, CancellationToken ct)
    {
        byte[] evidence = [];
        try
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
                evidence = bytes.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                CryptographicOperations.ZeroMemory(bytes.GetBuffer());
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") return Unknown(evidence);
            using var doc = JsonDocument.Parse(evidence, new JsonDocumentOptions { MaxDepth = 16 });
            using var receipt = Parse(doc.RootElement, binding, expectedOrder);
            string reference = receipt.Id.ToString(CultureInfo.InvariantCulture);
            if (receipt.Status == "failed") return new(SupplierOutcomeKind.ConfirmedNoEffect, reference, "USDT", 0, [], evidence);
            if (receipt.Status == "draft") return new(SupplierOutcomeKind.Unknown, reference, null, null, [], evidence);
            if (receipt.Status == "paid") return new(SupplierOutcomeKind.Processing, reference, "USDT", receipt.Payable, [], evidence);
            SupplierItem[] items = verify(binding, receipt);
            bool valid = items.Length == 0 || (approved.TryGetValue(long.Parse(binding.Product, CultureInfo.InvariantCulture), out var approval) && items.Length == receipt.Items.Count &&
                items.All(i => i.Disposition == SupplierItemDisposition.Customer && i.ContentKind == approval.ContentKind) &&
                items.Select(i => i.Reference).Distinct(StringComparer.Ordinal).Count() == items.Length &&
                receipt.Items.Select(raw => Convert.ToHexString(SHA256.HashData(raw.Span))).Order(StringComparer.Ordinal).SequenceEqual(items.Select(i => Convert.ToHexString(SHA256.HashData(i.Content.Span))).Order(StringComparer.Ordinal)));
            if (!valid)
            {
                foreach (var item in items) item.Dispose();
                return Unknown(evidence);
            }
            return new(SupplierOutcomeKind.Completed, reference, "USDT", receipt.Payable, items, evidence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        { return Unknown(evidence); }
        finally { CryptographicOperations.ZeroMemory(evidence); }
    }
    private static SupplierOutcome Unknown(ReadOnlySpan<byte> evidence) => new(SupplierOutcomeKind.Unknown, null, null, null, [], evidence.IsEmpty ? "akunding:unconfirmed-transport-outcome"u8 : evidence);
    private static AkundingOrderReceipt Parse(JsonElement root, SupplierBinding binding, long? expected)
    {
        Object(root);
        long id = root.GetProperty("id").GetInt64();
        var product = root.GetProperty("product"); Object(product);
        string? status = root.GetProperty("status").GetString();
        decimal unit = root.GetProperty("unit_price").GetDecimal(), subtotal = root.GetProperty("subtotal").GetDecimal(), discount = root.GetProperty("discount").GetDecimal(), payable = root.GetProperty("payable").GetDecimal();
        string? method = root.GetProperty("payment_method").GetString();
        var array = root.GetProperty("items");
        if (id <= 0 || (expected.HasValue && id != expected.Value) || product.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture) != binding.Product ||
            root.GetProperty("quantity").GetInt32() != binding.Quantity || status is not ("delivered" or "paid" or "failed" or "draft") ||
            unit <= 0 || subtotal != checked(unit * binding.Quantity) || discount != 0 || payable != subtotal || (status != "draft" && method != "wallet") ||
            array.ValueKind != JsonValueKind.Array || (status == "delivered" ? array.GetArrayLength() != binding.Quantity : array.GetArrayLength() != 0)) throw new FormatException();
        if (root.TryGetProperty("charged", out var charged) && (status is "delivered" or "paid") && charged.GetDecimal() != payable) throw new FormatException();
        var items = new List<byte[]>();
        try
        {
            foreach (var item in array.EnumerateArray())
            {
                string? text = item.GetString();
                if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 65536) throw new FormatException();
                items.Add(Encoding.UTF8.GetBytes(text));
            }
            return new(id, status!, payable, items.ToArray());
        }
        catch { foreach (byte[] item in items) CryptographicOperations.ZeroMemory(item); throw; }
    }
    private static void Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count()) throw new FormatException();
    }
    public void Dispose() { http.Dispose(); lock (gate) permits.Clear(); }
    private sealed class Authorization(SupplierCredentialSnapshot credentials, string account, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { Scheme: "https", Host: "akunding.shop", Port: 443 } uri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                !(request.Method == HttpMethod.Post && uri.AbsolutePath == "/api/v1/orders" || request.Method == HttpMethod.Get && (uri.AbsolutePath.StartsWith("/api/v1/products/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/api/v1/orders/", StringComparison.Ordinal))))
                throw new UnauthorizedAccessException("Supplier transport is unavailable.");
            credentials.UseCredential("akunding", account, key =>
            {
                if (key.Length is < 1 or > 256) throw new InvalidOperationException("Supplier credential is unavailable.");
                foreach (byte value in key) if (!char.IsAsciiLetterOrDigit((char)value) && value != (byte)'_' && value != (byte)'-') throw new InvalidOperationException("Supplier credential is unavailable.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(key)); return true;
            });
            try { return await base.SendAsync(request, ct); }
            finally { request.Headers.Authorization = null; }
        }
    }
}
