using System.Data;
using System.Text.Json;
using Npgsql;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.SchemaRunner;

internal static class ReadOnlyTargetFingerprints
{
    internal static async Task VerifyAsync(string connection, string backup, string expectedJson, CancellationToken ct)
    {
        var endpoint = ReadOnlyReconciliationTarget.ValidateConnection(connection, backup, true); using var expected = JsonDocument.Parse(expectedJson); var root = expected.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 28 || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 28) throw new MigrationRejectedException();
        await using var data = new NpgsqlSlimDataSourceBuilder(connection).Build(); await using var opened = await data.OpenConnectionAsync(ct); await using var transaction = await opened.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL TIME ZONE 'UTC'; SET LOCAL statement_timeout='120s'", opened, transaction)) await setup.ExecuteNonQueryAsync(ct);
        await using (var identity = new NpgsqlCommand("SELECT current_database()=$1 AND current_user='teleg_target_reader' AND current_setting('server_version_num')::integer=180006 AND shobj_description(oid,'pg_database')=$2 FROM pg_database WHERE datname=current_database()", opened, transaction))
        {
            identity.Parameters.AddWithValue(endpoint.Database!); identity.Parameters.AddWithValue("teleg-t066-reconciliation-copy:" + backup.ToLowerInvariant()); if (await identity.ExecuteScalarAsync(ct) is not true) throw new MigrationRejectedException();
        }
        await ReadOnlyReconciliationRole.RequireAsync(opened, transaction, "teleg_target_reader", ct); _ = await Platform.Persistence.SchemaRunner.InspectReadOnlyAsync(opened, transaction, ct);
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.Length is < 1 or > 63 || entry.Name.Any(c => c is not (>= 'a' and <= 'z') and not '_') || entry.Value.ValueKind != JsonValueKind.String) throw new MigrationRejectedException();
            string sql = "SELECT count(*)::text||':'||encode(sha256(convert_to(coalesce(string_agg(encode(sha256(convert_to(row_to_json(t)::text,'UTF8')),'hex'),'' ORDER BY encode(sha256(convert_to(row_to_json(t)::text,'UTF8')),'hex')),''),'UTF8')),'hex') FROM shop." + entry.Name + " t";
            await using var command = new NpgsqlCommand(sql, opened, transaction) { CommandTimeout = 125 };
            if (await command.ExecuteScalarAsync(ct) is not string actual || actual != entry.Value.GetString()) throw new MigrationRejectedException();
        }
        await ReadOnlyReconciliationRole.RequireAsync(opened, transaction, "teleg_target_reader", ct);
    }
}
