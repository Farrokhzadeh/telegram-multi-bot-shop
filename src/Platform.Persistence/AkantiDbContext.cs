using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Persistence.Migrations;
using Platform.Persistence.Configurations;

namespace Platform.Persistence;

public abstract class AkantiDbContext(DbContextOptions options) : DbContext(options)
{
    internal EfIntegrity? Integrity { get; set; }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.AddInterceptors(EfMutationInterceptor.Instance);
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (Integrity is not null)
        {
            Integrity.Reject();
            throw new InvalidOperationException("EF command sessions require asynchronous writes.");
        }
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            Integrity?.CaptureTracked();
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch { Integrity?.Reject(); throw; }
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        bool sqlite = Database.IsSqlite();
        modelBuilder.HasDbFunction(() => EfValues.TransactionTime()).HasName(sqlite ? "akanti_utc_now" : "transaction_timestamp").IsBuiltIn();
        modelBuilder.HasDbFunction(() => EfValues.UtcNow()).HasName(sqlite ? "akanti_utc_now" : "clock_timestamp").IsBuiltIn();
        modelBuilder.ApplyConfiguration(new Configurations.BrandConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.BrandChannelConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.UserConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.AdminConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.AdminSessionConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.ProductConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.BrandProductConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.InventoryItemConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.WalletConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.WalletEntryConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.OrderConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.PaymentConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.FulfillmentConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.SupplierRequestConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.JobConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.CampaignConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.TelegramDeliveryConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.SupportTicketConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.SupportMessageConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.LegacyIdMapConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.AuditEventConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.DiscountConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.DiscountUseConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.DiscountRevisionConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.BotActivityEventConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.SupplierRequestAttemptConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.ProductSupplierSourceConfiguration(sqlite));
        modelBuilder.ApplyConfiguration(new Configurations.ProductStockSubscriptionConfiguration(sqlite));
    }
}

public class PlainTargetDbContext : AkantiDbContext
{
    public PlainTargetDbContext(DbContextOptions<PlainTargetDbContext> options) : base(options) { }
    private protected PlainTargetDbContext(DbContextOptions options) : base(options) { }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (optionsBuilder.Options.Extensions.Any(extension => extension.Info.IsDatabaseProvider && extension.GetType().Assembly == typeof(SqliteDbContextOptionsBuilderExtensions).Assembly))
            optionsBuilder.ReplaceService<IMigrationsSqlGenerator, AkantiSqliteMigrationsSqlGenerator>();
        else if (optionsBuilder.Options.Extensions.Any(extension => extension.Info.IsDatabaseProvider && extension.GetType().Assembly == typeof(NpgsqlDbContextOptionsBuilderExtensions).Assembly))
            optionsBuilder.UseNpgsql(provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "shop"));
    }
}

public sealed class SqlitePlainTargetDbContext(DbContextOptions<SqlitePlainTargetDbContext> options) : PlainTargetDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        if (!Database.IsSqlite()) throw new InvalidOperationException("A SQLite plaintext provider is required.");
        base.OnModelCreating(builder);
    }
}

public sealed class PostgresPlainTargetDbContext(DbContextOptions<PostgresPlainTargetDbContext> options) : PlainTargetDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        if (!Database.IsNpgsql()) throw new InvalidOperationException("A PostgreSQL plaintext provider is required.");
        base.OnModelCreating(builder);
    }
}

internal sealed class SqliteAkantiDbContext(DbContextOptions<SqliteAkantiDbContext> options):PlainTargetDbContext(options) { }
internal sealed class PostgresAkantiDbContext(DbContextOptions<PostgresAkantiDbContext> options):PlainTargetDbContext(options) { }
