using Npgsql;
using Platform.Testing;

namespace Platform.AotSmoke;

internal static class NativeHostSmokeTests
{
    internal static void VerifyInventoryIsolation() => FoundationQualification.VerifyIndependentInventoryKeys();
    internal static Task VerifyDatabaseAsync(NpgsqlDataSource data, CancellationToken cancellationToken = default) =>
        FoundationQualification.VerifySchemaAsync(data, cancellationToken);
}
