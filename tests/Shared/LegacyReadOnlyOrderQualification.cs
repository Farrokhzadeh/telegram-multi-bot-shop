using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Migration;
using Platform.Migration.Recovery;
namespace Platform.Testing;

public static class LegacyReadOnlyOrderQualification
{
    public static async Task RunAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var files = new SyntheticSecretFiles(); using var secrets = SecretFiles.Load(files.Root, "service-config.json", files.Grant());
        var context = CipherQualification.Context(SyntheticSecretFiles.BrandA); var other = CipherQualification.Context(SyntheticSecretFiles.BrandB); Guid member = Guid.NewGuid();
        var body = JsonNode.Parse("""{"id":"ORD-ABC123ABC123","user_id":99001234,"product_id":456,"qty":2,"total":"600000","currency":"IRR","created_at":"2024-02-01T00:00:00Z","checkout_snapshot":{}}""")!;
        var source = new LegacySourceRecord(LegacyProjection.OrderRecord, "ORD-ABC123ABC123", body.ToJsonString()); Guid target = Guid.NewGuid();
        VerifiedImportManifest Plan(LegacySourceRecord record, Guid identity)
        {
            byte[] plan = VerifiedImportManifest.EncodePlan(SchemaManifest.ForQualification("Host=127.0.0.1;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner"), "public-a", context.BrandId, "66371a23a1c8149dc5bdf89a0355aa689d454dba", SHA256.HashData("synthetic schema"u8), "protected:synthetic/frozen-copy", SHA256.HashData("synthetic frozen copy"u8), Guid.NewGuid(), [new(LegacyProjection.User, "99001234", member, context.BrandId, SHA256.HashData("synthetic member"u8)), record.Proof(identity, context, secrets)]);
            return VerifiedImportManifest.Open("protected:synthetic/read-only-plan", SHA256.HashData(plan), plan);
        }
        var manifest = Plan(source, target);
        var archive = new LegacyRecordArchive(secrets); ProtectedLegacyEvidence? retained = null;
        var row = await archive.WriteAsync(context, manifest, source, target, (evidence, _) => { retained = evidence; return Task.FromResult("protected:synthetic/incomplete-original-order"); }, ct);
        var reader = new LegacyReadOnlyOrderHistory(secrets);
        var result = reader.Read(context, manifest, member, row, retained!);
        var customer = reader.ReadCustomer(context, manifest, member, row, retained!);
        if (customer != reader.ReadCustomer(context, manifest, member, row, retained!) || customer.OriginalReference != source.Id || customer.OriginalCurrency != "IRR" || customer.OriginalTotal != 600000 || customer.Quantity != 2 || customer.SavedTitle is not null || customer.SavedPaymentMethod is not null || !customer.IncompleteTerms || !customer.Historical || !customer.ReadOnly || customer.PaymentStatus != "unknown" || customer.DeliveryStatus != "unknown" || customer.CanPay || customer.CanRefund || customer.CanDeliver || customer.ToString() != "LegacyCustomerOrderView [redacted]") throw new InvalidOperationException("Historical customer view inferred missing terms or unsafe actions.");
        if (result != reader.Read(context, manifest, member, row, retained!) || result.ArchiveId != target || result.OriginalReference != source.Id || result.OriginalCurrency != "IRR" || result.OriginalTotal != 600000 || result.Quantity != 2 || result.SavedTitle is not null || result.SavedPaymentMethod is not null || !result.IncompleteTerms || !result.ReadOnly || result.ImportApproved || result.SettlementVerified || result.DeliveryVerified || result.ToString() != "LegacyReadOnlyOrder [redacted]") throw new InvalidOperationException("Read-only original order facts changed.");
        void Reject(Action action) { try { action(); throw new InvalidOperationException("Unverified historical order access accepted."); } catch (MigrationRejectedException) { } }
        Reject(() => reader.Read(context, manifest, Guid.NewGuid(), row, retained!));
        Reject(() => reader.ReadCustomer(context, manifest, Guid.NewGuid(), row, retained!));
        Reject(() => reader.Read(other, manifest, member, row, retained!));
        Reject(() => reader.ReadCustomer(other, manifest, member, row, retained!));
        byte[] changed = retained!.Ciphertext.ToArray(); changed[^1] ^= 1;
        Reject(() => reader.Read(context, manifest, member, row, retained with { Ciphertext = changed }));
        Reject(() => reader.ReadCustomer(context, manifest, member, row, retained with { Ciphertext = changed }));
        body["checkout_snapshot"] = JsonNode.Parse("""{"product_name":"Saved original title","payment_method":"bank"}""");
        var namedSource = source with { Body = body.ToJsonString() }; Guid namedTarget = Guid.NewGuid();
        var namedManifest = Plan(namedSource, namedTarget);
        var namedRow = await archive.WriteAsync(context, namedManifest, namedSource, namedTarget, (_, _) => Task.FromResult("protected:synthetic/saved-original-title"), ct);
        var named = reader.ReadCustomer(context, namedManifest, member, namedRow, namedRow.Evidence);
        if (named.SavedTitle != "Saved original title" || named.SavedPaymentMethod != "bank" || !named.IncompleteTerms || named.PaymentStatus != "unknown" || named.DeliveryStatus != "unknown") throw new InvalidOperationException("Original saved title or payment method was inferred as verified.");
        body["checkout_snapshot"] = new JsonObject();
        var history = new CustomerHistoryArchive(secrets); var projection = new LegacyCustomerHistoryProjection(secrets);
        var exported = projection.EncodeAll(context, manifest, [row]);
        if (exported.Count != 1 || !exported.TryGetValue(member, out var protectedHistory)) throw new InvalidOperationException("Member history projection lost the source record.");
        var expected = new CustomerHistoricalOrder(customer.OriginalReference, customer.CreatedAt, customer.Quantity,
            customer.OriginalTotal, customer.OriginalCurrency, customer.SavedTitle, customer.SavedPaymentMethod, customer.IncompleteTerms);
        byte[] SealLegacy(IReadOnlyList<CustomerHistoricalOrder> orders)
        {
            byte[] content = CustomerHistoryContent.Encode(member, orders);
            try { return new BrandCipher(secrets).Encrypt(context, "customer_history", member, "orders", content); }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
        var many = Enumerable.Range(0, 55).Select(n => new CustomerHistoricalOrder("ORD-" + n.ToString("D12"), new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n), 2, 600000, "IRR", null, null, true)).ToArray();
        var pages = SealLegacy(many);
        if (history.Page(context, member, pages, 0).NextOffset != 20 || history.Page(context, member, pages, 20).NextOffset != 40 || history.Page(context, member, pages, 40).Orders.Count != 15 || history.Page(context, member, pages, 40).NextOffset is not null) throw new InvalidOperationException("History pagination lost original order coverage.");
        try { _ = history.Page(context, Guid.NewGuid(), pages, 0); throw new InvalidOperationException("Foreign-member history read accepted."); } catch (UnauthorizedAccessException) { }
        try { _ = history.Page(other, member, pages, 0); throw new InvalidOperationException("Foreign-brand history read accepted."); } catch (UnauthorizedAccessException) { }
        try { _ = SealLegacy([many[0], many[0]]); throw new InvalidOperationException("Duplicate original order was accepted."); } catch (UnauthorizedAccessException) { }
        byte[] corrupted = pages.ToArray(); corrupted[^1] ^= 1;
        try { _ = history.Page(context, member, corrupted, 0); throw new InvalidOperationException("Tampered customer history was accepted."); } catch (UnauthorizedAccessException) { }
        string directory = Path.Combine(Path.GetTempPath(), "teleg-customer-history-" + Guid.NewGuid().ToString("N") + "-v2");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var storage = new CustomerHistoryFiles(directory);
            await storage.PublishAsync(context, member, protectedHistory, ct);
            var stored = await storage.ReadAsync(context, member, ct);
            if (!stored.Span.SequenceEqual(protectedHistory) || !(await storage.ReadOrdersAsync(context, member, ct)).SequenceEqual([expected])) throw new InvalidOperationException("Stored history differs.");
            await storage.PublishAsync(context, member, protectedHistory, ct);
            try { await storage.PublishAsync(context, member, CustomerHistoryContent.Encode(member, many), ct); throw new InvalidOperationException("Changed immutable history accepted."); } catch (UnauthorizedAccessException) { }
            try { _ = await storage.ReadAsync(other, member, ct); throw new InvalidOperationException("Foreign-brand history read accepted."); } catch (UnauthorizedAccessException) { }
            File.SetUnixFileMode(Path.Combine(directory, $"{context.BrandId:N}-{member:N}.history-v2"), UnixFileMode.UserRead | UnixFileMode.GroupRead);
            try { _ = await storage.ReadAsync(context, member, ct); throw new InvalidOperationException("Public-readable history accepted."); } catch (UnauthorizedAccessException) { }
            File.SetUnixFileMode(Path.Combine(directory, $"{context.BrandId:N}-{member:N}.history-v2"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { Directory.Delete(directory, true); }
        foreach (string currency in new[] { "USD", "UNKNOWN" })
        {
            body["currency"] = currency; var next = source with { Body = body.ToJsonString() }; Guid nextTarget = Guid.NewGuid();
            var nextManifest = Plan(next, nextTarget);
            var nextRow = await archive.WriteAsync(context, nextManifest, next, nextTarget, (_, _) => Task.FromResult("protected:synthetic/other-original-denomination"), ct);
            if (currency == "UNKNOWN") Reject(() => reader.Read(context, nextManifest, member, nextRow, nextRow.Evidence));
            else if (reader.Read(context, nextManifest, member, nextRow, nextRow.Evidence).OriginalCurrency != "USD") throw new InvalidOperationException("Original USD history changed.");
        }

    }
}
