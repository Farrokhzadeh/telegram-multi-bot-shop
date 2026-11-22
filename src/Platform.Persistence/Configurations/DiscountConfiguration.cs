using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class DiscountConfiguration(bool sqlite) : IEntityTypeConfiguration<Discount>
{
    public void Configure(EntityTypeBuilder<Discount> row)
    {
        row.ToTable("discounts", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_discounts_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_discounts_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_discounts_3", "length(code_digest)=32");
                table.HasCheckConstraint("ck_discounts_4", "length(code_ciphertext)>0");
                table.HasCheckConstraint("ck_discounts_5", "code_key_id IS NULL OR (length(code_key_id) BETWEEN 1 AND 160 AND trim(code_key_id)<>'')");
                table.HasCheckConstraint("ck_discounts_6", "display_prefix IS NULL OR (length(display_prefix) BETWEEN 1 AND 160 AND trim(display_prefix)<>'')");
                table.HasCheckConstraint("ck_discounts_7", "issuance_batch_id IS NULL OR akanti_uuid(issuance_batch_id)");
                table.HasCheckConstraint("ck_discounts_8", "kind IN ('prepaid_entitlement','goodwill','promotion')");
                table.HasCheckConstraint("ck_discounts_9", "percent_off IS NULL OR (akanti_decimal(percent_off)=1)");
                table.HasCheckConstraint("ck_discounts_10", "(akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)");
                table.HasCheckConstraint("ck_discounts_11", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_discounts_12", "eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)");
                table.HasCheckConstraint("ck_discounts_13", "source_order_id IS NULL OR akanti_uuid(source_order_id)");
                table.HasCheckConstraint("ck_discounts_14", "max_total_uses >= 0");
                table.HasCheckConstraint("ck_discounts_15", "max_uses_per_user >= 0");
                table.HasCheckConstraint("ck_discounts_16", "redeemed_count >= 0");
                table.HasCheckConstraint("ck_discounts_17", "active IS NULL OR (active IN (0,1))");
                table.HasCheckConstraint("ck_discounts_18", "revision >= 0");
                table.HasCheckConstraint("ck_discounts_19", "created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)");
                table.HasCheckConstraint("ck_discounts_20", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                table.HasCheckConstraint("ck_discounts_21", "(kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR\n           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)");
                table.HasCheckConstraint("ck_discounts_22", "starts_at IS NULL OR akanti_utc_time(starts_at)");
                table.HasCheckConstraint("ck_discounts_23", "ends_at IS NULL OR akanti_utc_time(ends_at)");
                table.HasCheckConstraint("ck_discounts_24", "created_at IS NULL OR akanti_utc_time(created_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_discounts_1", "octet_length(code_digest)=32");
                table.HasCheckConstraint("ck_discounts_2", "octet_length(code_ciphertext)>0");
                table.HasCheckConstraint("ck_discounts_3", "kind IN ('prepaid_entitlement','goodwill','promotion')");
                table.HasCheckConstraint("ck_discounts_4", "percent_off BETWEEN 0 AND 100");
                table.HasCheckConstraint("ck_discounts_5", "max_total_uses >= 0");
                table.HasCheckConstraint("ck_discounts_6", "max_uses_per_user >= 0");
                table.HasCheckConstraint("ck_discounts_7", "redeemed_count >= 0");
                table.HasCheckConstraint("ck_discounts_8", "revision >= 0");
                table.HasCheckConstraint("ck_discounts_9", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                table.HasCheckConstraint("ck_discounts_10", "(kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR\n           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)");
                table.HasCheckConstraint("ck_discounts_11", "isfinite(starts_at)");
                table.HasCheckConstraint("ck_discounts_12", "isfinite(ends_at)");
                table.HasCheckConstraint("ck_discounts_13", "isfinite(created_at)");
                table.HasCheckConstraint("ck_discounts_14", "length(code_key_id) BETWEEN 1 AND 160 AND btrim(code_key_id)<>''");
                table.HasCheckConstraint("ck_discounts_15", "length(display_prefix) BETWEEN 1 AND 160 AND btrim(display_prefix)<>''");
                table.HasCheckConstraint("ck_discounts_16", "percent_off NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.IsRequired();
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var code_digest = row.Property(v => v.CodeDigest).HasColumnName("code_digest").HasColumnType(sqlite ? "BLOB" : "bytea");
        code_digest.IsRequired();
        var code_ciphertext = row.Property(v => v.CodeCiphertext).HasColumnName("code_ciphertext").HasColumnType(sqlite ? "BLOB" : "bytea");
        code_ciphertext.IsRequired();
        var code_key_id = row.Property(v => v.CodeKeyId).HasColumnName("code_key_id").HasColumnType(sqlite ? "TEXT" : "text");
        code_key_id.IsRequired();
        code_key_id.HasMaxLength(160);
        var display_prefix = row.Property(v => v.DisplayPrefix).HasColumnName("display_prefix").HasColumnType(sqlite ? "TEXT" : "text");
        display_prefix.IsRequired();
        display_prefix.HasMaxLength(160);
        var issuance_batch_id = row.Property(v => v.IssuanceBatchId).HasColumnName("issuance_batch_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        issuance_batch_id.IsRequired();
        issuance_batch_id.ValueGeneratedNever();
        if (sqlite) issuance_batch_id.UseCollation("NOCASE");
        var kind = row.Property(v => v.Kind).HasColumnName("kind").HasColumnType(sqlite ? "TEXT" : "text");
        kind.IsRequired();
        var percent_off = row.Property(v => v.PercentOff).HasColumnName("percent_off").HasColumnType(sqlite ? "TEXT" : "numeric");
        percent_off.IsRequired();
        if (sqlite) percent_off.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var eligible_user_id = row.Property(v => v.EligibleUserId).HasColumnName("eligible_user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        eligible_user_id.ValueGeneratedNever();
        if (sqlite) eligible_user_id.UseCollation("NOCASE");
        var source_order_id = row.Property(v => v.SourceOrderId).HasColumnName("source_order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        source_order_id.ValueGeneratedNever();
        if (sqlite) source_order_id.UseCollation("NOCASE");
        var max_total_uses = row.Property(v => v.MaxTotalUses).HasColumnName("max_total_uses").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var max_uses_per_user = row.Property(v => v.MaxUsesPerUser).HasColumnName("max_uses_per_user").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var redeemed_count = row.Property(v => v.RedeemedCount).HasColumnName("redeemed_count").HasColumnType(sqlite ? "INTEGER" : "bigint");
        redeemed_count.IsRequired();
        redeemed_count.HasDefaultValueSql(sqlite ? "0" : "0");
        var usage_adjustment_units = row.Property(v => v.UsageAdjustmentUnits).HasColumnName("usage_adjustment_units").HasColumnType(sqlite ? "INTEGER" : "bigint");
        usage_adjustment_units.IsRequired();
        usage_adjustment_units.HasDefaultValueSql(sqlite ? "0" : "0");
        var starts_at = row.Property(v => v.StartsAt).HasColumnName("starts_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) starts_at.HasConversion(EfValues.UtcTime);
        var ends_at = row.Property(v => v.EndsAt).HasColumnName("ends_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) ends_at.HasConversion(EfValues.UtcTime);
        var active = row.Property(v => v.Active).HasColumnName("active").HasColumnType(sqlite ? "INTEGER" : "boolean");
        active.IsRequired();
        active.HasDefaultValueSql(sqlite ? "true" : "true");
        active.HasSentinel(true);
        var revision = row.Property(v => v.Revision).HasColumnName("revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision.IsRequired();
        revision.IsConcurrencyToken();
        revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var created_by_admin_id = row.Property(v => v.CreatedByAdminId).HasColumnName("created_by_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        created_by_admin_id.ValueGeneratedNever();
        if (sqlite) created_by_admin_id.UseCollation("NOCASE");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.BrandId, v.CodeDigest }).IsUnique();
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discounts_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discounts_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<User>().WithMany().HasForeignKey(v => v.EligibleUserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discounts_eligible_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.SourceOrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discounts_source_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.CreatedByAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discounts_created_by_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => v.SourceOrderId).HasDatabaseName("discount_prepaid_source_once").IsUnique().HasFilter(sqlite ? "kind='prepaid_entitlement'" : "kind='prepaid_entitlement'");
    }
}
