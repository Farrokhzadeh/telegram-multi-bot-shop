using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Core;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfProviderTests
{
    [Fact]
    public async Task SqliteTranslatesExactValuesAndPreservesCursorUtcAndBinaryContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<ProbeContext>().UseSqlite(connection).Options;
        await using var context = new ProbeContext(options);
        await VerifyAsync(context, ct);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT typeof(Amount),Amount FROM ef_provider_rows ORDER BY Id LIMIT 1";
        await using var reader = await inspect.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("text", reader.GetString(0));
        Assert.Equal("0.00000001", reader.GetString(1));
    }

    [Fact]
    public async Task PostgresTranslatesExactValuesAndPreservesCursorUtcAndBinaryContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DisposablePostgres.CreateAsync(ct);
        await using var connection = await database.Data.OpenConnectionAsync(ct);
        var options = new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(connection).Options;
        await using var context = new ProbeContext(options);
        await VerifyAsync(context, ct);
    }

    [Fact]
    public async Task SqliteExplicitTransactionRollsBackTheWholeOperationAfterCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<ProbeContext>().UseSqlite(connection).Options;
        await using var context = new ProbeContext(options);
        await context.Database.EnsureCreatedAsync(ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await context.Database.UseTransactionAsync(transaction, ct);
            context.Rows.Add(Row(1, 10m));
            await context.SaveChangesAsync(ct);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Rows.CountAsync(cancelled.Token));
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await context.Database.UseTransactionAsync(null, ct);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Rows.ToListAsync(ct));
    }

    [Fact]
    public async Task SqliteModelOwnedRebuildPreservesRowsIndexesAndChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<ProbeContext>().UseSqlite(connection).Options;
        await using var context = new ProbeContext(options);
        await context.Database.EnsureCreatedAsync(ct);
        context.Rows.Add(Row(1, 2m));
        await context.SaveChangesAsync(ct);
        var alteration = new AlterColumnOperation
        {
            Name = "Optional",
            Table = "ef_provider_rows",
            ClrType = typeof(string),
            ColumnType = "TEXT",
            IsNullable = true,
            MaxLength = 64,
            OldColumn = new AddColumnOperation
            {
                Name = "Optional",
                Table = "ef_provider_rows",
                ClrType = typeof(string),
                ColumnType = "TEXT",
                IsNullable = true
            }
        };
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([alteration], model);
        Assert.Contains(commands, command => command.CommandText.Contains("ef_temp_ef_provider_rows", StringComparison.Ordinal));
        foreach (var migration in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = migration.CommandText;
            await command.ExecuteNonQueryAsync(ct);
        }
        context.ChangeTracker.Clear();
        Assert.Equal(2m, (await context.Rows.SingleAsync(ct)).Amount);
        await using (var index = connection.CreateCommand())
        {
            index.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='IX_ef_provider_rows_At_Id'";
            Assert.Equal(1L, await index.ExecuteScalarAsync(ct));
        }
        var invalid = Row(2, 3m);
        invalid.Content = [];
        context.Rows.Add(invalid);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        context.ChangeTracker.Clear();
        Assert.Single(await context.Rows.ToArrayAsync(ct));
    }

    private static async Task VerifyAsync(ProbeContext context, CancellationToken ct)
    {
        await context.Database.EnsureCreatedAsync(ct);
        decimal[] amounts = [0.00000001m, 2m, 10m, 12345678901234567890.12345678m];
        var originals = amounts.Select((amount, index) => Row(index + 1, amount)).ToArray();
        context.Rows.AddRange(originals);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var ordered = context.Rows.AsNoTracking().OrderBy(row => row.Amount).Select(row => row.Amount);
        string sql = ordered.ToQueryString();
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(amounts, await ordered.ToArrayAsync(ct));
        var above = context.Rows.Where(row => row.Amount > 2m).OrderBy(row => row.Amount);
        Assert.Contains("WHERE", above.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(amounts[2..], await above.Select(row => row.Amount).ToArrayAsync(ct));
        Assert.Equal(12m, await context.Rows.Where(row => row.Id == originals[1].Id || row.Id == originals[2].Id).SumAsync(row => row.Amount, ct));
        Assert.Equal(3m, await context.Rows.Where(row => row.Id == originals[1].Id).Select(row => row.Amount + 1m).SingleAsync(ct));
        Guid cursor = originals[1].Id;
        Assert.Equal(originals.Skip(2).Select(row => row.Id), await context.Rows.Where(row => row.Id.CompareTo(cursor) > 0).OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(ct));
        var restored = await context.Rows.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct);
        for (int i = 0; i < restored.Length; i++)
        {
            Assert.Equal(originals[i].Amount, restored[i].Amount);
            Assert.Equal(originals[i].At, restored[i].At);
            Assert.Equal(originals[i].Content, restored[i].Content);
            Assert.Equal(originals[i].Optional, restored[i].Optional);
        }
        Assert.Equal("12345678901234567890.12345678", ExactDecimal.Format(restored[^1].Amount));
    }

    private static ProbeRow Row(int index, decimal amount) => new()
    {
        Id = index switch
        {
            1 => Guid.Parse("00000000-0000-0000-0000-000000000001"),
            2 => Guid.Parse("00000000-0000-0000-0000-00000000000a"),
            3 => Guid.Parse("80000000-0000-0000-0000-000000000000"),
            _ => Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")
        },
        Amount = amount,
        At = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
        Content = [0, 1, 127, 128, 255],
        Optional = index % 2 == 0 ? null : "آکانتی"
    };

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        internal DbSet<ProbeRow> Rows => Set<ProbeRow>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var row = modelBuilder.Entity<ProbeRow>();
            row.ToTable("ef_provider_rows", table => table.HasCheckConstraint("ef_content_nonempty", Database.IsSqlite() ? "length(\"Content\") > 0" : "octet_length(\"Content\") > 0"));
            row.HasIndex(value => new { value.At, value.Id });
            row.HasKey(value => value.Id);
            row.Property(value => value.Id).ValueGeneratedNever();
            row.Property(value => value.Amount).HasColumnType(Database.IsSqlite() ? "TEXT" : "numeric(28,8)");
            row.Property(value => value.Content).IsRequired();
        }
    }

    private sealed class ProbeRow
    {
        public Guid Id { get; set; }
        public decimal Amount { get; set; }
        public DateTime At { get; set; }
        public byte[] Content { get; set; } = [];
        public string? Optional { get; set; }
    }
}
