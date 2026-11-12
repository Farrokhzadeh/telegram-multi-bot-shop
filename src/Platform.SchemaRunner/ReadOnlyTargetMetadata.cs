using System.Security.Cryptography;
using System.Text;
using Platform.Migration;
using Platform.Persistence;
namespace Platform.SchemaRunner;

internal static class ReadOnlyTargetMetadata
{
    internal static string Parse(string connection, string expectedHash)
    {
        connection = connection.Trim();
        if (connection.Length is < 1 or > 16384 || expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigit)) throw new MigrationRejectedException();
        byte[] bytes = Encoding.UTF8.GetBytes(connection);
        try { if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash))) throw new MigrationRejectedException(); return SchemaManifest.ForQualification(connection).ToFileText(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
