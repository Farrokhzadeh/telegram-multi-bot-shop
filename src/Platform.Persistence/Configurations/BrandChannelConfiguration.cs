using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class BrandChannelConfiguration(bool sqlite) : IEntityTypeConfiguration<BrandChannel>
{
    public void Configure(EntityTypeBuilder<BrandChannel> row)
    {
        row.ToTable("brand_channels", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_brand_channels_1", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_brand_channels_2", "chat_id < 0");
                table.HasCheckConstraint("ck_brand_channels_3", "title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')");
                table.HasCheckConstraint("ck_brand_channels_4", "invite_link IS NULL OR (length(invite_link) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_brand_channels_5", "required IS NULL OR (required IN (0,1))");
                table.HasCheckConstraint("ck_brand_channels_6", "active IS NULL OR (active IN (0,1))");
                table.HasCheckConstraint("ck_brand_channels_7", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_brand_channels_8", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_brand_channels_1", "chat_id < 0");
                table.HasCheckConstraint("ck_brand_channels_2", "isfinite(created_at)");
                table.HasCheckConstraint("ck_brand_channels_3", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_brand_channels_4", "length(title) BETWEEN 1 AND 160 AND btrim(title)<>''");
                table.HasCheckConstraint("ck_brand_channels_5", "length(invite_link) BETWEEN 1 AND 1024 AND btrim(invite_link)<>''");
            }
        });
        row.HasKey(v => new { v.BrandId, v.ChatId });
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.IsRequired();
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var chat_id = row.Property(v => v.ChatId).HasColumnName("chat_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        chat_id.IsRequired();
        chat_id.ValueGeneratedNever();
        var title = row.Property(v => v.Title).HasColumnName("title").HasColumnType(sqlite ? "TEXT" : "text");
        title.HasMaxLength(160);
        var invite_link = row.Property(v => v.InviteLink).HasColumnName("invite_link").HasColumnType(sqlite ? "TEXT" : "text");
        invite_link.HasMaxLength(1024);
        var required = row.Property(v => v.Required).HasColumnName("required").HasColumnType(sqlite ? "INTEGER" : "boolean");
        required.IsRequired();
        required.HasDefaultValueSql(sqlite ? "true" : "true");
        required.HasSentinel(true);
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
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("brand_channels_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
