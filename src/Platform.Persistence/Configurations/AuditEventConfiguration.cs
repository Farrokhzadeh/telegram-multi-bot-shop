using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class AuditEventConfiguration(bool sqlite) : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> row)
    {
        row.ToTable("audit_events", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_audit_events_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_audit_events_2", "actor_kind IN ('admin','customer','system')");
                table.HasCheckConstraint("ck_audit_events_3", "actor_admin_id IS NULL OR akanti_uuid(actor_admin_id)");
                table.HasCheckConstraint("ck_audit_events_4", "actor_user_id IS NULL OR akanti_uuid(actor_user_id)");
                table.HasCheckConstraint("ck_audit_events_5", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_audit_events_6", "action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')");
                table.HasCheckConstraint("ck_audit_events_7", "entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')");
                table.HasCheckConstraint("ck_audit_events_8", "entity_id IS NULL OR akanti_uuid(entity_id)");
                table.HasCheckConstraint("ck_audit_events_9", "reason IS NULL OR (length(reason)<=16384)");
                table.HasCheckConstraint("ck_audit_events_10", "previous_revision >= 0");
                table.HasCheckConstraint("ck_audit_events_11", "new_revision >= 0");
                table.HasCheckConstraint("ck_audit_events_12", "correlation_id IS NULL OR akanti_uuid(correlation_id)");
                table.HasCheckConstraint("ck_audit_events_13", "length(command_sha256)=32");
                table.HasCheckConstraint("ck_audit_events_14", "before_channel_enforcement IS NULL OR (before_channel_enforcement IN (0,1))");
                table.HasCheckConstraint("ck_audit_events_15", "after_channel_enforcement IS NULL OR (after_channel_enforcement IN (0,1))");
                table.HasCheckConstraint("ck_audit_events_16", "(actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR\n           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR\n           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)");
                table.HasCheckConstraint("ck_audit_events_17", "(entity_kind='platform')=(entity_id IS NULL)");
                table.HasCheckConstraint("ck_audit_events_18", "(action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR\n           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)");
                table.HasCheckConstraint("ck_audit_events_19", "created_at IS NULL OR akanti_utc_time(created_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_audit_events_1", "actor_kind IN ('admin','customer','system')");
                table.HasCheckConstraint("ck_audit_events_2", "action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')");
                table.HasCheckConstraint("ck_audit_events_3", "entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')");
                table.HasCheckConstraint("ck_audit_events_4", "previous_revision >= 0");
                table.HasCheckConstraint("ck_audit_events_5", "new_revision >= 0");
                table.HasCheckConstraint("ck_audit_events_6", "octet_length(command_sha256)=32");
                table.HasCheckConstraint("ck_audit_events_7", "(actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR\n           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR\n           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)");
                table.HasCheckConstraint("ck_audit_events_8", "(entity_kind='platform')=(entity_id IS NULL)");
                table.HasCheckConstraint("ck_audit_events_9", "(action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR\n           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)");
                table.HasCheckConstraint("ck_audit_events_10", "isfinite(created_at)");
                table.HasCheckConstraint("ck_audit_events_11", "length(reason)<=16384");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var actor_kind = row.Property(v => v.ActorKind).HasColumnName("actor_kind").HasColumnType(sqlite ? "TEXT" : "text");
        actor_kind.IsRequired();
        var actor_admin_id = row.Property(v => v.ActorAdminId).HasColumnName("actor_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        actor_admin_id.ValueGeneratedNever();
        if (sqlite) actor_admin_id.UseCollation("NOCASE");
        var actor_user_id = row.Property(v => v.ActorUserId).HasColumnName("actor_user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        actor_user_id.ValueGeneratedNever();
        if (sqlite) actor_user_id.UseCollation("NOCASE");
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var action = row.Property(v => v.Action).HasColumnName("action").HasColumnType(sqlite ? "TEXT" : "text");
        action.IsRequired();
        var entity_kind = row.Property(v => v.EntityKind).HasColumnName("entity_kind").HasColumnType(sqlite ? "TEXT" : "text");
        entity_kind.IsRequired();
        var entity_id = row.Property(v => v.EntityId).HasColumnName("entity_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        entity_id.ValueGeneratedNever();
        if (sqlite) entity_id.UseCollation("NOCASE");
        var reason = row.Property(v => v.Reason).HasColumnName("reason").HasColumnType(sqlite ? "TEXT" : "text");
        reason.IsRequired();
        reason.HasMaxLength(16384);
        var previous_revision = row.Property(v => v.PreviousRevision).HasColumnName("previous_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var new_revision = row.Property(v => v.NewRevision).HasColumnName("new_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var correlation_id = row.Property(v => v.CorrelationId).HasColumnName("correlation_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        correlation_id.IsRequired();
        correlation_id.ValueGeneratedNever();
        if (sqlite) correlation_id.UseCollation("NOCASE");
        var command_sha256 = row.Property(v => v.CommandSha256).HasColumnName("command_sha256").HasColumnType(sqlite ? "BLOB" : "bytea");
        var before_channel_enforcement = row.Property(v => v.BeforeChannelEnforcement).HasColumnName("before_channel_enforcement").HasColumnType(sqlite ? "INTEGER" : "boolean");
        var after_channel_enforcement = row.Property(v => v.AfterChannelEnforcement).HasColumnName("after_channel_enforcement").HasColumnType(sqlite ? "INTEGER" : "boolean");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.ActorAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("audit_events_actor_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<User>().WithMany().HasForeignKey(v => v.ActorUserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("audit_events_actor_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("audit_events_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
