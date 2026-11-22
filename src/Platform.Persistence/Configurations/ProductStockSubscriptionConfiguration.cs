using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class ProductStockSubscriptionConfiguration(bool sqlite) : IEntityTypeConfiguration<ProductStockSubscription>
{
    public void Configure(EntityTypeBuilder<ProductStockSubscription> row)
    {
        row.ToTable("product_stock_subscriptions", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_product_stock_subscriptions_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_2", "user_id IS NULL OR akanti_uuid(user_id)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_3", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_4", "triggered_at IS NULL OR triggered_at >= created_at");
                table.HasCheckConstraint("ck_product_stock_subscriptions_5", "cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_6", "closed_at IS NULL OR closed_at >= created_at");
                table.HasCheckConstraint("ck_product_stock_subscriptions_7", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_8", "triggered_at IS NULL OR akanti_utc_time(triggered_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_9", "cancelled_at IS NULL OR akanti_utc_time(cancelled_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_10", "closed_at IS NULL OR akanti_utc_time(closed_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_product_stock_subscriptions_1", "triggered_at IS NULL OR triggered_at >= created_at");
                table.HasCheckConstraint("ck_product_stock_subscriptions_2", "cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_3", "closed_at IS NULL OR closed_at >= created_at");
                table.HasCheckConstraint("ck_product_stock_subscriptions_4", "isfinite(created_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_5", "isfinite(triggered_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_6", "isfinite(cancelled_at)");
                table.HasCheckConstraint("ck_product_stock_subscriptions_7", "isfinite(closed_at)");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var user_id = row.Property(v => v.UserId).HasColumnName("user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        user_id.IsRequired();
        user_id.ValueGeneratedNever();
        if (sqlite) user_id.UseCollation("NOCASE");
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.IsRequired();
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var triggered_at = row.Property(v => v.TriggeredAt).HasColumnName("triggered_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) triggered_at.HasConversion(EfValues.UtcTime);
        var cancelled_at = row.Property(v => v.CancelledAt).HasColumnName("cancelled_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) cancelled_at.HasConversion(EfValues.UtcTime);
        var closed_at = row.Property(v => v.ClosedAt).HasColumnName("closed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) closed_at.HasConversion(EfValues.UtcTime);
        row.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("product_stock_subscriptions_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("product_stock_subscriptions_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.UserId, v.ProductId }).HasDatabaseName("subscription_active_once").IsUnique().HasFilter(sqlite ? "closed_at IS NULL" : "closed_at IS NULL");
        row.HasIndex(v => new { v.ClosedAt, v.Id }).HasDatabaseName("subscription_closed_retention").HasFilter(sqlite ? "closed_at IS NOT NULL" : "closed_at IS NOT NULL");
    }
}
