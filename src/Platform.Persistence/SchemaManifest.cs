using System.Globalization;
using Npgsql;

namespace Platform.Persistence;

public sealed record SchemaManifest(string Environment, string Revision, string SqlSha256,
    string Host, int Port, string Database, string Username, int ServerVersion)
{
    public static SchemaManifest Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 1)..]))
            {
                throw new InvalidOperationException("Invalid or duplicate schema manifest field.");
            }
        }

        string[] required = ["environment", "revision", "sql_sha256", "host", "port", "database", "username", "server_version"];
        if (fields.Count != required.Length || required.Any(key => !fields.ContainsKey(key)) ||
            !int.TryParse(fields["port"], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            !int.TryParse(fields["server_version"], NumberStyles.None, CultureInfo.InvariantCulture, out int serverVersion))
        {
            throw new InvalidOperationException("Incomplete schema manifest.");
        }

        var manifest = new SchemaManifest(fields["environment"], fields["revision"], fields["sql_sha256"],
            fields["host"], port, fields["database"], fields["username"], serverVersion);
        manifest.Validate();
        return manifest;
    }

    public static SchemaManifest ForQualification(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString);
        var manifest = new SchemaManifest("qualification", "0001", SchemaRunner.BaselineSqlSha256,
            options.Host ?? "", options.Port, options.Database ?? "", options.Username ?? "", 180006);
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        const string prefix = "shop_qualification_";
        if (Environment != "qualification" || Revision != "0001" || ServerVersion != 180006 ||
            Host is not ("127.0.0.1" or "localhost" or "::1") || Port is < 1 or > 65535 ||
            Username != "shop_test_runner" || !Database.StartsWith(prefix, StringComparison.Ordinal) ||
            Database.Length != prefix.Length + 32 || !Database[prefix.Length..].All(char.IsAsciiHexDigit) ||
            SqlSha256.Length != 64 || !SqlSha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("Only an exact disposable qualification identity is supported.");
        }
    }

    public string ToFileText() => string.Join('\n',
        "environment=" + Environment, "revision=" + Revision, "sql_sha256=" + SqlSha256,
        "host=" + Host, "port=" + Port.ToString(CultureInfo.InvariantCulture), "database=" + Database,
        "username=" + Username, "server_version=" + ServerVersion.ToString(CultureInfo.InvariantCulture)) + "\n";
}
