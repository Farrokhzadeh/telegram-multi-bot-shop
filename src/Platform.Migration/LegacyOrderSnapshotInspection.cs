using System.Security.Cryptography;
using System.Text.Json;
using Platform.Persistence;
namespace Platform.Migration;

public sealed record LegacyOrderSnapshotAssessment(long Orders, long EmptySnapshots, long AcceptedTerms, long RejectedTerms, long MissingMembers, long MissingProducts)
{
    public bool ImportApproved => false;
    public bool ReleaseReady => false;
    public string Scope => "source_order_terms_only_not_payment_or_delivery_reconciliation";
}
public sealed partial class LegacySourceReader
{
    public async Task<LegacyOrderSnapshotAssessment> InspectOrderSnapshotsAsync(ReadOnlyMemory<byte> expectedSchema, CancellationToken ct = default)
    {
        if (expectedSchema.Length != 32) throw new MigrationRejectedException();
        await using var session = await OpenAsync(ct);
        byte[] schema = await SchemaAsync(session, ct);
        if (!CryptographicOperations.FixedTimeEquals(expectedSchema.Span, schema) || await session.QuerySingleAsync(new DbStatement("SELECT to_regclass('public.order_items') IS NOT NULL"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        string last = ""; long seen = 0, empty = 0, accepted = 0, rejected = 0, missingMembers = 0, missingProducts = 0;
        while (true)
        {
            var rows = await session.QueryManyAsync(new DbStatement("SELECT s.id::text,to_jsonb(s)::text,EXISTS(SELECT 1 FROM public.users u WHERE u.id=s.user_id),EXISTS(SELECT 1 FROM public.products p WHERE p.id=s.product_id) FROM public.orders s WHERE s.id::text COLLATE \"C\" > $1 COLLATE \"C\" ORDER BY s.id::text COLLATE \"C\" LIMIT 1000", DbValue.Text(last)), r => (Id: r.GetString(0), Body: r.GetString(1), Member: r.GetBoolean(2), Product: r.GetBoolean(3)), 1000, ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (++seen > 1000000) throw new MigrationRejectedException();
                if (!row.Member) missingMembers++; if (!row.Product) missingProducts++;
                var source = new LegacySourceRecord(LegacyProjection.OrderRecord, row.Id, row.Body);
                using var document = source.Open();
                if (document.RootElement.TryGetProperty("checkout_snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object && !snapshot.EnumerateObject().Any()) { empty++; continue; }
                try { _ = LegacyHistoricalRecord.ReadTerms(source); accepted++; }
                catch (MigrationRejectedException) { rejected++; }
            }
            last = rows[^1].Id;
        }
        if (seen != await session.QuerySingleAsync(new DbStatement("SELECT count(*) FROM public.orders"), r => r.GetInt64(0), ct)) throw new MigrationRejectedException();
        return new(seen, empty, accepted, rejected, missingMembers, missingProducts);
    }
}
