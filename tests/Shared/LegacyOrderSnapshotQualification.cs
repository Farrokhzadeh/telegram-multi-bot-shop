using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
namespace Platform.Testing;

public static class LegacyOrderSnapshotQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        await using var world = await LegacyRecordFixture.CreateAsync("wallet", ct); var f = world.Fixture;
        await using (var setup = f.Database.Data.CreateCommand("CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text); CREATE TABLE public.users(id bigint PRIMARY KEY); INSERT INTO public.users VALUES(99001234); CREATE TABLE public.products(id bigint PRIMARY KEY); INSERT INTO public.products VALUES(456); CREATE TABLE public.orders(id text PRIMARY KEY,user_id bigint,product_id bigint,qty integer,total numeric,currency text,created_at timestamptz,checkout_snapshot jsonb)")) await setup.ExecuteNonQueryAsync(ct);
        await using (var insert = f.Database.Data.CreateCommand("INSERT INTO public.orders SELECT v->>'id',(v->>'user_id')::bigint,(v->>'product_id')::bigint,(v->>'qty')::integer,(v->>'total')::numeric,v->>'currency',(v->>'created_at')::timestamptz,v->'checkout_snapshot' FROM (SELECT $1::jsonb AS v) q"))
        { insert.Parameters.AddWithValue(world.Set.Orders[0].Record.Source.Body); await insert.ExecuteNonQueryAsync(ct); }
        await using (var more = f.Database.Data.CreateCommand("INSERT INTO public.orders SELECT 'ORD-000000000001',user_id,product_id,qty,total,currency,created_at,'{}'::jsonb FROM public.orders LIMIT 1; INSERT INTO public.orders SELECT 'ORD-000000000002',user_id,product_id,qty,total,currency,created_at,jsonb_set(checkout_snapshot,'{quantity}','99') FROM public.orders WHERE id='ORD-ABC123ABC123'")) await more.ExecuteNonQueryAsync(ct);
        var options = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString); var reader = new LegacySourceReader(f.Database.Data, new(options.Host!, options.Port, options.Database!, options.Username!)); byte[] schema = await reader.InspectSchemaAsync(ct);
        if (fault == "schema")
        {
            await using var drift = f.Database.Data.CreateCommand("CREATE INDEX source_order_extra ON public.orders(user_id)"); await drift.ExecuteNonQueryAsync(ct);
            try { await reader.InspectOrderSnapshotsAsync(schema, ct); throw new InvalidOperationException("Changed source schema accepted."); } catch (MigrationRejectedException) { return; }
        }
        if (fault == "lines")
        {
            await using var lines = f.Database.Data.CreateCommand("CREATE TABLE public.order_items(id bigint PRIMARY KEY)"); await lines.ExecuteNonQueryAsync(ct); schema = await reader.InspectSchemaAsync(ct);
            try { await reader.InspectOrderSnapshotsAsync(schema, ct); throw new InvalidOperationException("Unexpected source order lines accepted."); } catch (MigrationRejectedException) { return; }
        }
        if (fault == "links")
        {
            await using var links = f.Database.Data.CreateCommand("UPDATE public.orders SET user_id=99009999,product_id=999 WHERE id='ORD-ABC123ABC123'"); await links.ExecuteNonQueryAsync(ct);
        }
        byte[] key = Enumerable.Range(0, 32).Select(v => (byte)v).ToArray(); using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        void Append(string value) { byte[] data = Encoding.UTF8.GetBytes(value); writer.Write(data.Length); writer.Write(data); }
        Append("accabad-source-table-v1"); Append("orders"); Append("id");
        await using (var rows = f.Database.Data.CreateCommand("SELECT id::text,to_jsonb(s)::text FROM public.orders s ORDER BY id::text COLLATE \"C\""))
        { await using var values = await rows.ExecuteReaderAsync(ct); while (await values.ReadAsync(ct)) { Append(values.GetString(0)); Append(values.GetString(1)); } }
        writer.Flush(); var expected = new LegacySourceTableExpectation("orders", "id", 3, HMACSHA256.HashData(key, bytes.ToArray()));
        if (fault == "commitment") expected = expected with { Commitment = new byte[32] };
        if (fault == "count") expected = expected with { Count = 2 };
        if (fault == "data") { await using var change = f.Database.Data.CreateCommand("UPDATE public.orders SET total=total+1 WHERE id='ORD-000000000001'"); await change.ExecuteNonQueryAsync(ct); }
        if (fault is "commitment" or "count" or "data")
        { try { await reader.InspectOrderSnapshotsAsync(schema, ct, expected, key); throw new InvalidOperationException("Changed source commitment accepted."); } catch (MigrationRejectedException) { return; } }
        var report = await reader.InspectOrderSnapshotsAsync(schema, ct, expected, key);
        if (report != await reader.InspectOrderSnapshotsAsync(schema, ct, expected, key) || report.Orders != 3 || report.EmptySnapshots != 1 || report.RejectedTerms != (fault == "links" ? 2 : 1) || report.AcceptedTerms != (fault == "links" ? 0 : 1) || report.MissingMembers != (fault == "links" ? 1 : 0) || report.MissingProducts != (fault == "links" ? 1 : 0) || report.ImportApproved || report.ReleaseReady) throw new InvalidOperationException("Source order snapshot classification failed.");
    }
}
