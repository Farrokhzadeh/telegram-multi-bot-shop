using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Persistence;
using Platform.Security;
namespace Platform.Migration;
public sealed record LegacySourceRecord(LegacyProjection Projection, string Id, string Body)
{
    internal static IReadOnlyList<LegacyProjection> Projections { get; } = Array.AsReadOnly<LegacyProjection>([LegacyProjection.OrderRecord, LegacyProjection.ProductRecord, LegacyProjection.DiscountRecord, LegacyProjection.PaymentAttempt, LegacyProjection.ManualDeposit, LegacyProjection.InventoryUnit, LegacyProjection.Delivery, LegacyProjection.DeliveryPart, LegacyProjection.PreorderBatch, LegacyProjection.PreorderPart, LegacyProjection.PreorderItem, LegacyProjection.SupplierFulfillment, LegacyProjection.SupplierItem, LegacyProjection.SupplierAttempt, LegacyProjection.SupplierRefundArchive, LegacyProjection.PreorderState, LegacyProjection.PreorderRefundArchive, LegacyProjection.ServiceJob, LegacyProjection.ZeroTransaction]);
    internal static string Table(LegacyProjection projection) => projection switch
    {
        LegacyProjection.SupplierFulfillment => "supplier_fulfillments", LegacyProjection.SupplierItem => "supplier_fulfillment_items", LegacyProjection.SupplierAttempt => "supplier_procurement_attempts", LegacyProjection.SupplierRefundArchive => "supplier_refund_records", LegacyProjection.PreorderState => "preorder_order_states", LegacyProjection.PreorderRefundArchive => "preorder_refund_records", LegacyProjection.ServiceJob => "service_jobs", LegacyProjection.ZeroTransaction => "transactions", LegacyProjection.OrderRecord => "orders", LegacyProjection.DiscountRecord => "discounts", LegacyProjection.ProductRecord => "products", LegacyProjection.PaymentAttempt => "payment_attempts", LegacyProjection.ManualDeposit => "manual_deposits", LegacyProjection.InventoryUnit => "inventory",
        LegacyProjection.Delivery => "common_orderdelivery", LegacyProjection.DeliveryPart => "common_orderdeliverypart", LegacyProjection.PreorderBatch => "common_preorderdeliverybatch",
        LegacyProjection.PreorderPart => "common_preorderdeliverybatchpart", LegacyProjection.PreorderItem => "common_preorderdeliverybatch_items", _ => throw new MigrationRejectedException()
    };
    internal JsonDocument Open()
    {
        _ = Table(Projection);
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(char.IsControl) || Body is null || Body.Length > 1048576) throw new MigrationRejectedException();
        try
        {
            var document = JsonDocument.Parse(Body); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                !root.TryGetProperty(Projection == LegacyProjection.DiscountRecord ? "code" : "id", out var id) || (id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText()) != Id ||
                (Projection == LegacyProjection.InventoryUnit && (!root.TryGetProperty("order_id", out var order) || order.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(order.GetString()))))
            { document.Dispose(); throw new MigrationRejectedException(); }
            if (Projection == LegacyProjection.ZeroTransaction && (!root.TryGetProperty("amount", out var amount) || (amount.ValueKind == JsonValueKind.String ? amount.GetString() : amount.GetRawText()) is not string number || Platform.Core.ExactDecimal.Parse(number) != 0)) { document.Dispose(); throw new MigrationRejectedException(); }
            return document;
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException) { throw new MigrationRejectedException(); }
    }
    public LegacyMapProof Proof(Guid target, BrandContext context, SecretSnapshot secrets)
    {
        using var document = Open();
        byte[] bytes = Encoding.UTF8.GetBytes("legacy-full-record-v1:" + Table(Projection) + ":" + Id + ":" + Body);
        try { return new(Projection, Id, target, context.BrandId, secrets.UseBrandKey(context, secrets.CurrentBrandKeyId(context), key => HMACSHA256.HashData(key, bytes))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override string ToString() => "LegacySourceRecord [redacted]";
}
public sealed partial class LegacySourceReader
{
    public async Task<LegacySourceRecord> ReadRecordAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest, LegacyProjection projection, string id, Guid target, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand || string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new MigrationRejectedException();
        string table = LegacySourceRecord.Table(projection), key = projection == LegacyProjection.DiscountRecord ? "code" : "id";
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        if (projection == LegacyProjection.OrderRecord && await session.QuerySingleAsync(new DbStatement("SELECT to_regclass('public.order_items') IS NOT NULL"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        string body = await session.QuerySingleAsync(new DbStatement("SELECT to_jsonb(s)::text FROM public." + table + " s WHERE s." + key + "::text=$1", DbValue.Text(id)), r => r.GetString(0), ct);
        var row = new LegacySourceRecord(projection, id, body); manifest.Require(row.Proof(target, context, secrets)); return row;
    }
    public async Task<long> VerifyRecordCoverageAsync(BrandContext context, SecretSnapshot secrets, VerifiedImportManifest manifest, LegacyProjection projection, CancellationToken ct = default)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException();
        string table = LegacySourceRecord.Table(projection), key = projection == LegacyProjection.DiscountRecord ? "code" : "id", predicate = projection == LegacyProjection.InventoryUnit ? "s.order_id IS NOT NULL" : projection == LegacyProjection.ZeroTransaction ? "s.amount=0" : projection == LegacyProjection.ProductRecord ? "s.active AND NOT s.is_removed" : "true";
        var expected = manifest.Proofs.Where(p => p.Projection == projection).ToDictionary(p => p.LegacyId, StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        if (projection == LegacyProjection.OrderRecord && await session.QuerySingleAsync(new DbStatement("SELECT to_regclass('public.order_items') IS NOT NULL"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        long count = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public." + table + " s WHERE " + predicate), r => r.GetInt64(0), ct);
        if (count != expected.Count) throw new MigrationRejectedException();
        string last = ""; long seen = 0;
        while (true)
        {
            var rows = await session.QueryManyAsync(new DbStatement("SELECT s." + key + "::text,to_jsonb(s)::text FROM public." + table + " s WHERE " + predicate + " AND s." + key + "::text COLLATE \"C\" > $1 COLLATE \"C\" ORDER BY s." + key + "::text COLLATE \"C\" LIMIT 1000", DbValue.Text(last)), r => new LegacySourceRecord(projection, r.GetString(0), r.GetString(1)), 1000, ct);
            if (rows.Count == 0) break;
            foreach (var row in rows) { if (!expected.TryGetValue(row.Id, out var proof)) throw new MigrationRejectedException(); manifest.Require(row.Proof(proof.Target, context, secrets)); seen++; }
            last = rows[^1].Id;
        }
        if (seen != count) throw new MigrationRejectedException(); return seen;
    }
    public async Task<long> VerifyWalletCoverageAsync(VerifiedImportManifest manifest, CancellationToken ct = default)
    {
        var expected = manifest.Proofs.Where(p => p.Projection == LegacyProjection.User).Select(p => p.LegacyId).ToHashSet(StringComparer.Ordinal);
        await using var session = await OpenAsync(ct);
        if (Convert.ToHexStringLower(await SchemaAsync(session, ct)) != manifest.SourceSchemaDigest) throw new MigrationRejectedException();
        long count = await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.users"), r => r.GetInt64(0), ct);
        if (count != expected.Count) throw new MigrationRejectedException();
        long last = 0, seen = 0;
        while (true)
        {
            var ids = await session.QueryManyAsync(new DbStatement("SELECT id FROM public.users WHERE id>$1 ORDER BY id LIMIT 1000", DbValue.Bigint(last)), r => r.GetInt64(0), 1000, ct);
            if (ids.Count == 0) break;
            foreach (long id in ids) { if (!expected.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture))) throw new MigrationRejectedException(); seen++; }
            last = ids[^1];
        }
        if (seen != count) throw new MigrationRejectedException(); return seen;
    }
}
public sealed record ProtectedLegacyField(string Field, string KeyId, ReadOnlyMemory<byte> Ciphertext)
{
    public override string ToString() => "ProtectedLegacyField [redacted]";
}
public sealed record ProtectedLegacyEvidence(Guid Target, Guid Brand, LegacyProjection Projection, string KeyId, ReadOnlyMemory<byte> Ciphertext, IReadOnlyList<ProtectedLegacyField>? ReencryptedFields = null)
{
    public override string ToString() => "ProtectedLegacyEvidence [redacted]";
}
public sealed class ArchivedLegacyRecord
{
    internal ArchivedLegacyRecord(LegacySourceRecord source, Guid target, string reference, ProtectedLegacyEvidence evidence)
    { Source = source; Target = target; Reference = reference; Evidence = evidence with { Ciphertext = evidence.Ciphertext.ToArray(), ReencryptedFields = Array.AsReadOnly((evidence.ReencryptedFields ?? []).Select(f => f with { Ciphertext = f.Ciphertext.ToArray() }).ToArray()) }; }
    public LegacySourceRecord Source { get; }
    public Guid Target { get; }
    public string Reference { get; }
    public ProtectedLegacyEvidence Evidence { get; }
    public byte[] Digest => LegacyRecordArchive.Digest(Evidence);
    public override string ToString() => "ArchivedLegacyRecord [redacted]";
}
public sealed class LegacyRecordArchive(SecretSnapshot secrets)
{
    public async Task<ArchivedLegacyRecord> WriteAsync(BrandContext context, VerifiedImportManifest manifest, LegacySourceRecord source, Guid target, Func<ProtectedLegacyEvidence, CancellationToken, Task<string>> archive, CancellationToken ct = default, IEnumerable<string>? legacyKeys = null)
    {
        if (context.BrandId != manifest.TargetBrand) throw new MigrationRejectedException(); manifest.Require(source.Proof(target, context, secrets));
        byte[] plain = Encoding.UTF8.GetBytes(source.Body);
        try
        {
            var cipher = new BrandCipher(secrets); string field = LegacySourceRecord.Table(source.Projection);
            var recovered = new List<ProtectedLegacyField>();
            foreach (var item in EncryptedFields(source))
            {
                if (legacyKeys is null) throw new MigrationRejectedException();
                byte[] original = Encoding.UTF8.GetBytes(LegacyFernetReader.Decrypt(item.Value, item.Kind, legacyKeys));
                byte[] roundtripField = [];
                try
                {
                    string location = FieldLocation(source, item.Field); byte[] encrypted = cipher.Encrypt(context, "legacy_id_map", target, location, original);
                    roundtripField = cipher.Decrypt(context, "legacy_id_map", target, location, secrets.CurrentBrandKeyId(context), encrypted);
                    if (!CryptographicOperations.FixedTimeEquals(original, roundtripField)) throw new MigrationRejectedException();
                    recovered.Add(new(item.Field, secrets.CurrentBrandKeyId(context), encrypted));
                }
                finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(roundtripField); }
            }
            var evidence = new ProtectedLegacyEvidence(target, context.BrandId, source.Projection, secrets.CurrentBrandKeyId(context), cipher.Encrypt(context, "legacy_id_map", target, field, plain), Array.AsReadOnly(recovered.ToArray()));
            byte[] roundtrip = cipher.Decrypt(context, "legacy_id_map", target, field, evidence.KeyId, evidence.Ciphertext.Span);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, roundtrip)) throw new MigrationRejectedException(); }
            finally { CryptographicOperations.ZeroMemory(roundtrip); }
            string reference = await archive(evidence, ct);
            if (!reference.StartsWith("protected:", StringComparison.Ordinal) || reference.Length is < 12 or > 1024 || reference.Any(char.IsControl)) throw new MigrationRejectedException();
            return new(source, target, reference, evidence);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public bool Matches(BrandContext context, ArchivedLegacyRecord row, ProtectedLegacyEvidence archived)
    {
        if (context.BrandId != row.Evidence.Brand || archived.Target != row.Target || archived.Brand != context.BrandId || archived.Projection != row.Source.Projection || archived.KeyId != row.Evidence.KeyId ||
            !CryptographicOperations.FixedTimeEquals(Digest(archived), row.Digest)) return false;
        byte[] expected = Encoding.UTF8.GetBytes(row.Source.Body), plain = [];
        try
        {
            plain = new BrandCipher(secrets).Decrypt(context, "legacy_id_map", row.Target, LegacySourceRecord.Table(row.Source.Projection), archived.KeyId, archived.Ciphertext.Span);
            if (!CryptographicOperations.FixedTimeEquals(expected, plain) || EncryptedFields(row.Source).Count != (archived.ReencryptedFields?.Count ?? 0)) return false;
            foreach (var field in archived.ReencryptedFields ?? [])
            {
                byte[] recovered = new BrandCipher(secrets).Decrypt(context, "legacy_id_map", row.Target, FieldLocation(row.Source, field.Field), field.KeyId, field.Ciphertext.Span);
                CryptographicOperations.ZeroMemory(recovered);
            }
            return true;
        }
        catch (Exception error) when (error is CryptographicException or CipherRejectedException) { return false; }
        finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(plain); }
    }
    internal static IReadOnlyList<(string Field, string Value, LegacyFieldKind Kind)> EncryptedFields(LegacySourceRecord source)
    {
        var field = source.Projection switch
        {
            LegacyProjection.InventoryUnit => ("data", LegacyFieldKind.Text), LegacyProjection.SupplierItem => ("content", LegacyFieldKind.Text),
            LegacyProjection.ServiceJob => ("submitted_data", LegacyFieldKind.Json), LegacyProjection.SupplierRefundArchive or LegacyProjection.PreorderRefundArchive => ("external_settlement_reference", LegacyFieldKind.Text),
            _ => ("", LegacyFieldKind.Text)
        };
        if (field.Item1.Length == 0) return [];
        using var document = source.Open(); string? value = LegacyValues.Optional(document.RootElement, field.Item1);
        return value is null ? [] : [(field.Item1, value, field.Item2)];
    }
    private static string FieldLocation(LegacySourceRecord source, string field) => "recovered_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Projection + ":" + source.Id + ":" + field)))[..16];
    internal static byte[] Digest(ProtectedLegacyEvidence evidence)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write("legacy-protected-record-v1"); writer.Write(evidence.Target.ToByteArray()); writer.Write(evidence.Brand.ToByteArray()); writer.Write((int)evidence.Projection); writer.Write(evidence.KeyId); writer.Write(evidence.Ciphertext.Length); writer.Write(evidence.Ciphertext.Span);
        var fields = evidence.ReencryptedFields ?? []; writer.Write(fields.Count);
        foreach (var field in fields) { writer.Write(field.Field); writer.Write(field.KeyId); writer.Write(field.Ciphertext.Length); writer.Write(field.Ciphertext.Span); }
        writer.Flush(); return SHA256.HashData(bytes.ToArray());
    }
}
