using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class SqliteStagingCopyTests
{
    [Fact]
    public async Task SyntheticPostgresSnapshotCopiesEveryTableToPersistentSqliteWithExactLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CheckoutFixture.CreateAsync(users: 3, stock: 2, ct: ct);
        var directory = Directory.CreateTempSubdirectory("akanti-copy-");
        try
        {
            var path = Path.Combine(directory.FullName, "staging.db");
            var result = await SqliteStagingCopy.CopyAsync(fixture.Database.Data, path, ct);
            Assert.Equal(28, result.Tables);
            Assert.True(result.Rows > 0);
            Assert.True(File.Exists(path));
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, """
                    SELECT (SELECT count(*) FROM brands),(SELECT count(*) FROM users),
                        (SELECT count(*) FROM inventory_items),(SELECT count(*) FROM wallet_entries),
                        (SELECT count(*) FROM wallets WHERE balance='100' AND version=1)
                    """);
                await using var row = await query.ExecuteReaderAsync(token);
                Assert.True(await row.ReadAsync(token));
                Assert.Equal(3, row.GetInt64(0));
                Assert.Equal(3, row.GetInt64(1));
                Assert.Equal(2, row.GetInt64(2));
                Assert.Equal(3, row.GetInt64(3));
                Assert.Equal(3, row.GetInt64(4));
                return true;
            }, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteStagingCopy.CopyAsync(fixture.Database.Data, path, ct));
            var again = SqliteSessionFactory.ForPlaintext(path);
            await again.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, "SELECT count(*) FROM wallet_entries WHERE is_posting=1");
                Assert.Equal(3L, (long)(await query.ExecuteScalarAsync(token))!);
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
