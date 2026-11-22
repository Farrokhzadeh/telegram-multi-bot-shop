using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class LegacyIdMapConfiguration(bool sqlite) : IEntityTypeConfiguration<LegacyIdMap>
{
    public void Configure(EntityTypeBuilder<LegacyIdMap> row)
    {
        row.ToTable("legacy_id_map", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_legacy_id_map_1", "source_brand IS NULL OR (length(source_brand) BETWEEN 1 AND 160 AND trim(source_brand)<>'')");
                table.HasCheckConstraint("ck_legacy_id_map_2", "source_table IS NULL OR (length(source_table) BETWEEN 1 AND 160 AND trim(source_table)<>'')");
                table.HasCheckConstraint("ck_legacy_id_map_3", "source_key_kind IN ('primary','public_order_reference')");
                table.HasCheckConstraint("ck_legacy_id_map_4", "legacy_id IS NULL OR (length(legacy_id) BETWEEN 1 AND 160 AND trim(legacy_id)<>'')");
                table.HasCheckConstraint("ck_legacy_id_map_5", "target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')");
                table.HasCheckConstraint("ck_legacy_id_map_6", "target_id IS NULL OR akanti_uuid(target_id)");
                table.HasCheckConstraint("ck_legacy_id_map_7", "target_brand_id IS NULL OR akanti_uuid(target_brand_id)");
                table.HasCheckConstraint("ck_legacy_id_map_8", "length(source_fingerprint)=32");
                table.HasCheckConstraint("ck_legacy_id_map_9", "import_batch IS NULL OR akanti_uuid(import_batch)");
                table.HasCheckConstraint("ck_legacy_id_map_10", "import_manifest_ref IS NULL OR (length(import_manifest_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_legacy_id_map_11", "migrated_at IS NULL OR akanti_utc_time(migrated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_legacy_id_map_1", "source_key_kind IN ('primary','public_order_reference')");
                table.HasCheckConstraint("ck_legacy_id_map_2", "target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')");
                table.HasCheckConstraint("ck_legacy_id_map_3", "octet_length(source_fingerprint)=32");
                table.HasCheckConstraint("ck_legacy_id_map_4", "isfinite(migrated_at)");
                table.HasCheckConstraint("ck_legacy_id_map_5", "length(source_brand) BETWEEN 1 AND 160 AND btrim(source_brand)<>''");
                table.HasCheckConstraint("ck_legacy_id_map_6", "length(source_table) BETWEEN 1 AND 160 AND btrim(source_table)<>''");
                table.HasCheckConstraint("ck_legacy_id_map_7", "length(legacy_id) BETWEEN 1 AND 160 AND btrim(legacy_id)<>''");
                table.HasCheckConstraint("ck_legacy_id_map_8", "length(import_manifest_ref) BETWEEN 1 AND 1024 AND btrim(import_manifest_ref)<>''");
            }
        });
        row.HasKey(v => new { v.SourceBrand, v.SourceTable, v.SourceKeyKind, v.LegacyId });
        var source_brand = row.Property(v => v.SourceBrand).HasColumnName("source_brand").HasColumnType(sqlite ? "TEXT" : "text");
        source_brand.IsRequired();
        source_brand.ValueGeneratedNever();
        source_brand.HasMaxLength(160);
        var source_table = row.Property(v => v.SourceTable).HasColumnName("source_table").HasColumnType(sqlite ? "TEXT" : "text");
        source_table.IsRequired();
        source_table.ValueGeneratedNever();
        source_table.HasMaxLength(160);
        var source_key_kind = row.Property(v => v.SourceKeyKind).HasColumnName("source_key_kind").HasColumnType(sqlite ? "TEXT" : "text");
        source_key_kind.IsRequired();
        source_key_kind.ValueGeneratedNever();
        var legacy_id = row.Property(v => v.LegacyId).HasColumnName("legacy_id").HasColumnType(sqlite ? "TEXT" : "text");
        legacy_id.IsRequired();
        legacy_id.ValueGeneratedNever();
        legacy_id.HasMaxLength(160);
        var target_kind = row.Property(v => v.TargetKind).HasColumnName("target_kind").HasColumnType(sqlite ? "TEXT" : "text");
        target_kind.IsRequired();
        var target_id = row.Property(v => v.TargetId).HasColumnName("target_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        target_id.IsRequired();
        target_id.ValueGeneratedNever();
        if (sqlite) target_id.UseCollation("NOCASE");
        var target_brand_id = row.Property(v => v.TargetBrandId).HasColumnName("target_brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        target_brand_id.ValueGeneratedNever();
        if (sqlite) target_brand_id.UseCollation("NOCASE");
        var source_fingerprint = row.Property(v => v.SourceFingerprint).HasColumnName("source_fingerprint").HasColumnType(sqlite ? "BLOB" : "bytea");
        source_fingerprint.IsRequired();
        var import_batch = row.Property(v => v.ImportBatch).HasColumnName("import_batch").HasColumnType(sqlite ? "TEXT" : "uuid");
        import_batch.IsRequired();
        import_batch.ValueGeneratedNever();
        if (sqlite) import_batch.UseCollation("NOCASE");
        var import_manifest_ref = row.Property(v => v.ImportManifestRef).HasColumnName("import_manifest_ref").HasColumnType(sqlite ? "TEXT" : "text");
        import_manifest_ref.IsRequired();
        import_manifest_ref.HasMaxLength(1024);
        var migrated_at = row.Property(v => v.MigratedAt).HasColumnName("migrated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        migrated_at.IsRequired();
        migrated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) migrated_at.HasConversion(EfValues.UtcTime);
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.TargetBrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("legacy_id_map_target_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
