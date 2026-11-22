using Npgsql;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;

namespace Platform.SchemaRunner;

public sealed record EfPostgresQualificationReceipt(string Database, string Migration, string ScriptSha256);

public static partial class EfSchemaCommand
{
    public static async Task<EfPostgresQualificationReceipt> CreatePostgresQualificationAsync(NpgsqlDataSource data, string expectedScriptSha256, CancellationToken ct = default)
    {
        RequireQualification();
        var description = Describe("postgres");
        if (expectedScriptSha256.Length != 64 || !expectedScriptSha256.All(char.IsAsciiHexDigit) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedScriptSha256), Convert.FromHexString(description.ScriptSha256)))
            throw new InvalidOperationException("The reviewed PostgreSQL EF script digest differs.");
        var identity = SchemaManifest.ForQualification(data.ConnectionString);
        var options = new NpgsqlConnectionStringBuilder(data.ConnectionString);
        if (options.IncludeErrorDetail || options.Multiplexing) throw new UnauthorizedAccessException("Qualification requires a dedicated redacted connection.");
        await using var connection = await data.OpenConnectionAsync(ct);
        await using (var check = connection.CreateCommand())
        {
            check.CommandTimeout = 5;
            check.CommandText = """
                SELECT current_database(),current_user,current_setting('server_version_num')::integer,
                    (SELECT pg_get_userbyid(datdba)=current_user FROM pg_database WHERE datname=current_database()),
                    (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                     WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
                        AND n.nspname NOT LIKE 'pg_temp_%' AND c.relkind IN ('r','p','v','m','f','S')),
                    (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                     WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_temp_%'),
                    (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
                     WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
                        AND n.nspname NOT LIKE 'pg_temp_%' AND t.typtype IN ('d','e','c'))
                """;
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != identity.Database || reader.GetString(1) != identity.Username ||
                reader.GetInt32(2) != identity.ServerVersion || !reader.GetBoolean(3) || reader.GetInt64(4) != 0 ||
                reader.GetInt64(5) != 0 || reader.GetInt64(6) != 0)
                throw new UnauthorizedAccessException("Only an owned empty synthetic qualification database is accepted.");
        }
        await using var context = new PostgresAkantiDbContext(new DbContextOptionsBuilder<PostgresAkantiDbContext>()
            .UseNpgsql(connection, provider => provider.CommandTimeout(30)).Options);
        await context.Database.MigrateAsync(ct);
        if ((await context.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("The PostgreSQL schema is incomplete.");
        return new(identity.Database, description.Migration, description.ScriptSha256);
    }
}
