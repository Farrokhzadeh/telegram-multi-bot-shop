using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
namespace Platform.SchemaRunner;

internal static class SourceCopyInspection
{
    internal static async Task RunAsync(string connection, string plan, string receipt, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SHOP_ALLOW_PROTECTED_SOURCE_INSPECTION") != "1" || !Path.IsPathFullyQualified(receipt) || File.Exists(receipt) || plan.Length > 1048576) throw new MigrationRejectedException();
        using var document = JsonDocument.Parse(plan); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 8 || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 8) throw new MigrationRejectedException();
        string host = root.GetProperty("socket").GetString() ?? throw new MigrationRejectedException(), database = root.GetProperty("database").GetString() ?? throw new MigrationRejectedException(), archive = root.GetProperty("archive_sha256").GetString() ?? throw new MigrationRejectedException();
        var options = new NpgsqlConnectionStringBuilder(connection);
        if (!Path.IsPathFullyQualified(host) || Path.GetFileName(host) != "socket" || Path.GetFileName(Path.GetDirectoryName(host))?.StartsWith("teleg-source-qualification-", StringComparison.Ordinal) != true ||
            options.Host != host || options.Port != 5432 || options.Database != database || database is not ("teleg_source_prod1" or "teleg_source_prod2" or "teleg_source_prod3") || options.Username != "teleg_source_reader" || options.IncludeErrorDetail || options.LogParameters || options.Multiplexing || options.Enlist ||
            archive.Length != 64 || !archive.All(char.IsAsciiHexDigit) || root.GetProperty("source_revision").GetString() != "66371a23a1c8149dc5bdf89a0355aa689d454dba") throw new MigrationRejectedException();
        byte[] schema = Convert.FromHexString(root.GetProperty("schema_sha256").GetString() ?? throw new MigrationRejectedException()), key = Convert.FromHexString(root.GetProperty("commitment_key").GetString() ?? throw new MigrationRejectedException());
        try
        {
            await using var data = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
            await using (var command = data.CreateCommand("SELECT current_database()=$1 AND current_user='teleg_source_reader' AND current_setting('server_version_num')::integer=$2 AND shobj_description(oid,'pg_database')=$3 FROM pg_database WHERE datname=current_database()"))
            {
                command.Parameters.AddWithValue(database); command.Parameters.AddWithValue(root.GetProperty("server_version").GetInt32()); command.Parameters.AddWithValue("teleg-offline-copy:" + archive);
                if (await command.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
            }
            var expected = root.GetProperty("tables").EnumerateArray().Select(t => new LegacySourceTableExpectation(t.GetProperty("table").GetString() ?? throw new MigrationRejectedException(), t.GetProperty("key").GetString() ?? throw new MigrationRejectedException(), t.GetProperty("count").GetInt64(), Convert.FromHexString(t.GetProperty("commitment").GetString() ?? throw new MigrationRejectedException()))).ToArray();
            var reader = new LegacySourceReader(data, new(host, 5432, database, "teleg_source_reader"));
            var counts = await reader.VerifyCopyAsync(schema, expected, key, ct);
            using var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject(); writer.WriteString("scope", "native_read_only_source_copy_validation"); writer.WriteString("archive_sha256", archive); writer.WriteString("schema_sha256", Convert.ToHexStringLower(schema)); writer.WriteNumber("server_version", root.GetProperty("server_version").GetInt32()); writer.WriteBoolean("release_ready", false); writer.WriteBoolean("source_unchanged", true); writer.WriteStartObject("table_rows");
                foreach (var row in counts) writer.WriteNumber(row.Key, row.Value);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
            await using var output = new FileStream(receipt, file); await output.WriteAsync(body.ToArray(), ct);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
