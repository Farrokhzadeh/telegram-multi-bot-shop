using NpgsqlTypes;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class PostgresQualificationTests
{
    [Fact]
    public async Task ExactDecimalAndRollbackWorkInAnOwnedDisposableDatabase()
    {
        await using var database = await DisposablePostgres.CreateAsync();
        await using var connection = await database.Data.OpenConnectionAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE qualification_amounts (id integer PRIMARY KEY, amount numeric NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }

        const decimal amount = 123456789012.123456789012m;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO qualification_amounts VALUES (1, @amount) RETURNING amount";
            insert.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
            Assert.Equal(amount, (decimal)(await insert.ExecuteScalarAsync())!);
            await transaction.RollbackAsync();
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM qualification_amounts";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task QualificationRunsAgainstThePinnedPostgresMajorAndMinor()
    {
        await using var database = await DisposablePostgres.CreateAsync();
        await using var command = database.Data.CreateCommand("SHOW server_version_num");
        Assert.Equal("180006", (string)(await command.ExecuteScalarAsync())!);
    }
}
