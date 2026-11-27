using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        await Platform.Persistence.SchemaRunner.ApplyPlaintextAsync(database.Data, ct);
        await using var connection = await database.Data.OpenConnectionAsync(ct);
        var options = new DbContextOptionsBuilder<PostgresPlainTargetDbContext>().UseNpgsql(connection).Options;
        await using var context = new PostgresPlainTargetDbContext(options);
        var entities = context.Model.GetEntityTypes().ToArray();
        Assert.Equal(28, entities.Length);
        Assert.Equal(493, entities.Sum(entity => entity.GetProperties().Count()));
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
            await SqliteSchema.InitializePlaintextAsync(path, ct);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(ct);
            SqliteSchema.Configure(connection);
            var options = new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options;
            await using var context = new SqlitePlainTargetDbContext(options);
            var entities = context.Model.GetEntityTypes().ToArray();
            Assert.Equal(28, entities.Length);
            Assert.Equal(493, entities.Sum(entity => entity.GetProperties().Count()));
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
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void SchemaDescriptionIsDeterministicWithoutConnectingToTheDesignDatabase(string provider)
    {
        var first = Platform.SchemaRunner.EfSchemaCommand.Describe(provider);
        Assert.Equal(first, Platform.SchemaRunner.EfSchemaCommand.Describe(provider));
        Assert.Equal(provider, first.Provider);
        Assert.EndsWith("_InitialPlaintext", first.Migration, StringComparison.Ordinal);
        Assert.Equal(64, first.ScriptSha256.Length);
        Assert.All(first.ScriptSha256, value => Assert.True(char.IsAsciiHexDigitLower(value)));
        Assert.Throws<ArgumentException>(() => Platform.SchemaRunner.EfSchemaCommand.Describe("unknown"));
    }

    [Fact]
    public async Task PostgresEfMigrationPreservesDeferredReferencesAndCanonicalDatabaseGuards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DisposablePostgres.CreateAsync(ct);
        await using var connection = await database.Data.OpenConnectionAsync(ct);
        var options = new DbContextOptionsBuilder<PostgresPlainTargetDbContext>().UseNpgsql(connection).Options;
        await using var context = new PostgresPlainTargetDbContext(options);
        Assert.False(context.Database.HasPendingModelChanges());
        await context.Database.MigrateAsync(ct);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync(ct));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema='shop' AND table_name<>'__EFMigrationsHistory' AND table_type='BASE TABLE'";
        Assert.Equal(28L, await command.ExecuteScalarAsync(ct));
        command.CommandText = "SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='shop' AND c.contype='f' AND c.condeferrable";
        Assert.Equal(95L, await command.ExecuteScalarAsync(ct));
        command.CommandText = "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='shop' AND NOT t.tgisinternal";
        Assert.Equal(85L, await command.ExecuteScalarAsync(ct));
        await VerifyTextRoundtripAsync(context, ct);
    }

    [Fact]
    public async Task SqliteEfMigrationCreatesStrictTablesAndPreservesAllForeignKeysAndHistoryGuards()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        var options = new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options;
        await using var context = new SqlitePlainTargetDbContext(options);
        Assert.False(context.Database.HasPendingModelChanges());
        await context.Database.MigrateAsync(ct);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync(ct));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pragma_table_list WHERE name NOT LIKE 'sqlite_%' AND name<>'__EFMigrationsHistory' AND strict=1";
        Assert.Equal(28L, await command.ExecuteScalarAsync(ct));
        long foreignKeys = 0;
        foreach (var entity in context.Model.GetEntityTypes())
        {
            command.CommandText = "SELECT count(DISTINCT id) FROM pragma_foreign_key_list($table)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$table", entity.GetTableName()!);
            foreignKeys += (long)(await command.ExecuteScalarAsync(ct))!;
        }
        Assert.Equal(95, foreignKeys);
        command.Parameters.Clear();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='trigger'";
        Assert.Equal(27L, await command.ExecuteScalarAsync(ct));
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'__EFMigrationsHistory'";
        int deferred = 0;
        await using (var definitions = await command.ExecuteReaderAsync(ct))
            while (await definitions.ReadAsync(ct)) deferred += definitions.GetString(0).Split("DEFERRABLE", StringSplitOptions.None).Length - 1;
        Assert.Equal(95, deferred);
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='fulfillments'";
        string definition = (string)(await command.ExecuteScalarAsync(ct))!;
        Assert.Contains("fulfillments_order_id_manifest_generation_fk", definition, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY IMMEDIATE", definition, StringComparison.Ordinal);
        await VerifyTextRoundtripAsync(context, ct);
    }

    [Theory]
    [InlineData("wallet_entries", "reason")]
    [InlineData("fulfillments", "kind")]
    [InlineData("telegram_deliveries", "purpose")]
    [InlineData("jobs", "job_kind")]
    public async Task SqliteBusinessRebuildRestoresReviewedTriggersAndDeferredForeignKeys(string table, string column)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var context = new SqlitePlainTargetDbContext(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync(ct);
        Guid brand = Guid.NewGuid(), member = Guid.NewGuid(), entry = Guid.NewGuid();
        context.Add(SchemaBrand(brand));
        context.Add(new Platform.Persistence.Entities.User { Id = member, BrandId = brand, TelegramId = 7001, Language = "en" });
        context.Add(new Platform.Persistence.Entities.Wallet { UserId = member, Balance = 7m, Version = 1 });
        context.Add(new Platform.Persistence.Entities.WalletEntry
        {
            Id = entry,
            UserId = member,
            SignedAmount = 7m,
            BalanceAfter = 7m,
            PostingVersion = 1,
            MovementKind = "opening_balance",
            IsPosting = true,
            IdempotencyKey = "rebuild-opening",
            LegacyImportBatch = Guid.NewGuid(),
            Reason = "Retained history"
        });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        string[] before = await TriggersAsync(connection, ct);
        Assert.Equal(27, before.Length);
        var model = context.GetService<IDesignTimeModel>().Model;
        var alteration = new Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation
        {
            Name = column,
            Table = table,
            ClrType = typeof(string),
            ColumnType = "TEXT",
            IsNullable = false,
            MaxLength = 512,
            OldColumn = new Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation
            {
                Name = column,
                Table = table,
                ClrType = typeof(string),
                ColumnType = "TEXT",
                IsNullable = false,
                MaxLength = 256
            }
        };
        var commands = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsSqlGenerator>().Generate([alteration], model);
        Assert.Contains(commands, value => value.CommandText.Contains("ef_temp_" + table, StringComparison.Ordinal));
        foreach (var migration in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = migration.CommandText;
            await command.ExecuteNonQueryAsync(ct);
        }
        Assert.Equal(before, await TriggersAsync(connection, ct));
        var retained = await context.Set<Platform.Persistence.Entities.WalletEntry>().AsNoTracking().SingleAsync(ct);
        Assert.Equal(entry, retained.Id);
        Assert.Equal(7m, retained.BalanceAfter);
        Assert.Equal("Retained history", retained.Reason);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT strict FROM pragma_table_list WHERE name=$table";
        inspect.Parameters.AddWithValue("$table", table);
        Assert.Equal(1L, await inspect.ExecuteScalarAsync(ct));
        inspect.Parameters.Clear();
        inspect.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%'";
        int references = 0;
        await using (var reader = await inspect.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) references += reader.GetString(0).Split("DEFERRABLE", StringSplitOptions.None).Length - 1;
        Assert.Equal(95, references);
        inspect.CommandText = "INSERT INTO wallet_entries(id,user_id,signed_amount,balance_after,posting_version,movement_kind,is_posting,idempotency_key,legacy_import_batch,reason) VALUES($id,$user,'7','7',1,'opening_balance',1,'rebuilt',$batch,'Retained history')";
        inspect.Parameters.AddWithValue("$id", Guid.NewGuid());
        inspect.Parameters.AddWithValue("$user", Guid.NewGuid());
        inspect.Parameters.AddWithValue("$batch", Guid.NewGuid());
        await Assert.ThrowsAsync<SqliteException>(() => inspect.ExecuteNonQueryAsync(ct));
        inspect.Parameters.Clear();
        inspect.CommandText = "DELETE FROM wallet_entries";
        var immutable = await Assert.ThrowsAsync<SqliteException>(() => inspect.ExecuteNonQueryAsync(ct));
        Assert.Contains("immutable history", immutable.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCyclicUserOrderReferencesRequireACompleteDeferredTransaction(bool complete)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var context = new SqlitePlainTargetDbContext(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync(ct);
        Guid brand = Guid.NewGuid(), product = Guid.NewGuid(), member = Guid.NewGuid(), order = Guid.NewGuid();
        context.Add(new Platform.Persistence.Entities.Brand
        {
            Id = brand,
            Code = "cycle",
            InternalName = "Cycle",
            PublicName = "Cycle",
            Market = "international",
            DefaultLanguage = "en",
            BotUsername = "cycle_bot",
            BotId = 1001,
            BotTokenCiphertext = [1],
            BotTokenKeyId = "fixture",
            Active = true
        });
        context.Add(new Platform.Persistence.Entities.Product { Id = product, Title = "Cycle", FulfillmentKind = "manual", AvailabilityPolicy = "immediate" });
        await context.SaveChangesAsync(ct);
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await context.Database.UseTransactionAsync(transaction, ct);
            await using var defer = connection.CreateCommand();
            defer.Transaction = transaction;
            defer.CommandText = "PRAGMA defer_foreign_keys=ON";
            await defer.ExecuteNonQueryAsync(ct);
            context.Add(new Platform.Persistence.Entities.User { Id = member, BrandId = brand, TelegramId = 7001, Language = "en", FlowOrderId = order });
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
            if (complete)
            {
                var created = DateTime.UtcNow;
                context.Add(new Platform.Persistence.Entities.Order
                {
                    Id = order,
                    UserId = member,
                    ProductId = product,
                    Quantity = 1,
                    OrderKind = "product_purchase",
                    PaymentStatus = "awaiting_payment",
                    FulfillmentStatus = "unfulfilled",
                    Currency = "USDT",
                    PaymentMethod = "crypto",
                    UnitPriceUsdt = 1m,
                    PricingRoundingStepSnapshot = 5000m,
                    PriceCalculationVersion = "fixture",
                    BrandPublicNameSnapshot = "Cycle",
                    ProductTitleSnapshot = "Cycle",
                    PaymentTermsSnapshot = "crypto",
                    FulfillmentKindSnapshot = "manual",
                    LocalFirstSnapshot = true,
                    CreatedAt = created,
                    UpdatedAt = created,
                    PaymentDueAt = created.AddMinutes(30)
                });
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                await Assert.ThrowsAsync<SqliteException>(() => transaction.CommitAsync(ct));
                await transaction.RollbackAsync(ct);
            }
        }
        await context.Database.UseTransactionAsync(null, ct);
        context.ChangeTracker.Clear();
        Assert.Equal(complete ? 1 : 0, await context.Set<Platform.Persistence.Entities.User>().CountAsync(ct));
        if (!complete) return;
        Assert.Equal(order, await context.Set<Platform.Persistence.Entities.User>().Select(value => value.FlowOrderId).SingleAsync(ct));
        context.Add(new Platform.Persistence.Entities.User { Id = Guid.NewGuid(), BrandId = brand, TelegramId = 7001, Language = "en" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
    }

    private static Platform.Persistence.Entities.Brand SchemaBrand(Guid id) => new()
    {
        Id = id,
        Code = "cycle",
        InternalName = "Cycle",
        PublicName = "Cycle",
        Market = "international",
        DefaultLanguage = "en",
        BotUsername = "cycle_bot",
        BotId = 1001,
        BotTokenCiphertext = [1],
        BotTokenKeyId = "fixture",
        Active = true
    };

    private static async Task<string[]> TriggersAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' ORDER BY name";
        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) definitions.Add(reader.GetString(0));
        return definitions.ToArray();
    }

    private static async Task VerifyTextRoundtripAsync(AkantiDbContext context, CancellationToken ct)
    {
        context.Set<Platform.Persistence.Entities.Product>().Add(new()
        {
            Id = Guid.NewGuid(),
            Title = new string('x', 161),
            FulfillmentKind = "manual",
            AvailabilityPolicy = "immediate"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Set<Platform.Persistence.Entities.Product>().ToArrayAsync(ct));
        string original = string.Concat(Enumerable.Repeat("😀", 160));
        Guid id = Guid.NewGuid();
        context.Set<Platform.Persistence.Entities.Product>().Add(new()
        {
            Id = id,
            Title = original,
            FulfillmentKind = "manual",
            AvailabilityPolicy = "immediate"
        });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        Assert.Equal(original, await context.Set<Platform.Persistence.Entities.Product>().Where(value => value.Id == id).Select(value => value.Title).SingleAsync(ct));
    }

}
