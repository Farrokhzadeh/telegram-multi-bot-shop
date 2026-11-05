using System.Security.Cryptography;
using Npgsql;
using Platform.Migration;
namespace Platform.Testing;

public static class SourceCopyQualification
{
    public static async Task RunAsync(string fault = "valid", CancellationToken ct = default)
    {
        await using var database = await DisposablePostgres.CreateAsync(ct);
        string role = "source_reader_" + Guid.NewGuid().ToString("N");
        await using (var setup = database.Data.CreateCommand($"CREATE ROLE {role} LOGIN PASSWORD 'public-synthetic-source'; CREATE TABLE public.users(id bigint PRIMARY KEY,value text); CREATE TABLE public.django_migrations(id bigint PRIMARY KEY,app text,name text); INSERT INTO public.users VALUES(1,'PRIVATE'); INSERT INTO public.django_migrations VALUES(1,'test','initial'); GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role}")) await setup.ExecuteNonQueryAsync(ct);
        try
        {
            var options = new NpgsqlConnectionStringBuilder(database.Data.ConnectionString) { Username = role, Password = "public-synthetic-source" };
            await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
            var reader = new LegacySourceReader(data, new(options.Host!, options.Port, options.Database!, options.Username!));
            byte[] schema = await reader.InspectSchemaAsync(ct), key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
            LegacySourceTableExpectation[] expected = [new("users", "id", 1, Convert.FromHexString("3446cc39207eca1a5bcadcc485d59df758cdf14faf8ca91736265fa150b07fe2")), new("django_migrations", "id", 1, Convert.FromHexString("8c64a57f3f26ee17b38602909a8e2ec9c378f4542f3f6bd4545e092986533563"))];
            string? sql = fault switch { "row" => "UPDATE public.users SET value='CHANGED'", "missing" => "DELETE FROM public.users", "extra" => "INSERT INTO public.users VALUES(2,'EXTRA')", "schema" => "ALTER TABLE public.users ADD COLUMN drift text", "index" => "CREATE INDEX source_drift ON public.users(value)", "privilege" => $"GRANT INSERT ON public.users TO {role}", _ => null };
            if (sql is not null) { await using var change = database.Data.CreateCommand(sql); await change.ExecuteNonQueryAsync(ct); }
            if (fault == "table") expected = expected[..1];
            if (fault == "key") expected[0] = expected[0] with { Key = "value" };
            if (fault == "count") expected[0] = expected[0] with { Count = 0 };
            if (fault == "commitment") expected[0] = expected[0] with { Commitment = new byte[32] };
            if (fault == "role") reader = new(database.Data, new(options.Host!, options.Port, options.Database!, new NpgsqlConnectionStringBuilder(database.Data.ConnectionString).Username!));
            bool rejected = false;
            try
            {
                var result = await reader.VerifyCopyAsync(schema, expected, key, ct);
                if (result.Count != 2 || result["users"] != 1 || result["django_migrations"] != 1) throw new InvalidOperationException("Source copy count mismatch.");
                var again = await reader.VerifyCopyAsync(schema, expected, key, ct);
                if (again.Count != result.Count) throw new InvalidOperationException("Source inspection replay mismatch.");
            }
            catch (MigrationRejectedException) { rejected = true; }
            if (rejected != (fault != "valid")) throw new InvalidOperationException("Source copy fault was not rejected as expected.");
            if (fault == "valid")
            {
                await using var write = data.CreateCommand("UPDATE public.users SET value='FORBIDDEN'");
                try { await write.ExecuteNonQueryAsync(ct); throw new InvalidOperationException("Source role acquired write access."); }
                catch (PostgresException error) when (error.SqlState == "42501") { }
            }
        }
        finally
        {
            await using var cleanup = database.Data.CreateCommand($"DROP OWNED BY {role}; DROP ROLE {role}"); await cleanup.ExecuteNonQueryAsync(ct);
        }
    }
}
