using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
using Platform.Persistence;
using Platform.SchemaRunner;
namespace Platform.UnitTests;

public sealed class ReadOnlyTargetMetadataTests
{
    private const string Connection = "Host=127.0.0.1;Port=5432;Database=shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;Username=shop_test_runner;Password=SYNTHETIC-OWNER-SECRET;Enlist=false";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    [Fact]
    public void MatchingFrozenConnectionExportsOnlyOriginalDestinationMetadata()
    {
        string metadata = ReadOnlyTargetMetadata.Parse(Connection, Hash(Connection)); var manifest = SchemaManifest.Parse(metadata);
        Assert.Equal("shop_test_runner", manifest.Username); Assert.DoesNotContain("SYNTHETIC-OWNER-SECRET", metadata); Assert.DoesNotContain("Password", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(metadata, ReadOnlyTargetMetadata.Parse(" \n" + Connection + "\n", Hash(Connection)));
    }
    [Fact]
    public void ChangedConnectionDigestOrLiveIdentityIsRejected()
    {
        Assert.Throws<MigrationRejectedException>(() => ReadOnlyTargetMetadata.Parse(Connection + ";Timeout=7", Hash(Connection)));
        Assert.Throws<MigrationRejectedException>(() => ReadOnlyTargetMetadata.Parse(Connection, new string('0', 64)));
        Assert.Throws<MigrationRejectedException>(() => ReadOnlyTargetMetadata.Parse(Connection, "invalid"));
        string live = Connection.Replace("shop_qualification_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "accabad_prod1", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ReadOnlyTargetMetadata.Parse(live, Hash(live)));
    }
}
