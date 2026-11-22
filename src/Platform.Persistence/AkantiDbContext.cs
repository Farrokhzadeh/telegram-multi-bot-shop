using Microsoft.EntityFrameworkCore;
using Platform.Persistence.Configurations;

namespace Platform.Persistence;

public abstract class AkantiDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        bool sqlite = Database.IsSqlite();
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

public sealed class SqliteAkantiDbContext(DbContextOptions<SqliteAkantiDbContext> options) : AkantiDbContext(options);

public sealed class PostgresAkantiDbContext(DbContextOptions<PostgresAkantiDbContext> options) : AkantiDbContext(options);
