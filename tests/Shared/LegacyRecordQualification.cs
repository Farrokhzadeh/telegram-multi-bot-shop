using System.Security.Cryptography;
using System.Text.Json;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.Testing;

public sealed class LegacyRecordFixture : IAsyncDisposable
{
    private LegacyRecordFixture(CheckoutFixture fixture, VerifiedImportManifest manifest, LegacyImportSet set, Dictionary<string, ProtectedLegacyEvidence> files)
    { Fixture = fixture; Manifest = manifest; Set = set; ProtectedFiles = files; Reports = new(fixture.Database.Data, fixture.Secrets); }
    public CheckoutFixture Fixture { get; }
    public VerifiedImportManifest Manifest { get; }
    public LegacyImportSet Set { get; }
    public Dictionary<string, ProtectedLegacyEvidence> ProtectedFiles { get; }
    public LegacyRecordReconciliationReport Reports { get; }
    public Task<ProtectedLegacyEvidence> Read(string asset, CancellationToken ct) => Task.FromResult(ProtectedFiles[asset]);
    public static async Task<LegacyRecordFixture> CreateAsync(string route, CancellationToken ct)
    {
        var f = await CheckoutFixture.CreateAsync(users: 0, stock: 0, ct: ct);
        try
        {
            await MigrationQualification.EnableRoleAsync(f, ct);
            Guid batch = Guid.NewGuid(), orderId = Guid.NewGuid(), productId = Guid.NewGuid(), couponId = Guid.NewGuid(), paymentId = Guid.NewGuid();
            const string reference = "ORD-ABC123ABC123";
            bool free = route == "free", partial = route == "partial", bank = route == "bank", crypto = route == "crypto", topup = route == "topup";
            string method = bank ? "bank" : crypto ? "crypto" : "wallet", type = route is "preorder" or "api" or "service" ? route : "credential";
            string net = free ? "0" : partial ? "15" : "20", netToman = free ? "0" : partial ? "450000" : "600000", saved = free ? "20" : partial ? "5" : "0", savedToman = free ? "600000" : partial ? "150000" : "0";
            string discount = free || partial ? "\"PRIVATE-LEGACY-CODE\"" : "null", percent = free ? "\"100\"" : partial ? "\"50\"" : "null", units = free ? "2" : partial ? "1" : "0";
            var wallet = MigrationQualification.Wallet() with { LegacyUser = "99001234" };
            if (!bank && !crypto && !free) wallet = wallet with { History = wallet.History.Append(new LegacyHistoryRow(reference, Guid.NewGuid(), partial ? -15 : -20, "USD", new DateTime(2024, 2, 1, 0, 0, 1, DateTimeKind.Utc), null)).ToArray() };
            if (topup) wallet = wallet with { History = wallet.History.Append(new LegacyHistoryRow("source-payment", Guid.NewGuid(), 5, "USD", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), null)).ToArray() };
            var order = new LegacySourceRecord(LegacyProjection.OrderRecord, reference, $$$"""{"id":"{{{reference}}}","user_id":99001234,"product_id":456,"qty":2,"total":"{{{(bank ? netToman : net)}}}","currency":"{{{(bank ? "IRR" : "USD")}}}","created_at":"2024-02-01T00:00:00Z","checkout_snapshot":{"version":1,"product_id":456,"product_name":"Original private synthetic title","product_type":"{{{type}}}","quantity":2,"original_usd":"20","total_usd":"{{{net}}}","original_toman":"600000","total_toman":"{{{netToman}}}","savings_usd":"{{{saved}}}","savings_toman":"{{{savedToman}}}","discount_code":{{{discount}}},"discount_pct":{{{percent}}},"discount_quantity":{{{units}}},"currency":"{{{(bank ? "IRR" : "USD")}}}","payment_method":"{{{method}}}","expected_availability_date":"2024-03-01"}}""");
            var product = new LegacySourceRecord(LegacyProjection.ProductRecord, "456", """{"id":456,"name":"Master catalog title","price":"10","price_irr":"300000","auto_price":false,"description":"Original description","rules":null,"icon_custom_emoji_id":null,"post_purchase":"Original guidance","post_purchase_media_id":null,"product_type":"credential","active":true,"is_removed":false,"stock":123}""");
            var coupon = new LegacySourceRecord(LegacyProjection.DiscountRecord, "PRIVATE-COUPON", """{"code":"PRIVATE-COUPON","product_id":456,"assigned_user_id":99001234,"pct_off":"25","max_uses":10,"current_uses":3,"active":true}""");
            var header = new LegacySourceRecord(LegacyProjection.Delivery, "10", $$$"""{"id":10,"order_id":"{{{reference}}}","status":"sent","manifest":{"generation":2,"private":"original partial evidence"},"generation":2,"completed_at":"2024-02-02T00:00:00Z"}""");
            var part = new LegacySourceRecord(LegacyProjection.DeliveryPart, "11", """{"id":11,"delivery_id":10,"key":"item-1","message_id":123456,"sent_at":"2024-02-02T00:00:00Z"}""");
            LegacySourceRecord? payment = null;
            if (bank) payment = new(LegacyProjection.ManualDeposit, "source-payment", $$$"""{"id":"source-payment","user_id":99001234,"amount":"{{{netToman}}}","fulfilled_order_id":"{{{reference}}}","status":"approved","created_at":"2024-01-31T00:00:00Z","reviewed_at":"2024-02-01T00:00:00Z","reviewed_by":1234,"proof_data":"private original receipt","bank_reference":"private original reference"}""");
            else if (crypto || topup) payment = new(LegacyProjection.PaymentAttempt, "source-payment", $$$"""{"id":"source-payment","user_id":99001234,"kind":"{{{(topup ? "wallet_topup" : "direct_checkout")}}}","expected_amount":"{{{(topup ? "5" : "20")}}}","expected_currency":"USD","pay_currency":"usdtbsc","fulfilled_order_id":{{{(topup ? "null" : "\"" + reference + "\"")}}},"status":"{{{(topup ? "credited" : "fulfilled")}}}","created_at":"2024-01-31T00:00:00Z","paid_at":"2024-02-01T00:00:00Z","provider_invoice_id":"private-invoice","provider_payment_id":"123456789","provider_status":"finished","provider_response":{"private":"original payment payload"}}""");
            LegacySourceRecord? zero = free ? new(LegacyProjection.ZeroTransaction, reference, $$$"""{"id":"{{{reference}}}","user_id":99001234,"amount":0,"currency":"USD","created_at":"2024-02-01T00:00:01Z","desc":"Original zero-cost order"}""") : null;
            var raw = new List<(LegacySourceRecord Source, Guid Target)> { (order, orderId), (product, productId), (coupon, couponId) };
            string[] legacyKeys = [];
            if (route is "preorder" or "service" or "api")
            {
                using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-fernet-synthetic.json")));
                var vector = vectors.RootElement.GetProperty("vectors").EnumerateArray().First(v => v.GetProperty("kind").GetString() == (route == "service" ? "Json" : "Text"));
                legacyKeys = vector.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!).ToArray(); string ciphertext = vector.GetProperty("ciphertext").GetString()!;
                if (route == "preorder")
                {
                    raw.Add((new(LegacyProjection.InventoryUnit, "20", $$$"""{"id":20,"order_id":"{{{reference}}}","product_id":456,"data":"{{{ciphertext}}}","created_at":"2024-02-02T00:00:00Z"}"""), orderId));
                    raw.Add((new(LegacyProjection.PreorderBatch, "21", $$$"""{"id":21,"order_id":"{{{reference}}}","batch_index":1,"status":"sent","generation":2,"manifest":{"items":[20]},"completed_at":"2024-02-02T00:00:00Z"}"""), orderId));
                    raw.Add((new(LegacyProjection.PreorderPart, "22", """{"id":22,"batch_id":21,"key":"item-1","message_id":123456,"sent_at":"2024-02-02T00:00:00Z"}"""), orderId));
                    raw.Add((new(LegacyProjection.PreorderItem, "23", """{"id":23,"preorderdeliverybatch_id":21,"inventoryitem_id":20}"""), orderId));
                }
                else if (route == "service") raw.Add((new(LegacyProjection.ServiceJob, "original-service", $$$"""{"id":"original-service","order_id":"{{{reference}}}","user_id":99001234,"product_id":456,"status":"in_progress","submitted_data":"{{{ciphertext}}}","created_at":"2024-02-01T00:00:00Z","expected_completion_at":"2024-03-01T00:00:00Z"}"""), orderId));
                else
                {
                    raw.Add((new(LegacyProjection.SupplierFulfillment, "30", $$$"""{"id":30,"order_id":"{{{reference}}}","provider_key":"synthetic","requested_quantity":2,"fulfilled_quantity":1,"state":"partial"}"""), orderId));
                    raw.Add((new(LegacyProjection.SupplierItem, "31", $$$"""{"id":31,"fulfillment_id":30,"unit_index":1,"remote_item_id":"original-unit","content":"{{{ciphertext}}}","content_kind":"text","created_at":"2024-02-02T00:00:00Z"}"""), orderId));
                    raw.Add((new(LegacyProjection.SupplierAttempt, "32", """{"id":32,"fulfillment_id":30,"attempt_number":1,"operation":"export","outcome":"unknown","started_at":"2024-02-02T00:00:00Z"}"""), orderId));
                    raw.Add((new(LegacyProjection.SupplierRefundArchive, "33", $$$"""{"id":33,"order_id":"{{{reference}}}","amount":"1","currency":"USD","state":"completed","external_settlement_reference":"{{{ciphertext}}}","created_at":"2024-02-03T00:00:00Z"}"""), orderId));
                }
            }
            if (route != "preorder") { raw.Add((header, orderId)); raw.Add((part, orderId)); raw.Add((new(LegacyProjection.DeliveryAttempt, "12", $$$"""{"id":12,"delivery_id":10,"generation":2,"source":"worker","actor_id":null,"started_at":"2024-02-01T23:59:00Z","finished_at":"2024-02-02T00:00:00Z","outcome":"{{{(route == "delivery_retry" ? "retry_wait" : "sent")}}}","error_code":""}"""), orderId)); }
            if (payment is not null) raw.Add((payment, paymentId)); if (zero is not null) raw.Add((zero, orderId));
            var initial = LegacyRecordQualification.Manifest(f, batch, wallet.Proofs(f.Brands[0]).Concat(raw.Select(r => r.Source.Proof(r.Target, f.Context(0), f.Secrets))), wallet.BalanceUsd);
            var protectedFiles = new Dictionary<string, ProtectedLegacyEvidence>(); var archived = new Dictionary<LegacyProjection, ArchivedLegacyRecord>();
            var archive = new LegacyRecordArchive(f.Secrets);
            foreach (var row in raw)
                archived[row.Source.Projection] = await archive.WriteAsync(f.Context(0), initial, row.Source, row.Target, (evidence, _) => { string asset = "protected:synthetic/record-" + protectedFiles.Count; protectedFiles[asset] = evidence; return Task.FromResult(asset); }, ct, legacyKeys);
            var historical = new LegacyHistoricalRecord(archived[LegacyProjection.OrderRecord], wallet.TargetUser, "Original private brand");
            var obligation = new LegacyCouponRecord(archived[LegacyProjection.DiscountRecord], Guid.NewGuid(), productId, wallet.TargetUser, "promotion");
            var manifest = LegacyRecordQualification.Manifest(f, batch, wallet.Proofs(f.Brands[0]).Concat(historical.Proofs(f.Context(0), f.Secrets)).Concat(obligation.Proofs(f.Context(0), f.Secrets)).Concat(raw.Where(r => r.Source.Projection is not (LegacyProjection.OrderRecord or LegacyProjection.DiscountRecord)).Select(r => r.Source.Proof(r.Target, f.Context(0), f.Secrets))), wallet.BalanceUsd);
            await new OpeningBalanceImportWorkflow(f.Sessions).ImportAsync(f.Context(0), manifest, wallet, ct);
            var catalog = new LegacyCatalogImportWorkflow(f.Sessions, f.Secrets, f.Brands[0]); await catalog.ImportAsync(f.Context(0), manifest, archived[LegacyProjection.ProductRecord], ct);
            var orders = new LegacyOrderRecordImportWorkflow(f.Sessions, f.Secrets); await orders.ImportAsync(f.Context(0), manifest, historical, ct); await orders.ImportAsync(f.Context(0), manifest, historical, ct);
            var couponStore = new LegacyCouponImportWorkflow(f.Sessions, f.Secrets); await couponStore.ImportAsync(f.Context(0), manifest, obligation, ct); await couponStore.ImportAsync(f.Context(0), manifest, obligation, ct);
            LegacyPaymentRecord[] payments = payment is null ? [] : [new(archived[payment.Projection], wallet.TargetUser, topup ? null : orderId)];
            foreach (var row in payments) { var store = new LegacyPaymentImportWorkflow(f.Sessions, f.Secrets); await store.ImportAsync(f.Context(0), manifest, row, ct); await store.ImportAsync(f.Context(0), manifest, row, ct); }
            var delivery = archived.Values.Where(r => r.Source.Projection is not (LegacyProjection.OrderRecord or LegacyProjection.DiscountRecord or LegacyProjection.ProductRecord or LegacyProjection.ManualDeposit or LegacyProjection.PaymentAttempt)).ToArray();
            foreach (var row in delivery) { var store = new LegacyDeliveryImportWorkflow(f.Sessions, f.Secrets); await store.ImportAsync(f.Context(0), manifest, row, ct); await store.ImportAsync(f.Context(0), manifest, row, ct); }
            var set = new LegacyImportSet([wallet], [historical], payments, [obligation], [archived[LegacyProjection.ProductRecord]], delivery);
            return new(f, manifest, set, protectedFiles);
        }
        catch { await f.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync() => Fixture.DisposeAsync();
}
public static class LegacyRecordQualification
{
    public static VerifiedImportManifest Manifest(CheckoutFixture f, Guid batch, IEnumerable<LegacyMapProof> proofs, decimal opening = 0, byte[]? schema = null)
    {
        byte[] bytes = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification(f.Database.Data.ConnectionString), "public-a", f.Brands[0], "66371a23a1c8149dc5bdf89a0355aa689d454dba", schema ?? SHA256.HashData("synthetic schema"u8), "protected:synthetic/frozen-copy", SHA256.HashData("synthetic frozen copy"u8), batch, proofs, opening);
        return VerifiedImportManifest.Open("protected:synthetic/record-plan", SHA256.HashData(bytes), bytes);
    }
    public static async Task RunAsync(CancellationToken ct = default)
    {
        foreach (string route in new[] { "bank", "crypto", "topup", "free", "partial", "preorder", "api", "service", "delivery_retry" })
        {
            await using var world = await LegacyRecordFixture.CreateAsync(route, ct);
            var report = await world.Reports.CompareAsync(world.Fixture.Context(0), world.Manifest, world.Set, world.Read, ct);
            if (!report.Matched || report.ReleaseReady || await world.Fixture.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE is_posting", ct) != 1 ||
                await world.Fixture.CountAsync("SELECT count(*) FROM shop.inventory_items", ct) != 0 || await world.Fixture.CountAsync("SELECT count(*) FROM shop.jobs", ct) != 0 ||
                await world.Fixture.CountAsync("SELECT count(*) FROM shop.telegram_deliveries", ct) != 0) throw new InvalidOperationException("Native protected legacy record qualification failed.");
        }
    }
}
