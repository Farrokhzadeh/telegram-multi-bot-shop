using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class ProductConfiguration(bool sqlite) : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> row)
    {
        row.ToTable("products", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_products_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_products_2", "sku IS NULL OR (length(sku) BETWEEN 1 AND 160 AND trim(sku)<>'')");
                table.HasCheckConstraint("ck_products_3", "fulfillment_kind IN ('local','supplier','manual')");
                table.HasCheckConstraint("ck_products_4", "local_first IS NULL OR (local_first IN (0,1))");
                table.HasCheckConstraint("ck_products_5", "availability_policy IN ('immediate','local_preorder')");
                table.HasCheckConstraint("ck_products_6", "active_supplier_source_id IS NULL OR akanti_uuid(active_supplier_source_id)");
                table.HasCheckConstraint("ck_products_7", "revision >= 0");
                table.HasCheckConstraint("ck_products_8", "title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')");
                table.HasCheckConstraint("ck_products_9", "description IS NULL OR (length(description)<=16384)");
                table.HasCheckConstraint("ck_products_10", "icon_ref IS NULL OR (length(icon_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_products_11", "guidance IS NULL OR (length(guidance)<=16384)");
                table.HasCheckConstraint("ck_products_12", "active IS NULL OR (active IN (0,1))");
                table.HasCheckConstraint("ck_products_13", "availability_policy <> 'local_preorder' OR fulfillment_kind='local'");
                table.HasCheckConstraint("ck_products_14", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_products_15", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_products_1", "fulfillment_kind IN ('local','supplier','manual')");
                table.HasCheckConstraint("ck_products_2", "availability_policy IN ('immediate','local_preorder')");
                table.HasCheckConstraint("ck_products_3", "revision >= 0");
                table.HasCheckConstraint("ck_products_4", "availability_policy <> 'local_preorder' OR fulfillment_kind='local'");
                table.HasCheckConstraint("ck_products_5", "isfinite(created_at)");
                table.HasCheckConstraint("ck_products_6", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_products_7", "length(sku) BETWEEN 1 AND 160 AND btrim(sku)<>''");
                table.HasCheckConstraint("ck_products_8", "length(title) BETWEEN 1 AND 160 AND btrim(title)<>''");
                table.HasCheckConstraint("ck_products_9", "length(description)<=16384");
                table.HasCheckConstraint("ck_products_10", "length(icon_ref) BETWEEN 1 AND 1024 AND btrim(icon_ref)<>''");
                table.HasCheckConstraint("ck_products_11", "length(guidance)<=16384");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var sku = row.Property(v => v.Sku).HasColumnName("sku").HasColumnType(sqlite ? "TEXT" : "text");
        sku.HasMaxLength(160);
        var fulfillment_kind = row.Property(v => v.FulfillmentKind).HasColumnName("fulfillment_kind").HasColumnType(sqlite ? "TEXT" : "text");
        fulfillment_kind.IsRequired();
        var local_first = row.Property(v => v.LocalFirst).HasColumnName("local_first").HasColumnType(sqlite ? "INTEGER" : "boolean");
        local_first.IsRequired();
        local_first.HasDefaultValueSql(sqlite ? "true" : "true");
        local_first.HasSentinel(true);
        var availability_policy = row.Property(v => v.AvailabilityPolicy).HasColumnName("availability_policy").HasColumnType(sqlite ? "TEXT" : "text");
        availability_policy.IsRequired();
        var active_supplier_source_id = row.Property(v => v.ActiveSupplierSourceId).HasColumnName("active_supplier_source_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        active_supplier_source_id.ValueGeneratedNever();
        if (sqlite) active_supplier_source_id.UseCollation("NOCASE");
        var revision = row.Property(v => v.Revision).HasColumnName("revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision.IsRequired();
        revision.IsConcurrencyToken();
        revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var title = row.Property(v => v.Title).HasColumnName("title").HasColumnType(sqlite ? "TEXT" : "text");
        title.IsRequired();
        title.HasMaxLength(160);
        var description = row.Property(v => v.Description).HasColumnName("description").HasColumnType(sqlite ? "TEXT" : "text");
        description.HasMaxLength(16384);
        var icon_ref = row.Property(v => v.IconRef).HasColumnName("icon_ref").HasColumnType(sqlite ? "TEXT" : "text");
        icon_ref.HasMaxLength(1024);
        var guidance = row.Property(v => v.Guidance).HasColumnName("guidance").HasColumnType(sqlite ? "TEXT" : "text");
        guidance.HasMaxLength(16384);
        var active = row.Property(v => v.Active).HasColumnName("active").HasColumnType(sqlite ? "INTEGER" : "boolean");
        active.IsRequired();
        active.HasDefaultValueSql(sqlite ? "true" : "true");
        active.HasSentinel(true);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.Sku).IsUnique();
        row.HasOne<ProductSupplierSource>().WithMany().HasForeignKey(v => new { v.Id, v.ActiveSupplierSourceId }).HasPrincipalKey(v => new { v.ProductId, v.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("products_id_active_supplier_source_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
