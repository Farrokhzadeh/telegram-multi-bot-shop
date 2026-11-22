using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class InventoryItemConfiguration(bool sqlite) : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> row)
    {
        row.ToTable("inventory_items", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_inventory_items_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_inventory_items_2", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_inventory_items_3", "length(credential_ciphertext)>0");
                table.HasCheckConstraint("ck_inventory_items_4", "credential_key_id IS NULL OR (length(credential_key_id) BETWEEN 1 AND 160 AND trim(credential_key_id)<>'')");
                table.HasCheckConstraint("ck_inventory_items_5", "length(source_fingerprint)=32");
                table.HasCheckConstraint("ck_inventory_items_6", "origin_kind IN ('local_import','supplier_bonus')");
                table.HasCheckConstraint("ck_inventory_items_7", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                table.HasCheckConstraint("ck_inventory_items_8", "remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')");
                table.HasCheckConstraint("ck_inventory_items_9", "length(supplier_item_key)=32");
                table.HasCheckConstraint("ck_inventory_items_10", "state IN ('available','allocated','retired','admin_held')");
                table.HasCheckConstraint("ck_inventory_items_11", "assigned_fulfillment_id IS NULL OR akanti_uuid(assigned_fulfillment_id)");
                table.HasCheckConstraint("ck_inventory_items_12", "(state='allocated')=(assigned_fulfillment_id IS NOT NULL)");
                table.HasCheckConstraint("ck_inventory_items_13", "(assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)");
                table.HasCheckConstraint("ck_inventory_items_14", "(origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR\n           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')");
                table.HasCheckConstraint("ck_inventory_items_15", "allocated_at IS NULL OR akanti_utc_time(allocated_at)");
                table.HasCheckConstraint("ck_inventory_items_16", "created_at IS NULL OR akanti_utc_time(created_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_inventory_items_1", "octet_length(credential_ciphertext)>0");
                table.HasCheckConstraint("ck_inventory_items_2", "octet_length(source_fingerprint)=32");
                table.HasCheckConstraint("ck_inventory_items_3", "origin_kind IN ('local_import','supplier_bonus')");
                table.HasCheckConstraint("ck_inventory_items_4", "octet_length(supplier_item_key)=32");
                table.HasCheckConstraint("ck_inventory_items_5", "state IN ('available','allocated','retired','admin_held')");
                table.HasCheckConstraint("ck_inventory_items_6", "(state='allocated')=(assigned_fulfillment_id IS NOT NULL)");
                table.HasCheckConstraint("ck_inventory_items_7", "(assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)");
                table.HasCheckConstraint("ck_inventory_items_8", "(origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR\n           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')");
                table.HasCheckConstraint("ck_inventory_items_9", "isfinite(allocated_at)");
                table.HasCheckConstraint("ck_inventory_items_10", "isfinite(created_at)");
                table.HasCheckConstraint("ck_inventory_items_11", "length(credential_key_id) BETWEEN 1 AND 160 AND btrim(credential_key_id)<>''");
                table.HasCheckConstraint("ck_inventory_items_12", "length(remote_item_ref) BETWEEN 1 AND 160 AND btrim(remote_item_ref)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.IsRequired();
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var credential_ciphertext = row.Property(v => v.CredentialCiphertext).HasColumnName("credential_ciphertext").HasColumnType(sqlite ? "BLOB" : "bytea");
        credential_ciphertext.IsRequired();
        var credential_key_id = row.Property(v => v.CredentialKeyId).HasColumnName("credential_key_id").HasColumnType(sqlite ? "TEXT" : "text");
        credential_key_id.IsRequired();
        credential_key_id.HasMaxLength(160);
        var source_fingerprint = row.Property(v => v.SourceFingerprint).HasColumnName("source_fingerprint").HasColumnType(sqlite ? "BLOB" : "bytea");
        source_fingerprint.IsRequired();
        var origin_kind = row.Property(v => v.OriginKind).HasColumnName("origin_kind").HasColumnType(sqlite ? "TEXT" : "text");
        origin_kind.IsRequired();
        var supplier_request_id = row.Property(v => v.SupplierRequestId).HasColumnName("supplier_request_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        supplier_request_id.ValueGeneratedNever();
        if (sqlite) supplier_request_id.UseCollation("NOCASE");
        var remote_item_ref = row.Property(v => v.RemoteItemRef).HasColumnName("remote_item_ref").HasColumnType(sqlite ? "TEXT" : "text");
        remote_item_ref.HasMaxLength(160);
        var supplier_item_key = row.Property(v => v.SupplierItemKey).HasColumnName("supplier_item_key").HasColumnType(sqlite ? "BLOB" : "bytea");
        var state = row.Property(v => v.State).HasColumnName("state").HasColumnType(sqlite ? "TEXT" : "text");
        state.IsRequired();
        state.HasDefaultValueSql(sqlite ? "'available'" : "'available'");
        var assigned_fulfillment_id = row.Property(v => v.AssignedFulfillmentId).HasColumnName("assigned_fulfillment_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        assigned_fulfillment_id.ValueGeneratedNever();
        if (sqlite) assigned_fulfillment_id.UseCollation("NOCASE");
        var allocated_at = row.Property(v => v.AllocatedAt).HasColumnName("allocated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) allocated_at.HasConversion(EfValues.UtcTime);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.SupplierItemKey).IsUnique();
        row.HasIndex(v => v.AssignedFulfillmentId).IsUnique();
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("inventory_items_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupplierRequest>().WithMany().HasForeignKey(v => v.SupplierRequestId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("inventory_items_supplier_request_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Fulfillment>().WithMany().HasForeignKey(v => v.AssignedFulfillmentId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("inventory_items_assigned_fulfillment_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.ProductId, v.CreatedAt, v.Id }).HasDatabaseName("inventory_available_product").HasFilter(sqlite ? "state='available'" : "state='available'");
    }
}
