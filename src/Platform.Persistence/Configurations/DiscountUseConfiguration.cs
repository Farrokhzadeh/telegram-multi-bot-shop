using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class DiscountUseConfiguration(bool sqlite) : IEntityTypeConfiguration<DiscountUse>
{
    public void Configure(EntityTypeBuilder<DiscountUse> row)
    {
        row.ToTable("discount_uses", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_discount_uses_1", "discount_id IS NULL OR akanti_uuid(discount_id)");
                table.HasCheckConstraint("ck_discount_uses_2", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_discount_uses_3", "discount_revision_id IS NULL OR akanti_uuid(discount_revision_id)");
                table.HasCheckConstraint("ck_discount_uses_4", "units_covered > 0");
                table.HasCheckConstraint("ck_discount_uses_5", "saved_amount IS NULL OR (akanti_decimal(saved_amount)=1)");
                table.HasCheckConstraint("ck_discount_uses_6", "akanti_decimal_compare(saved_amount,'0') >= 0");
                table.HasCheckConstraint("ck_discount_uses_7", "saved_currency IN ('USD','TOMAN')");
                table.HasCheckConstraint("ck_discount_uses_8", "status IN ('held','redeemed','released')");
                table.HasCheckConstraint("ck_discount_uses_9", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_discount_uses_10", "transitioned_at IS NULL OR akanti_utc_time(transitioned_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_discount_uses_1", "units_covered > 0");
                table.HasCheckConstraint("ck_discount_uses_2", "saved_amount >= 0");
                table.HasCheckConstraint("ck_discount_uses_3", "saved_currency IN ('USD','TOMAN')");
                table.HasCheckConstraint("ck_discount_uses_4", "status IN ('held','redeemed','released')");
                table.HasCheckConstraint("ck_discount_uses_5", "isfinite(created_at)");
                table.HasCheckConstraint("ck_discount_uses_6", "isfinite(transitioned_at)");
                table.HasCheckConstraint("ck_discount_uses_7", "saved_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
            }
        });
        row.HasKey(v => new { v.DiscountId, v.OrderId });
        var discount_id = row.Property(v => v.DiscountId).HasColumnName("discount_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        discount_id.IsRequired();
        discount_id.ValueGeneratedNever();
        if (sqlite) discount_id.UseCollation("NOCASE");
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.IsRequired();
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var discount_revision_id = row.Property(v => v.DiscountRevisionId).HasColumnName("discount_revision_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        discount_revision_id.IsRequired();
        discount_revision_id.ValueGeneratedNever();
        if (sqlite) discount_revision_id.UseCollation("NOCASE");
        var units_covered = row.Property(v => v.UnitsCovered).HasColumnName("units_covered").HasColumnType(sqlite ? "INTEGER" : "integer");
        units_covered.IsRequired();
        var saved_amount = row.Property(v => v.SavedAmount).HasColumnName("saved_amount").HasColumnType(sqlite ? "TEXT" : "numeric");
        saved_amount.IsRequired();
        if (sqlite) saved_amount.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var saved_currency = row.Property(v => v.SavedCurrency).HasColumnName("saved_currency").HasColumnType(sqlite ? "TEXT" : "text");
        saved_currency.IsRequired();
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var transitioned_at = row.Property(v => v.TransitionedAt).HasColumnName("transitioned_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        transitioned_at.IsRequired();
        transitioned_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) transitioned_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.OrderId).IsUnique();
        row.HasOne<Discount>().WithMany().HasForeignKey(v => v.DiscountId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_uses_discount_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_uses_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<DiscountRevision>().WithMany().HasForeignKey(v => v.DiscountRevisionId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_uses_discount_revision_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
