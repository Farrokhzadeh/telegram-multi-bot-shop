using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PostgresQualificationTests
{
    [Fact]
    public async Task QualificationRunsAgainstThePinnedPostgresMajorAndMinor()
    {
        await using var database = await DisposablePostgres.CreateAsync(TestContext.Current.CancellationToken);
        await using var command = database.Data.CreateCommand("SHOW server_version_num");
        Assert.Equal("180006", (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
