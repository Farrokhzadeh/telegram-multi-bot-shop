using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Data.Sqlite;
using Platform.Persistence.Migrations;
using Platform.Persistence;
using Platform.Persistence.Entities;
using System.Security.Cryptography;
using System.Text;

namespace Platform.IntegrationTests;

public sealed class PlainTargetRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderPlaintextMigrationHistoryMatchesTheFinalModel(bool postgres)
    {
        using PlainTargetDbContext data = postgres
            ? new PostgresPlainDesignTimeFactory().CreateDbContext([])
            : new SqlitePlainDesignTimeFactory().CreateDbContext([]);
        Assert.Equal(PlainContentBaseline.MigrationId, Assert.Single(data.Database.GetMigrations()));
        Assert.False(data.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalModelPreservesAllTablesAndDirectContentWithoutEncryptionKeyColumns(bool postgres)
    {
        var options = new DbContextOptionsBuilder<PlainTargetDbContext>();
        if (postgres) options.UseNpgsql("Host=localhost;Database=plain_model_only;Username=plain_model_only");
        else options.UseSqlite("Data Source=:memory:");
        using var data = new PlainTargetDbContext(options.Options);
        var entities = data.Model.GetEntityTypes().ToArray();
        Assert.Equal(28, entities.Length);
        Assert.Equal(493, entities.Sum(entity => entity.GetProperties().Count()));
        (string Table, string Content, string[] Removed)[] changes =
        [
            ("brands", "bot_token", ["bot_token_ciphertext", "bot_token_key_id", "bank_number_ciphertext", "bank_holder_ciphertext", "bank_key_id"]),
            ("brands", "bank_number", []), ("brands", "bank_holder", []),
            ("inventory_items", "credential_content", ["credential_ciphertext", "credential_key_id"]),
            ("payments", "bank_destination", ["bank_destination_ciphertext", "bank_destination_key_id", "pending_evidence_ciphertext", "pending_evidence_key_id"]),
            ("payments", "pending_evidence", []),
            ("fulfillments", "delivered_content", ["delivered_content_key_id"]),
            ("support_messages", "body_content", ["body_ciphertext", "body_key_id"]),
            ("discounts", "code_content", ["code_ciphertext", "code_key_id"])
        ];
        foreach (var change in changes)
        {
            var entity = Assert.Single(entities, entity => entity.GetTableName() == change.Table);
            var table = StoreObjectIdentifier.Table(change.Table, entity.GetSchema());
            var columns = entity.GetProperties().ToDictionary(property => property.GetColumnName(table)!);
            var content = columns[change.Content]; Assert.Equal(typeof(byte[]), content.ClrType);
            Assert.Null(content.GetValueConverter());
            foreach (string removed in change.Removed) Assert.False(columns.ContainsKey(removed), change.Table + "." + removed);
        }
        var payment = Assert.Single(entities, entity => entity.GetTableName() == "payments");
        Assert.Contains(payment.GetProperties(), property => property.GetColumnName(StoreObjectIdentifier.Table("payments", payment.GetSchema())) == "ipn_key_id");
    }
    [Theory]
    [InlineData(null)]
    [InlineData("فارسی 🌍\0 original")]
    public async Task DirectInventoryBytesAndNullableUnicodeSurviveRestartWithoutKeyFiles(string? description)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-plain-runtime-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            Guid product = Guid.NewGuid(), inventory = Guid.NewGuid(); byte[] bytes = [0, 255, 128, .. Encoding.UTF8.GetBytes("اعتبار\0exact")];
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
            await connection.OpenAsync(ct); SqliteSchema.Configure(connection);
            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL";
                Assert.Equal("wal", await journal.ExecuteScalarAsync(ct));
            }
            var options = new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(connection).Options;
            await using (var writer = new PlainTargetDbContext(options))
            {
                await PlainTargetSchema.CreateAsync(writer, ct);
                writer.Add(new Product { Id = product, FulfillmentKind = "local", AvailabilityPolicy = "immediate", Title = "Plain fixture", Description = description });
                writer.Add(new InventoryItem { Id = inventory, ProductId = product, CredentialContent = bytes, SourceFingerprint = SHA256.HashData(bytes), OriginKind = "local_import", State = "available" });
                await writer.SaveChangesAsync(ct);
            }
            await connection.CloseAsync();
            var runtime = SqliteSessionFactory.ForPlaintext(path);
            await runtime.RunEfAsync(false, null, async (data, token) =>
            {
                Assert.Equal(bytes, await data.Data.Set<InventoryItem>().Where(row => row.Id == inventory).Select(row => row.CredentialContent).SingleAsync(token));
                Assert.Equal(description, await data.Data.Set<Product>().Where(row => row.Id == product).Select(row => row.Description).SingleAsync(token));
                return true;
            }, ct);
            var wrong = SqliteSessionFactory.ForPlaintext(Path.Combine(directory.FullName, "missing.db"));
            await Assert.ThrowsAsync<SqliteException>(() => wrong.RunAsync(false, (_, _, _) => Task.FromResult(true), ct));
            Assert.Equal(["shop.db"], Directory.GetFiles(directory.FullName).Select(path => Path.GetFileName(path)!).Where(name => name is not "shop.db-wal" and not "shop.db-shm").Order().ToArray());
        }
        finally { directory.Delete(recursive: true); }
    }
    internal static async Task InitializeSqliteAsync(string path, CancellationToken ct, bool staged = false)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=WAL";
        Assert.Equal("wal", await journal.ExecuteScalarAsync(ct));
        await using var data = new PlainTargetDbContext(new DbContextOptionsBuilder<PlainTargetDbContext>().UseSqlite(connection).Options);
        await PlainTargetSchema.CreateAsync(data, ct, forConversion: staged);
    }

    [Fact]
    public async Task PlaintextRuntimeRefusesStagedAndLegacyTargetsBeforeAnyOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-plain-runtime-");
        try
        {
            string staged = Path.Combine(directory.FullName, "staged.db"), legacy = Path.Combine(directory.FullName, "legacy.db");
            await InitializeSqliteAsync(staged, ct, staged: true);
            await SqliteSchema.InitializeAsync(legacy, ct);
            foreach (string path in new[] { staged, legacy })
            {
                var sessions = SqliteSessionFactory.ForPlaintext(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunAsync<bool>(false, (_, _, _) =>
                    throw new NotSupportedException("Incompatible target reached the runtime operation."), ct));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

}
