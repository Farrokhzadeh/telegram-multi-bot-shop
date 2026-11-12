using System.Security.Cryptography;
using Npgsql;
using Platform.Persistence;
namespace Platform.Migration;

public sealed class ReadOnlyReconciliationTarget
{
    private readonly byte[] manifestDigest;
    private readonly string reference, host, database, backup, catalog;
    private readonly int port;
    private const string Username = "teleg_target_reader";
    private ReadOnlyReconciliationTarget(VerifiedImportManifest manifest, NpgsqlConnectionStringBuilder endpoint, string backup, string catalog)
    { manifestDigest = manifest.Digest.ToArray(); reference = manifest.Reference; host = endpoint.Host!; port = endpoint.Port; database = endpoint.Database!; this.backup = backup; this.catalog = catalog; }
    public override string ToString() => "ReadOnlyReconciliationTarget [redacted]";
    public static async Task<ReadOnlyReconciliationTarget> VerifyAsync(NpgsqlDataSource data, VerifiedImportManifest manifest, string backupSha256, bool granted, CancellationToken ct = default)
    {
        manifest.Destination.Validate(); if (manifest.Destination.SqlSha256 != SchemaRunner.BaselineSqlSha256) throw new MigrationRejectedException();
        var endpoint = ValidateConnection(data.ConnectionString, backupSha256, granted);
        string backup = backupSha256.ToLowerInvariant();
        await using var session = await new DbSessionFactory(data).BeginReadOnlyAsync(ct);
        var receipt = await CheckAsync(session, endpoint.Database!, manifest.Destination.ServerVersion, backup, ct);
        return new(manifest, endpoint, backup, receipt.CatalogSha256);
    }
    public static NpgsqlConnectionStringBuilder ValidateConnection(string connection, string backupSha256, bool granted)
    {
        var endpoint = new NpgsqlConnectionStringBuilder(connection); const string prefix = "shop_qualification_";
        if (!granted || backupSha256.Length != 64 || !backupSha256.All(char.IsAsciiHexDigit) || endpoint.Host is not ("127.0.0.1" or "localhost" or "::1") || endpoint.Username != Username || endpoint.Port is < 1 or > 65535 || endpoint.IncludeErrorDetail || endpoint.LogParameters || endpoint.Enlist || endpoint.Multiplexing || endpoint.Database is null || endpoint.Database != "t066_reconcile" && (!endpoint.Database.StartsWith(prefix, StringComparison.Ordinal) || endpoint.Database.Length != prefix.Length + 32 || !endpoint.Database[prefix.Length..].All(char.IsAsciiHexDigit))) throw new MigrationRejectedException();
        return endpoint;
    }
    public async Task RequireAsync(DbSession session, VerifiedImportManifest manifest, CancellationToken ct)
    {
        if (manifest.Reference != reference || !CryptographicOperations.FixedTimeEquals(manifestDigest, manifest.Digest.Span) || !session.MatchesEndpoint(host, port, database, Username)) throw new MigrationRejectedException();
        var receipt = await CheckAsync(session, database, manifest.Destination.ServerVersion, backup, ct);
        if (receipt.CatalogSha256 != catalog) throw new MigrationRejectedException();
    }
    private static async Task<SchemaReceipt> CheckAsync(DbSession session, string database, int version, string backup, CancellationToken ct)
    {
        if (!await session.QuerySingleAsync(new DbStatement("SELECT current_database()=$1 AND current_user=$2 AND current_setting('server_version_num')::integer=$3 AND current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read' AND shobj_description(oid,'pg_database')=$4 AND has_schema_privilege(current_user,'shop','USAGE') AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND c.relkind='r' AND NOT has_table_privilege(current_user,c.oid,'SELECT')) FROM pg_database WHERE datname=current_database()", DbValue.Text(database), DbValue.Text(Username), DbValue.Integer(version), DbValue.Text("teleg-t066-reconciliation-copy:" + backup)), r => r.GetBoolean(0), ct)) throw new MigrationRejectedException();
        await ReadOnlyReconciliationRole.RequireAsync(session, Username, ct);
        return await session.InspectSchemaAsync(ct);
    }
}
