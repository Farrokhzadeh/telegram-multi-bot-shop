using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class EfDefaultValueTests
{
    [Fact]
    public async Task SqlitePreservesExplicitZeroAndFalseAndRejectsInvalidZeroDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(ct);
        SqliteSchema.Configure(connection);
        await using var context = new SqlitePlainTargetDbContext(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite(connection).Options);
        await VerifyAsync(context, ct);
    }

    [Fact]
    public async Task PostgresPreservesExplicitZeroAndFalseAndRejectsInvalidZeroDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DisposablePostgres.CreateAsync(ct);
        await using var connection = await database.Data.OpenConnectionAsync(ct);
        await using var context = new PostgresPlainTargetDbContext(new DbContextOptionsBuilder<PostgresPlainTargetDbContext>().UseNpgsql(connection).Options);
        await VerifyAsync(context, ct);
    }

    private static async Task VerifyAsync(AkantiDbContext context, CancellationToken ct)
    {
        await context.Database.MigrateAsync(ct);
        var accepted = Brand();
        accepted.TelegramUpdateHighwater = 0;
        accepted.TelegramUpdateWindow = new byte[128];
        accepted.TelegramUpdateWindow[0] = 1;
        accepted.AllowWallet = false;
        context.Add(accepted);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var restored = await context.Set<Platform.Persistence.Entities.Brand>().SingleAsync(ct);
        Assert.Equal(0, restored.TelegramUpdateHighwater);
        Assert.Equal(accepted.TelegramUpdateWindow, restored.TelegramUpdateWindow);
        Assert.False(restored.AllowWallet);
        Assert.False(restored.Active);
        context.ChangeTracker.Clear();
        foreach (bool rounding in new[] { false, true })
        {
            var invalid = Brand();
            if (rounding) invalid.PricingRoundingStep = 0;
            else invalid.MaxExchangeRateAgeSeconds = 0;
            context.Add(invalid);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
            context.ChangeTracker.Clear();
        }
        var product = new Product { Id = Guid.NewGuid(), Title = "Default fixture", FulfillmentKind = "manual", AvailabilityPolicy = "immediate", Active = false, LocalFirst = false };
        context.Add(product);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var productRow = await context.Set<Product>().SingleAsync(ct);
        Assert.False(productRow.Active);
        Assert.False(productRow.LocalFirst);
        context.ChangeTracker.Clear();
        foreach (bool minimum in new[] { false, true })
        {
            context.Add(new BrandProduct { BrandId = accepted.Id, ProductId = product.Id, PriceUsdt = 1m, MinQuantity = minimum ? 0 : 1, MaxQuantity = minimum ? 100 : 0 });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
            context.ChangeTracker.Clear();
        }
    }
    private static Platform.Persistence.Entities.Brand Brand()
    {
        Guid id = Guid.NewGuid();
        return new()
        {
            Id = id,
            Code = "default-" + id.ToString("N"),
            InternalName = "Default fixture",
            PublicName = "Default fixture",
            Market = "international",
            DefaultLanguage = "en",
            BotUsername = "default_" + id.ToString("N") + "_bot",
            BotId = Random.Shared.NextInt64(1, long.MaxValue),
            BotTokenCiphertext = [1],
            BotTokenKeyId = "fixture"
        };
    }
}
