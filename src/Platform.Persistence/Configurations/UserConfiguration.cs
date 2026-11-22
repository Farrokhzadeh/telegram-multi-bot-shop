using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class UserConfiguration(bool sqlite) : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> row)
    {
        row.ToTable("users", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_users_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_users_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_users_3", "telegram_id > 0");
                table.HasCheckConstraint("ck_users_4", "username IS NULL OR (length(username) BETWEEN 1 AND 160 AND trim(username)<>'')");
                table.HasCheckConstraint("ck_users_5", "first_name IS NULL OR (length(first_name) BETWEEN 1 AND 160 AND trim(first_name)<>'')");
                table.HasCheckConstraint("ck_users_6", "last_name IS NULL OR (length(last_name) BETWEEN 1 AND 160 AND trim(last_name)<>'')");
                table.HasCheckConstraint("ck_users_7", "language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')");
                table.HasCheckConstraint("ck_users_8", "flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')");
                table.HasCheckConstraint("ck_users_9", "flow_revision >= 0");
                table.HasCheckConstraint("ck_users_10", "flow_order_id IS NULL OR akanti_uuid(flow_order_id)");
                table.HasCheckConstraint("ck_users_11", "flow_product_id IS NULL OR akanti_uuid(flow_product_id)");
                table.HasCheckConstraint("ck_users_12", "flow_anchor_message_id > 0");
                table.HasCheckConstraint("ck_users_13", "blocked_at IS NULL OR akanti_utc_time(blocked_at)");
                table.HasCheckConstraint("ck_users_14", "banned_at IS NULL OR akanti_utc_time(banned_at)");
                table.HasCheckConstraint("ck_users_15", "marketing_opt_out_at IS NULL OR akanti_utc_time(marketing_opt_out_at)");
                table.HasCheckConstraint("ck_users_16", "first_seen_at IS NULL OR akanti_utc_time(first_seen_at)");
                table.HasCheckConstraint("ck_users_17", "last_seen_at IS NULL OR akanti_utc_time(last_seen_at)");
                table.HasCheckConstraint("ck_users_18", "flow_expires_at IS NULL OR akanti_utc_time(flow_expires_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_users_1", "telegram_id > 0");
                table.HasCheckConstraint("ck_users_2", "flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')");
                table.HasCheckConstraint("ck_users_3", "flow_revision >= 0");
                table.HasCheckConstraint("ck_users_4", "flow_anchor_message_id > 0");
                table.HasCheckConstraint("ck_users_5", "isfinite(blocked_at)");
                table.HasCheckConstraint("ck_users_6", "isfinite(banned_at)");
                table.HasCheckConstraint("ck_users_7", "isfinite(marketing_opt_out_at)");
                table.HasCheckConstraint("ck_users_8", "isfinite(first_seen_at)");
                table.HasCheckConstraint("ck_users_9", "isfinite(last_seen_at)");
                table.HasCheckConstraint("ck_users_10", "isfinite(flow_expires_at)");
                table.HasCheckConstraint("ck_users_11", "length(username) BETWEEN 1 AND 160 AND btrim(username)<>''");
                table.HasCheckConstraint("ck_users_12", "length(first_name) BETWEEN 1 AND 160 AND btrim(first_name)<>''");
                table.HasCheckConstraint("ck_users_13", "length(last_name) BETWEEN 1 AND 160 AND btrim(last_name)<>''");
                table.HasCheckConstraint("ck_users_14", "length(language) BETWEEN 1 AND 160 AND btrim(language)<>''");
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
        var telegram_id = row.Property(v => v.TelegramId).HasColumnName("telegram_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        telegram_id.IsRequired();
        var username = row.Property(v => v.Username).HasColumnName("username").HasColumnType(sqlite ? "TEXT" : "text");
        username.HasMaxLength(160);
        var first_name = row.Property(v => v.FirstName).HasColumnName("first_name").HasColumnType(sqlite ? "TEXT" : "text");
        first_name.HasMaxLength(160);
        var last_name = row.Property(v => v.LastName).HasColumnName("last_name").HasColumnType(sqlite ? "TEXT" : "text");
        last_name.HasMaxLength(160);
        var language = row.Property(v => v.Language).HasColumnName("language").HasColumnType(sqlite ? "TEXT" : "text");
        language.IsRequired();
        language.HasMaxLength(160);
        var blocked_at = row.Property(v => v.BlockedAt).HasColumnName("blocked_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) blocked_at.HasConversion(EfValues.UtcTime);
        var banned_at = row.Property(v => v.BannedAt).HasColumnName("banned_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) banned_at.HasConversion(EfValues.UtcTime);
        var marketing_opt_out_at = row.Property(v => v.MarketingOptOutAt).HasColumnName("marketing_opt_out_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) marketing_opt_out_at.HasConversion(EfValues.UtcTime);
        var first_seen_at = row.Property(v => v.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        first_seen_at.IsRequired();
        first_seen_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) first_seen_at.HasConversion(EfValues.UtcTime);
        var last_seen_at = row.Property(v => v.LastSeenAt).HasColumnName("last_seen_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        last_seen_at.IsRequired();
        last_seen_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) last_seen_at.HasConversion(EfValues.UtcTime);
        var flow_state = row.Property(v => v.FlowState).HasColumnName("flow_state").HasColumnType(sqlite ? "TEXT" : "text");
        flow_state.IsRequired();
        flow_state.HasDefaultValueSql(sqlite ? "'idle'" : "'idle'");
        var flow_revision = row.Property(v => v.FlowRevision).HasColumnName("flow_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        flow_revision.IsRequired();
        flow_revision.IsConcurrencyToken();
        flow_revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var flow_order_id = row.Property(v => v.FlowOrderId).HasColumnName("flow_order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        flow_order_id.ValueGeneratedNever();
        if (sqlite) flow_order_id.UseCollation("NOCASE");
        var flow_product_id = row.Property(v => v.FlowProductId).HasColumnName("flow_product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        flow_product_id.ValueGeneratedNever();
        if (sqlite) flow_product_id.UseCollation("NOCASE");
        var flow_anchor_message_id = row.Property(v => v.FlowAnchorMessageId).HasColumnName("flow_anchor_message_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var flow_expires_at = row.Property(v => v.FlowExpiresAt).HasColumnName("flow_expires_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) flow_expires_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.BrandId, v.TelegramId }).IsUnique();
        row.HasIndex(v => new { v.Id, v.BrandId }).IsUnique();
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("users_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.FlowOrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("users_flow_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.FlowProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("users_flow_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
