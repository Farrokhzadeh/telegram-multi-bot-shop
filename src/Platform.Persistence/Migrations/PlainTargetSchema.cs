using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Platform.Persistence.Migrations;

internal static class PlainTargetSchema
{
    internal const int SqliteVersion = 2;
    internal const int SqliteCopyApplicationId = 0x414b4350;
    internal const string RowCopyTable = "_akanti_row_copy";
    internal const string PostgresFormat = "akanti-plaintext-v2";

    internal static async Task CreateAsync(PlainTargetDbContext data, CancellationToken ct, bool forConversion = false)
    {
        bool sqlite = data.Database.IsSqlite();
        if (!sqlite && !data.Database.IsNpgsql()) throw new InvalidOperationException("A supported plaintext provider is required.");
        if (forConversion && !sqlite) throw new InvalidOperationException("This conversion target requires SQLite.");
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        string existing = sqlite
            ? "SELECT count(*) AS Value FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'"
            : """SELECT CASE WHEN to_regnamespace('shop') IS NULL THEN 0 ELSE 1 END AS "Value" """;
        if (await data.Database.SqlQueryRaw<int>(existing).SingleAsync(ct) != 0)
            throw new InvalidOperationException("Plaintext schema creation requires an empty destination.");
        var connection = data.Database.GetDbConnection();
        var native = transaction.GetDbTransaction();
        foreach (string text in Commands(data)) await ExecuteAsync(connection, native, text, ct);
        if (forConversion)
        {
            await ExecuteAsync(connection, native, $"PRAGMA application_id={SqliteCopyApplicationId};", ct);
            await ExecuteAsync(connection, native, $"CREATE TABLE {RowCopyTable}(id INTEGER PRIMARY KEY CHECK(id=1),schema_digest BLOB NOT NULL CHECK(length(schema_digest)=32),snapshot BLOB CHECK(length(snapshot)=32),row_count INTEGER CHECK(row_count>=0),draft BLOB CHECK(length(draft)=32),binding BLOB CHECK(binding IS NULL OR (length(binding)=32 AND snapshot IS NOT NULL)),asset_count INTEGER CHECK(asset_count IS NULL OR (asset_count>=0 AND binding IS NOT NULL)),CHECK((snapshot IS NULL)=(row_count IS NULL))) STRICT;", ct);
            byte[] digest = await SqliteSchemaDigestAsync(connection, native, ct);
            await using var seed = connection.CreateCommand();
            seed.Transaction = native;
            seed.CommandText = "INSERT INTO _akanti_row_copy(id,schema_digest) VALUES(1,$digest)";
            var parameter = seed.CreateParameter(); parameter.ParameterName = "$digest"; parameter.Value = digest;
            seed.Parameters.Add(parameter);
            await seed.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string text, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static string Script(PlainTargetDbContext data) =>
        string.Join("\n", Commands(data)).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    internal static async Task<byte[]> ExpectedSqliteSchemaAsync(CancellationToken ct, bool staged)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=True");
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var data = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(connection).Options);
        await CreateAsync(data, ct, forConversion: staged);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        return await SqliteSchemaDigestAsync(connection, snapshot, ct);
    }

    internal static byte[] ScriptDigest(PlainTargetDbContext data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-plaintext-schema-v2");
        foreach (string command in Commands(data)) SqliteStagingCopy.HashValue(hash, command.Replace("\r\n", "\n", StringComparison.Ordinal));
        return hash.GetHashAndReset();
    }

    internal static byte[] ModelDigest(PlainTargetDbContext data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-plaintext-model-v2");
        SqliteStagingCopy.HashValue(hash, ScriptDigest(data));
        foreach (var entity in data.Model.GetEntityTypes().OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            SqliteStagingCopy.HashValue(hash, entity.Name);
            foreach (var property in entity.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                SqliteStagingCopy.HashValue(hash, property.Name);
                SqliteStagingCopy.HashValue(hash, property.ClrType.FullName);
            }
            SqliteStagingCopy.HashValue(hash, null);
        }
        return hash.GetHashAndReset();
    }

    private static IEnumerable<string> Commands(PlainTargetDbContext data)
    {
        bool sqlite = data.Database.IsSqlite();
        if (!sqlite && !data.Database.IsNpgsql()) throw new InvalidOperationException("A supported plaintext provider is required.");
        var model = data.GetService<IDesignTimeModel>().Model;
        Migration migration = sqlite ? new SqlitePlaintext.InitialPlaintext() : new PostgreSqlPlaintext.InitialPlaintext();
        migration.ActiveProvider = data.Database.ProviderName ?? throw new InvalidOperationException("The migration provider is unavailable.");
        foreach (var command in data.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model))
        {
            if (command.TransactionSuppressed) throw new InvalidOperationException("Plaintext schema creation requires transactional DDL.");
            yield return command.CommandText;
        }
        string version = model.FindAnnotation("ProductVersion")?.Value as string
            ?? throw new InvalidOperationException("The EF model version is unavailable.");
        var history = data.GetService<IHistoryRepository>();
        yield return history.GetCreateScript();
        yield return history.GetInsertScript(new HistoryRow(PlainContentBaseline.MigrationId, version));
    }

    internal static async Task<byte[]> SqliteSchemaDigestAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SqliteStagingCopy.HashValue(hash, "akanti-sqlite-schema-v1");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,name,sql FROM sqlite_schema ORDER BY type,name";
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
            for (int i = 0; i < 3; i++)
                SqliteStagingCopy.HashValue(hash, rows.IsDBNull(i) ? null : rows.GetString(i));
        return hash.GetHashAndReset();
    }

    internal static string PostgresGuards() => EfMigrationGuards.PostgresBaseline
        .Replace(".bank_number_ciphertext", ".bank_number", StringComparison.Ordinal)
        .Replace(".bank_holder_ciphertext", ".bank_holder", StringComparison.Ordinal)
        .Replace(",NEW.bank_key_id", "", StringComparison.Ordinal)
        .Replace(",OLD.bank_key_id", "", StringComparison.Ordinal)
        .Replace(".bank_destination_ciphertext", ".bank_destination", StringComparison.Ordinal)
        .Replace(",NEW.bank_destination_key_id", "", StringComparison.Ordinal)
        .Replace(",OLD.bank_destination_key_id", "", StringComparison.Ordinal)
        .Replace(",NEW.delivered_content_key_id", "", StringComparison.Ordinal)
        .Replace(",OLD.delivered_content_key_id", "", StringComparison.Ordinal)
        .Replace(".credential_ciphertext", ".credential_content", StringComparison.Ordinal)
        .Replace(",NEW.credential_key_id", "", StringComparison.Ordinal)
        .Replace(",OLD.credential_key_id", "", StringComparison.Ordinal);
}
