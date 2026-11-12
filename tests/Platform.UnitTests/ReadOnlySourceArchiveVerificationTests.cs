using Npgsql;
using Platform.Migration;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ReadOnlySourceArchiveVerificationTests
{
    private const string Host = "/root/teleg-source-qualification-example/socket";
    private const string Database = "teleg_source_prod1";
    private const string Revision = "66371a23a1c8149dc5bdf89a0355aa689d454dba";
    private static readonly string Archive = new('a', 64);
    private static NpgsqlConnectionStringBuilder Connection() => new() { Host = Host, Database = Database, Username = "teleg_source_reader", Port = 5432, Enlist = false };
    private static LegacySourceEndpoint Validate(NpgsqlConnectionStringBuilder value, string host = Host, string database = Database, string? archive = null, string revision = Revision, int version = 160006, bool granted = true) => ReadOnlySourceArchiveVerification.ValidateEndpoint(value.ConnectionString, host, database, archive ?? Archive, revision, version, granted);
    [Fact]
    public void ExplicitGrantAndExactFrozenSocketAreRequired()
    {
        var value = Connection(); var endpoint = Validate(value); Assert.Equal(Host, endpoint.Host); Assert.Equal(Database, endpoint.Database); Assert.Equal("teleg_source_reader", endpoint.Username);
        Assert.Throws<MigrationRejectedException>(() => Validate(value, granted: false));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, host: "127.0.0.1"));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, host: "/tmp/other/socket"));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, host: "/root/teleg-source-qualification-example/other"));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, database: "accabad_prod1"));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, archive: "not_a_hash"));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, revision: new string('0', 40)));
        Assert.Throws<MigrationRejectedException>(() => Validate(value, version: 180006));
    }
    [Fact]
    public void ConnectionCannotRedirectOrEnableDiagnosticDisclosure()
    {
        var cases = new NpgsqlConnectionStringBuilder[]
        {
            new(Connection().ConnectionString){Host="/root/teleg-source-qualification-other/socket"},
            new(Connection().ConnectionString){Database="teleg_source_prod2"},
            new(Connection().ConnectionString){Username="postgres"},
            new(Connection().ConnectionString){Port=5433},
            new(Connection().ConnectionString){IncludeErrorDetail=true},
            new(Connection().ConnectionString){LogParameters=true},
            new(Connection().ConnectionString){Enlist=true},
            new(Connection().ConnectionString){Multiplexing=true}
        };
        foreach (var value in cases) Assert.Throws<MigrationRejectedException>(() => Validate(value));
    }
}
