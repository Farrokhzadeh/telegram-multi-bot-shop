using Microsoft.Data.Sqlite;
using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class SqliteSessionTests
{
    [Fact]
    public async Task FileSessionsPreserveCatalogPaginationAndRollBackFailedWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            var sessions = new SqliteSessionFactory(path);
            await Assert.ThrowsAsync<SqliteException>(() => sessions.ListAdminProductsAsync(null, 1, ct));
            Assert.False(File.Exists(path));

            await using (var setup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await setup.OpenAsync(ct);
                await using var command = setup.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE products (id TEXT PRIMARY KEY, title TEXT NOT NULL, fulfillment_kind TEXT NOT NULL,
                        availability_policy TEXT NOT NULL, local_first INTEGER NOT NULL, active INTEGER NOT NULL, revision INTEGER NOT NULL);
                    CREATE TABLE listings (product_id TEXT NOT NULL REFERENCES products(id));
                    INSERT INTO products VALUES
                        ('00000000-0000-0000-0000-00000000000A','First','local','immediate',1,1,0),
                        ('FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF','Last','manual','immediate',0,0,7);
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }

            var first = Assert.Single(await sessions.ListAdminProductsAsync(null, 1, ct));
            var last = Assert.Single(await sessions.ListAdminProductsAsync(first.Id, 1, ct));
            Assert.Equal("First", first.Title);
            Assert.Equal(new AdminProduct(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), "Last", "manual", "immediate", false, false, 7), last);
            Assert.Empty(await sessions.ListAdminProductsAsync(last.Id, 1, ct));

            var forbidden = await Assert.ThrowsAsync<SqliteException>(() => Execute(false, "UPDATE products SET title='changed'"));
            Assert.Equal(8, forbidden.SqliteErrorCode);
            var rejected = await Assert.ThrowsAsync<SqliteException>(() => Execute(true, """
                UPDATE products SET title='changed';
                INSERT INTO listings VALUES ('absent');
                """));
            Assert.Equal(19, rejected.SqliteErrorCode);
            Assert.Equal(first, Assert.Single(await sessions.ListAdminProductsAsync(null, 1, ct)));

            Task<int> Execute(bool write, string sql) => sessions.RunAsync(write, async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                return await command.ExecuteNonQueryAsync(token);
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
