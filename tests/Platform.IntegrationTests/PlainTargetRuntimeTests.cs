using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
    public void FinalModelPreservesAllTablesAndDirectContentWithoutEncryptionKeyColumns(bool postgres)
    {
        using AkantiDbContext data = postgres ? new PostgresAkantiDbContext(new DbContextOptionsBuilder<PostgresAkantiDbContext>().UseNpgsql("Host=localhost;Database=plain_model_only;Username=plain_model_only").Options) : new SqliteAkantiDbContext(new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite("Data Source=:memory:").Options);
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
            string path = Path.Combine(directory.FullName, "shop.db"); await SqliteSchema.InitializeAsync(path, ct);
            Guid product = Guid.NewGuid(), inventory = Guid.NewGuid(); byte[] bytes = [0, 255, 128, .. Encoding.UTF8.GetBytes("اعتبار\0exact")];
            var options = new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite("Data Source=" + path).Options;
            await using (var writer = new SqliteAkantiDbContext(options))
            {
                var entity = writer.Model.FindEntityType(typeof(InventoryItem))!; var table = StoreObjectIdentifier.Table("inventory_items", entity.GetSchema());
                string content = Assert.Single(entity.GetProperties(), property => property.GetColumnName(table) == "credential_content").Name;
                writer.Add(new Product { Id = product, FulfillmentKind = "local", AvailabilityPolicy = "immediate", Title = "Plain fixture", Description = description });
                var row = new InventoryItem { Id = inventory, ProductId = product, SourceFingerprint = SHA256.HashData(bytes), OriginKind = "local_import", State = "available" };
                writer.Add(row); writer.Entry(row).Property(content).CurrentValue = bytes; await writer.SaveChangesAsync(ct);
            }
            await using var reader = new SqliteAkantiDbContext(options);
            var metadata = reader.Model.FindEntityType(typeof(InventoryItem))!; var storedTable = StoreObjectIdentifier.Table("inventory_items", metadata.GetSchema());
            string name = Assert.Single(metadata.GetProperties(), property => property.GetColumnName(storedTable) == "credential_content").Name;
            Assert.Equal(bytes, await reader.Set<InventoryItem>().Where(row => row.Id == inventory).Select(row => EF.Property<byte[]>(row, name)).SingleAsync(ct));
            Assert.Equal(description, await reader.Set<Product>().Where(row => row.Id == product).Select(row => row.Description).SingleAsync(ct));
            Assert.Equal(["shop.db"], Directory.GetFiles(directory.FullName).Select(path => Path.GetFileName(path)!).Where(name => name is not "shop.db-wal" and not "shop.db-shm").Order().ToArray());
        }
        finally { directory.Delete(recursive: true); }
    }

}
