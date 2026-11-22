using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Persistence;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfSchemaTests
{
    [Fact]
    public async Task PostgresModelMapsEveryBaselineColumnWithoutChangingNullability()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DisposablePostgres.CreateAsync(ct);
        await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), ct);
        await using var connection = await database.Data.OpenConnectionAsync(ct);
        var options = new DbContextOptionsBuilder<PostgresAkantiDbContext>().UseNpgsql(connection).Options;
        await using var context = new PostgresAkantiDbContext(options);
        var entities = context.Model.GetEntityTypes().ToArray();
        Assert.Equal(28, entities.Length);
        Assert.Equal(501, entities.Sum(entity => entity.GetProperties().Count()));
        foreach (var entity in entities)
        {
            string table = entity.GetTableName()!;
            var store = StoreObjectIdentifier.Table(table, "shop");
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name,is_nullable FROM information_schema.columns WHERE table_schema='shop' AND table_name=$1";
            command.Parameters.AddWithValue(table);
            var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0), reader.GetString(1) == "NO");
            Assert.Equal(columns.Count, entity.GetProperties().Count());
            foreach (var property in entity.GetProperties())
            {
                Assert.False(property.IsShadowProperty());
                Assert.True(columns.TryGetValue(property.GetColumnName(store)!, out bool required));
                Assert.True(required == !property.IsNullable, table + "." + property.GetColumnName(store) + " nullability mismatch");
            }
        }
    }

    [Fact]
    public async Task SqliteModelMapsAllBaselineColumnsKeysAndForeignKeysWithoutShadowFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("akanti-ef-model-");
        try
        {
            string path = Path.Combine(directory.FullName, "shop.db");
            await SqliteSchema.InitializeAsync(path, ct);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(ct);
            SqliteSchema.Configure(connection);
            var options = new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite(connection).Options;
            await using var context = new SqliteAkantiDbContext(options);
            var entities = context.Model.GetEntityTypes().ToArray();
            Assert.Equal(28, entities.Length);
            Assert.Equal(501, entities.Sum(entity => entity.GetProperties().Count()));
            foreach (var entity in entities)
            {
                Assert.DoesNotContain(entity.GetProperties(), property => property.IsShadowProperty());
                string table = entity.GetTableName()!;
                var store = StoreObjectIdentifier.Table(table, null);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_xinfo(\"" + table + "\")";
                var columns = new Dictionary<string, (bool Required, int Key)>(StringComparer.Ordinal);
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1), (reader.GetInt32(3) != 0 || reader.GetInt32(5) != 0, reader.GetInt32(5)));
                Assert.Equal(columns.Count, entity.GetProperties().Count());
                foreach (var property in entity.GetProperties())
                {
                    Assert.True(columns.TryGetValue(property.GetColumnName(store)!, out var physical));
                    Assert.True(physical.Required == !property.IsNullable, table + "." + property.GetColumnName(store) + " nullability mismatch");
                }
                Assert.Equal(columns.Where(column => column.Value.Key > 0).OrderBy(column => column.Value.Key).Select(column => column.Key),
                    entity.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName(store)));
                command.CommandText = "PRAGMA foreign_key_list(\"" + table + "\")";
                var foreignKeys = new HashSet<long>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) foreignKeys.Add(reader.GetInt64(0));
                Assert.Equal(foreignKeys.Count, entity.GetForeignKeys().Count() + entity.GetAnnotations().Count(annotation => annotation.Name.StartsWith("Akanti:DatabaseForeignKey:", StringComparison.Ordinal)));
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}
