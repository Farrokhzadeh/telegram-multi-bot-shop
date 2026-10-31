using Platform.Persistence;

namespace Platform.UnitTests;

public sealed class SchemaManifestTests
{
    private const string Valid = """
        environment=qualification
        revision=0001
        sql_sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
        host=127.0.0.1
        port=5432
        database=shop_qualification_00000000000000000000000000000001
        username=shop_test_runner
        server_version=180006
        """;

    [Fact]
    public void ExactManifestRoundtripsWithoutConnectionSecrets()
    {
        SchemaManifest parsed = SchemaManifest.Parse(Valid);
        Assert.Equal(parsed, SchemaManifest.Parse(parsed.ToFileText()));
        Assert.DoesNotContain("Password", parsed.ToFileText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("environment=qualification", "environment=production")]
    [InlineData("revision=0001", "revision=0002")]
    [InlineData("host=127.0.0.1", "host=production.internal")]
    [InlineData("port=5432", "port=0")]
    [InlineData("username=shop_test_runner", "username=postgres")]
    [InlineData("server_version=180006", "server_version=180005")]
    [InlineData("shop_qualification_00000000000000000000000000000001", "shop_production")]
    [InlineData("sql_sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sql_sha256=invalid")]
    public void OtherIdentityOrVersionCannotBeUsed(string original, string changed)
    {
        Assert.Throws<InvalidOperationException>(() => SchemaManifest.Parse(Valid.Replace(original, changed, StringComparison.Ordinal)));
    }

    [Fact]
    public void DuplicateAndUnknownManifestFieldsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaManifest.Parse(Valid + "\nrevision=0001"));
        Assert.Throws<InvalidOperationException>(() => SchemaManifest.Parse(Valid + "\nunknown=ignored"));
    }
}
