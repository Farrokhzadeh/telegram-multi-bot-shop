using NpgsqlTypes;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PostgresQualificationTests
{
    [Fact]
    public async Task ExactDecimalAndRollbackWorkInAnOwnedDisposableDatabase()
    {
        await using var database = await DisposablePostgres.CreateAsync(TestContext.Current.CancellationToken);
        await using var connection = await database.Data.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE qualification_amounts (id integer PRIMARY KEY, amount numeric NOT NULL)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        const decimal amount = 123456789012.123456789012m;
        await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO qualification_amounts VALUES (1, @amount) RETURNING amount";
            insert.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
            Assert.Equal(amount, (decimal)(await insert.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM qualification_amounts";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task QualificationRunsAgainstThePinnedPostgresMajorAndMinor()
    {
        await using var database = await DisposablePostgres.CreateAsync(TestContext.Current.CancellationToken);
        await using var command = database.Data.CreateCommand("SHOW server_version_num");
        Assert.Equal("180006", (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
