using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence;
using Platform.Persistence.Configurations;
using Platform.Persistence.Entities;

namespace Platform.Migration.Recovery;

internal sealed class RecoveryFulfillmentConfiguration(bool sqlite, bool plaintext = false) : IEntityTypeConfiguration<Fulfillment>
{
    public void Configure(EntityTypeBuilder<Fulfillment> row)
    {
        row.ToTable("fulfillments", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_fulfillments_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_fulfillments_2", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_fulfillments_3", "unit_no > 0");
                table.HasCheckConstraint("ck_fulfillments_4", "kind IN ('local','supplier','manual')");
                table.HasCheckConstraint("ck_fulfillments_5", "status IN ('unfilled','acquired','ready','released','retained_admin')");
                table.HasCheckConstraint("ck_fulfillments_6", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                table.HasCheckConstraint("ck_fulfillments_7", "remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')");
                table.HasCheckConstraint("ck_fulfillments_8", "length(supplier_item_key)=32");
                if (!plaintext) table.HasCheckConstraint("ck_fulfillments_9", "delivered_content_key_id IS NULL OR (length(delivered_content_key_id) BETWEEN 1 AND 160 AND trim(delivered_content_key_id)<>'')");
                table.HasCheckConstraint("ck_fulfillments_10", "content_kind IN ('account','key','code','text','file')");
                table.HasCheckConstraint("ck_fulfillments_11", "resolution_admin_id IS NULL OR akanti_uuid(resolution_admin_id)");
                table.HasCheckConstraint("ck_fulfillments_12", "resolution_note IS NULL OR (length(resolution_note)<=16384)");
                table.HasCheckConstraint("ck_fulfillments_13", "manifest_generation IS NULL OR akanti_uuid(manifest_generation)");
                table.HasCheckConstraint("ck_fulfillments_14", "kind IS NOT NULL OR status='unfilled'");
                table.HasCheckConstraint("ck_fulfillments_15", "(kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)");
                if (!plaintext) table.HasCheckConstraint("ck_fulfillments_16", "((delivered_content IS NOT NULL) + (delivered_content_key_id IS NOT NULL) + (content_kind IS NOT NULL)) IN (0,3)");
                else table.HasCheckConstraint("ck_fulfillments_16", "((delivered_content IS NOT NULL) + (content_kind IS NOT NULL)) IN (0,2)");
                table.HasCheckConstraint("ck_fulfillments_17", "status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL");
                table.HasCheckConstraint("ck_fulfillments_18", "status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_fulfillments_19", "status <> 'retained_admin' OR resolution_admin_id IS NOT NULL");
                table.HasCheckConstraint("ck_fulfillments_20", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_fulfillments_21", "completed_at IS NULL OR akanti_utc_time(completed_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_fulfillments_1", "unit_no > 0");
                table.HasCheckConstraint("ck_fulfillments_2", "kind IN ('local','supplier','manual')");
                table.HasCheckConstraint("ck_fulfillments_3", "status IN ('unfilled','acquired','ready','released','retained_admin')");
                table.HasCheckConstraint("ck_fulfillments_4", "octet_length(supplier_item_key)=32");
                table.HasCheckConstraint("ck_fulfillments_5", "content_kind IN ('account','key','code','text','file')");
                table.HasCheckConstraint("ck_fulfillments_6", "kind IS NOT NULL OR status='unfilled'");
                table.HasCheckConstraint("ck_fulfillments_7", "(kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)");
                if (!plaintext) table.HasCheckConstraint("ck_fulfillments_8", "num_nonnulls(delivered_content,delivered_content_key_id,content_kind) IN (0,3)");
                else table.HasCheckConstraint("ck_fulfillments_8", "num_nonnulls(delivered_content,content_kind) IN (0,2)");
                table.HasCheckConstraint("ck_fulfillments_9", "status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL");
                table.HasCheckConstraint("ck_fulfillments_10", "status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_fulfillments_11", "status <> 'retained_admin' OR resolution_admin_id IS NOT NULL");
                table.HasCheckConstraint("ck_fulfillments_12", "isfinite(created_at)");
                table.HasCheckConstraint("ck_fulfillments_13", "isfinite(completed_at)");
                table.HasCheckConstraint("ck_fulfillments_14", "length(remote_item_ref) BETWEEN 1 AND 160 AND btrim(remote_item_ref)<>''");
                if (!plaintext) table.HasCheckConstraint("ck_fulfillments_15", "length(delivered_content_key_id) BETWEEN 1 AND 160 AND btrim(delivered_content_key_id)<>''");
                table.HasCheckConstraint("ck_fulfillments_16", "length(resolution_note)<=16384");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.IsRequired();
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var unit_no = row.Property(v => v.UnitNo).HasColumnName("unit_no").HasColumnType(sqlite ? "INTEGER" : "integer");
        unit_no.IsRequired();
        var kind = row.Property(v => v.Kind).HasColumnName("kind").HasColumnType(sqlite ? "TEXT" : "text");
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        status.HasDefaultValueSql(sqlite ? "'unfilled'" : "'unfilled'");
        var supplier_request_id = row.Property(v => v.SupplierRequestId).HasColumnName("supplier_request_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        supplier_request_id.ValueGeneratedNever();
        if (sqlite) supplier_request_id.UseCollation("NOCASE");
        var remote_item_ref = row.Property(v => v.RemoteItemRef).HasColumnName("remote_item_ref").HasColumnType(sqlite ? "TEXT" : "text");
        remote_item_ref.HasMaxLength(160);
        var supplier_item_key = row.Property(v => v.SupplierItemKey).HasColumnName("supplier_item_key").HasColumnType(sqlite ? "BLOB" : "bytea");
        var delivered_content = row.Property(v => v.DeliveredContent).HasColumnName("delivered_content").HasColumnType(sqlite ? "BLOB" : "bytea");
        if (plaintext) row.Ignore(v => v.DeliveredContentKeyId);
        else
        {
            var delivered_content_key_id = row.Property(v => v.DeliveredContentKeyId).HasColumnName("delivered_content_key_id").HasColumnType(sqlite ? "TEXT" : "text");
            delivered_content_key_id.HasMaxLength(160);
        }
        var content_kind = row.Property(v => v.ContentKind).HasColumnName("content_kind").HasColumnType(sqlite ? "TEXT" : "text");
        var resolution_admin_id = row.Property(v => v.ResolutionAdminId).HasColumnName("resolution_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        resolution_admin_id.ValueGeneratedNever();
        if (sqlite) resolution_admin_id.UseCollation("NOCASE");
        var resolution_note = row.Property(v => v.ResolutionNote).HasColumnName("resolution_note").HasColumnType(sqlite ? "TEXT" : "text");
        resolution_note.HasMaxLength(16384);
        var manifest_generation = row.Property(v => v.ManifestGeneration).HasColumnName("manifest_generation").HasColumnType(sqlite ? "TEXT" : "uuid");
        manifest_generation.ValueGeneratedNever();
        if (sqlite) manifest_generation.UseCollation("NOCASE");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var completed_at = row.Property(v => v.CompletedAt).HasColumnName("completed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) completed_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.SupplierItemKey).IsUnique();
        row.HasIndex(v => new { v.OrderId, v.UnitNo }).IsUnique();
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fulfillments_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupplierRequest>().WithMany().HasForeignKey(v => v.SupplierRequestId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fulfillments_supplier_request_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.ResolutionAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fulfillments_resolution_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasAnnotation("Akanti:DatabaseForeignKey:fulfillments_order_id_manifest_generation_fk", "{\"columns\":[\"order_id\",\"manifest_generation\"],\"principalTable\":\"orders\",\"principalColumns\":[\"id\",\"delivery_generation\"],\"onDelete\":\"RESTRICT\",\"deferrability\":\"DEFERRABLE INITIALLY IMMEDIATE\"}");
    }
}
