using System.Security.Cryptography;
using System.Text;
using Platform.Persistence;
namespace Platform.Migration;

public sealed record LegacySourceTableExpectation(string Table, string Key, long Count, ReadOnlyMemory<byte> Commitment);
public sealed partial class LegacySourceReader
{
    public async Task<IReadOnlyDictionary<string, long>> VerifyCopyAsync(ReadOnlyMemory<byte> schema, IReadOnlyList<LegacySourceTableExpectation> expected, ReadOnlyMemory<byte> commitmentKey, CancellationToken ct = default, Func<string, string, string, CancellationToken, ValueTask>? visit = null)
    {
        if (schema.Length != 32 || commitmentKey.Length != 32 || expected.Count is < 1 or > 1000 || expected.Select(x => x.Table).Distinct(StringComparer.Ordinal).Count() != expected.Count || expected.Any(x => !Identifier(x.Table) || !Identifier(x.Key) || x.Count is < 0 or > 1000000 || x.Commitment.Length != 32)) throw new MigrationRejectedException();
        expected = expected.Select(x => x with { Commitment = x.Commitment.ToArray() }).ToArray();
        schema = schema.ToArray(); commitmentKey = commitmentKey.ToArray();
        await using var session = await OpenAsync(ct);
        byte[] actualSchema = await SchemaAsync(session, ct);
        if (!CryptographicOperations.FixedTimeEquals(schema.Span, actualSchema) ||
            !await session.QuerySingleAsync(new DbStatement("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        var tables = await session.QueryManyAsync(new DbStatement("SELECT c.relname,a.attname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_index i ON i.indrelid=c.oid AND i.indisprimary AND i.indnkeyatts=1 LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=i.indkey[0] WHERE n.nspname='public' AND c.relkind='r' ORDER BY c.relname"), r => (Table: r.GetString(0), Key: r.IsDBNull(1) ? "" : r.GetString(1)), 1000, ct);
        if (tables.Count != expected.Count || tables.Any(t => !expected.Any(x => x.Table == t.Table && x.Key == t.Key)) ||
            await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND NOT k.convalidated) OR EXISTS(SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND (NOT i.indisvalid OR NOT i.indisready)) OR EXISTS(SELECT 1 FROM pg_tables WHERE schemaname='public' AND tableowner=current_user)"), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in expected.OrderBy(x => x.Table, StringComparer.Ordinal))
        {
            if (await session.QuerySingleAsync(new DbStatement("SELECT has_table_privilege(current_user,$1,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')", DbValue.Text("public." + table.Table)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, commitmentKey.Span);
            Append(hash, "accabad-source-table-v1"); Append(hash, table.Table); Append(hash, table.Key);
            string last = ""; long seen = 0;
            while (true)
            {
                var rows = await session.QueryManyAsync(new DbStatement("SELECT s." + table.Key + "::text,to_jsonb(s)::text FROM public." + table.Table + " s WHERE s." + table.Key + "::text COLLATE \"C\" > $1 COLLATE \"C\" ORDER BY s." + table.Key + "::text COLLATE \"C\" LIMIT 1000", DbValue.Text(last)), r => (Key: r.GetString(0), Body: r.GetString(1)), 1000, ct);
                if (rows.Count == 0) break;
                foreach (var row in rows)
                {
                    if (row.Key.Length > 1024 || row.Body.Length > 1048576 || ++seen > table.Count) throw new MigrationRejectedException();
                    Append(hash, row.Key); Append(hash, row.Body);
                    if (visit is not null) await visit(table.Table, row.Key, row.Body, ct);
                }
                last = rows[^1].Key;
            }
            if (seen != table.Count || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), table.Commitment.Span)) throw new MigrationRejectedException();
            result.Add(table.Table, seen);
        }
        return result;
    }
    private static bool Identifier(string text) => !string.IsNullOrEmpty(text) && text.Length <= 63 && text[0] is >= 'a' and <= 'z' && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static void Append(IncrementalHash hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try { Span<byte> size = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length); hash.AppendData(size); hash.AppendData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
