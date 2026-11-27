using Microsoft.Data.Sqlite;
using Platform.Persistence;

namespace Platform.IntegrationTests;

public sealed class SqliteSessionTests
{
    [Fact]
    public async Task SchemaInitializationCreatesAnEmptyDatabaseAndRefusesAnExistingFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-schema-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            await Assert.ThrowsAsync<IOException>(() => SqliteSchema.InitializePlaintextAsync(path, ct));

            var sessions = SqliteSessionFactory.ForPlaintext(path);
            Assert.Empty(await sessions.ListAdminProductsAsync(null, 10, ct));
            await sessions.RunAsync(false, async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table'";
                Assert.Equal(28L, await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT count(*) FROM brands";
                Assert.Equal(0L, await command.ExecuteScalarAsync(token));
                command.CommandText = "PRAGMA foreign_key_check";
                await using var violations = await command.ExecuteReaderAsync(token);
                Assert.False(await violations.ReadAsync(token));
                return true;
            }, ct);
            const string exact = "12345678901234567890.12345678";
            await sessions.RunAsync(true, async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,
                        bot_token,current_fx,fx_source,fx_fetched_at)
                    VALUES ('00000000-0000-0000-0000-000000000001','fixture','Fixture','Fixture','international','en',
                        'fixture_bot',1,x'00',$amount,'fixture','2026-11-21T00:00:00.0000000Z');
                    """;
                command.Parameters.AddWithValue("$amount", exact);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
                command.CommandText = "SELECT current_fx FROM brands";
                Assert.Equal(exact, await command.ExecuteScalarAsync(token));
                command.CommandText = "UPDATE brands SET current_fx='0.00000000000000000000000000001'";
                var rejected = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(token));
                Assert.Equal(19, rejected.SqliteErrorCode);
                return true;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task FileSessionsPreserveCatalogPaginationAndRollBackFailedWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-");
        try
        {
            var path = Path.Combine(directory.FullName, "shop.db");
            var sessions = SqliteSessionFactory.ForPlaintext(path);
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

    [Fact]
    public async Task CancelledWriteRollsBackAndImmediateWriterContentionIsBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-session-recovery-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path, timeoutSeconds: 1);
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await sessions.RunAsync(true, async (_, tx, token) =>
                    {
                        using var insert = SqliteWallet.Command(tx, """
                            INSERT INTO brands(id,code,internal_name,public_name,market,default_language,
                                bot_username,bot_id,bot_token)
                            VALUES ($id,'cancelled','Cancelled','Cancelled','international','en',
                                'cancelled_bot',1234,x'01')
                            """, ("$id", Guid.NewGuid()));
                        Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
                        cancelled.Cancel();
                        return 0;
                    }, cancelled.Token);
                });
            }
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, "SELECT count(*) FROM brands");
                Assert.Equal(0L, await query.ExecuteScalarAsync(token));
                return 0;
            }, ct);
            await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            await writer.OpenAsync(ct);
            await using (var hold = writer.BeginTransaction(deferred: false))
            {
                var error = await Assert.ThrowsAsync<SqliteException>(async () =>
                {
                    await Task.Run(() => sessions.RunAsync(true, (_, _, _) => Task.FromResult(0), ct), ct);
                });
                Assert.Equal(5, error.SqliteErrorCode);
                await hold.RollbackAsync(ct);
            }
            await sessions.RunAsync(true, async (_, tx, token) =>
            {
                using var insert = SqliteWallet.Command(tx, """
                    INSERT INTO brands(id,code,internal_name,public_name,market,default_language,
                        bot_username,bot_id,bot_token)
                    VALUES ($id,'recovered','Recovered','Recovered','international','en',
                        'recovered_bot',1234,x'01')
                    """, ("$id", Guid.NewGuid()));
                return await insert.ExecuteNonQueryAsync(token);
            }, ct);
            await sessions.RunAsync(false, async (_, tx, token) =>
            {
                using var query = SqliteWallet.Command(tx, "SELECT count(*) FROM brands");
                Assert.Equal(1L, await query.ExecuteScalarAsync(token));
                return 0;
            }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ConsistentBackupIncludesCommittedWalWritesWithoutDroppingSourceOrConstraints()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-sqlite-wal-backup-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            string backup = Path.Combine(directory.FullName, "restored.db");
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            var sessions = SqliteSessionFactory.ForPlaintext(path);
            async Task InsertAsync(string code, long bot)
            {
                await sessions.RunAsync(true, async (_, tx, token) =>
                {
                    using var insert = SqliteWallet.Command(tx, """
                        INSERT INTO brands(id,code,internal_name,public_name,market,default_language,
                            bot_username,bot_id,bot_token)
                        VALUES ($id,$code,$code,$code,'international','en',$username,$bot,x'01')
                        """, ("$id", Guid.NewGuid()), ("$code", code),
                        ("$username", code + "_bot"), ("$bot", bot));
                    return await insert.ExecuteNonQueryAsync(token);
                }, ct);
            }
            await InsertAsync("first", 1001);
            var settings = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
            await using (var hold = new SqliteConnection(settings))
            {
                await hold.OpenAsync(ct);
                await using var reader = hold.BeginTransaction(deferred: true);
                using (var query = hold.CreateCommand())
                {
                    query.Transaction = reader;
                    query.CommandText = "SELECT count(*) FROM brands";
                    Assert.Equal(1L, await query.ExecuteScalarAsync(ct));
                }
                await InsertAsync("second", 1002);
                Assert.True(File.Exists(path + "-wal"));
                Assert.True(new FileInfo(path + "-wal").Length > 0);
                await using (var source = new SqliteConnection(settings))
                await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = backup,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
                {
                    await source.OpenAsync(ct);
                    await destination.OpenAsync(ct);
                    source.BackupDatabase(destination);
                }
                await reader.RollbackAsync(ct);
            }
            foreach (string database in new[] { path, backup })
                await SqliteSessionFactory.ForPlaintext(database).RunAsync(false, async (_, tx, token) =>
                {
                    using var check = SqliteWallet.Command(tx, """
                        SELECT count(*),sum(bot_id) FROM brands
                        WHERE code IN ('first','second')
                        """);
                    await using (var row = await check.ExecuteReaderAsync(token))
                    {
                        Assert.True(await row.ReadAsync(token));
                        Assert.Equal(2L, row.GetInt64(0));
                        Assert.Equal(2003L, row.GetInt64(1));
                    }
                    using var integrity = SqliteWallet.Command(tx, "PRAGMA integrity_check");
                    Assert.Equal("ok", await integrity.ExecuteScalarAsync(token));
                    using var constraints = SqliteWallet.Command(tx, "PRAGMA foreign_key_check");
                    await using var violations = await constraints.ExecuteReaderAsync(token);
                    Assert.False(await violations.ReadAsync(token));
                    return 0;
                }, ct);
        }
        finally { directory.Delete(recursive: true); }
    }
}
