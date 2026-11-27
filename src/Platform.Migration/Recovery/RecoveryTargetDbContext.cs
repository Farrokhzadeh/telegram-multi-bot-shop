using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Configurations;

namespace Platform.Migration.Recovery;

internal abstract class RecoveryTargetDbContext(DbContextOptions options):DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		bool sqlite=Database.IsSqlite();
		modelBuilder.HasDbFunction(()=>EfValues.TransactionTime()).HasName(sqlite?"akanti_utc_now":"transaction_timestamp").IsBuiltIn();
		modelBuilder.HasDbFunction(()=>EfValues.UtcNow()).HasName(sqlite?"akanti_utc_now":"clock_timestamp").IsBuiltIn();
		modelBuilder.ApplyConfiguration(new RecoveryBrandConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new BrandChannelConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new UserConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new AdminConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new AdminSessionConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new ProductConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new BrandProductConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new RecoveryInventoryItemConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new WalletConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new WalletEntryConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new OrderConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new RecoveryPaymentConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new RecoveryFulfillmentConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new SupplierRequestConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new JobConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new CampaignConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new TelegramDeliveryConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new SupportTicketConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new RecoverySupportMessageConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new LegacyIdMapConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new AuditEventConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new RecoveryDiscountConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new DiscountUseConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new DiscountRevisionConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new BotActivityEventConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new SupplierRequestAttemptConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new ProductSupplierSourceConfiguration(sqlite));
		modelBuilder.ApplyConfiguration(new ProductStockSubscriptionConfiguration(sqlite));
	}
}

internal sealed class SqliteRecoveryTargetDbContext(DbContextOptions<SqliteRecoveryTargetDbContext> options):RecoveryTargetDbContext(options)
{
	protected override void OnModelCreating(ModelBuilder builder)
	{
		if(!Database.IsSqlite()) throw new MigrationRejectedException();
		base.OnModelCreating(builder);
	}
}

internal sealed class PostgresRecoveryTargetDbContext(DbContextOptions<PostgresRecoveryTargetDbContext> options):RecoveryTargetDbContext(options)
{
	protected override void OnModelCreating(ModelBuilder builder)
	{
		if(!Database.IsNpgsql()) throw new MigrationRejectedException();
		base.OnModelCreating(builder);
	}
}
