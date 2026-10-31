using Npgsql;

namespace Platform.Testing;

public sealed class DisposablePostgres : IAsyncDisposable
{
    private readonly NpgsqlDataSource admin;
    private readonly string databaseName;

    private DisposablePostgres(NpgsqlDataSource admin, NpgsqlDataSource data, string databaseName)
    {
        this.admin = admin;
        Data = data;
        this.databaseName = databaseName;
    }

    public NpgsqlDataSource Data { get; }

    public static async Task<DisposablePostgres> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (Environment.GetEnvironmentVariable("SHOP_TEST_ALLOW_DATABASE_CREATION") != "1")
        {
            throw new InvalidOperationException("Disposable database creation must be explicitly enabled.");
        }

        string connection = Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN")
            ?? throw new InvalidOperationException("A dedicated synthetic PostgreSQL control database is required.");
        var options = new NpgsqlConnectionStringBuilder(connection);
        if (options.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            options.Database != "shop_test_control" || options.Username != "shop_test_runner")
        {
            throw new InvalidOperationException("Only the loopback synthetic test identity is allowed.");
        }

        options.IncludeErrorDetail = false;
        var admin = new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
        string name = "shop_qualification_" + Guid.NewGuid().ToString("N");
        try
        {
            await using var command = admin.CreateCommand($"CREATE DATABASE \"{name}\"");
            await command.ExecuteNonQueryAsync(cancellationToken);
            options.Database = name;
            return new DisposablePostgres(admin,
                new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build(), name);
        }
        catch
        {
            await admin.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Data.DisposeAsync();
        try
        {
            await using var command = admin.CreateCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await admin.DisposeAsync();
        }
    }
}
