using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class CampaignConfiguration(bool sqlite) : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> row)
    {
        row.ToTable("campaigns", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_campaigns_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_campaigns_2", "batch_id IS NULL OR akanti_uuid(batch_id)");
                table.HasCheckConstraint("ck_campaigns_3", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_campaigns_4", "language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')");
                table.HasCheckConstraint("ck_campaigns_5", "audience_kind IN ('all','active_since','language')");
                table.HasCheckConstraint("ck_campaigns_6", "audience_language IS NULL OR (length(audience_language) BETWEEN 1 AND 160 AND trim(audience_language)<>'')");
                table.HasCheckConstraint("ck_campaigns_7", "message_text IS NULL OR (length(message_text)<=16384)");
                table.HasCheckConstraint("ck_campaigns_8", "media_ref IS NULL OR (length(media_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_campaigns_9", "status IN ('draft','approved','running','completed','cancelled')");
                table.HasCheckConstraint("ck_campaigns_10", "created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)");
                table.HasCheckConstraint("ck_campaigns_11", "((message_text IS NOT NULL) + (media_ref IS NOT NULL))>0");
                table.HasCheckConstraint("ck_campaigns_12", "(audience_kind='active_since')=(active_since IS NOT NULL)");
                table.HasCheckConstraint("ck_campaigns_13", "(audience_kind='language')=(audience_language IS NOT NULL)");
                table.HasCheckConstraint("ck_campaigns_14", "status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL");
                table.HasCheckConstraint("ck_campaigns_15", "active_since IS NULL OR akanti_utc_time(active_since)");
                table.HasCheckConstraint("ck_campaigns_16", "approved_at IS NULL OR akanti_utc_time(approved_at)");
                table.HasCheckConstraint("ck_campaigns_17", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_campaigns_18", "started_at IS NULL OR akanti_utc_time(started_at)");
                table.HasCheckConstraint("ck_campaigns_19", "completed_at IS NULL OR akanti_utc_time(completed_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_campaigns_1", "audience_kind IN ('all','active_since','language')");
                table.HasCheckConstraint("ck_campaigns_2", "status IN ('draft','approved','running','completed','cancelled')");
                table.HasCheckConstraint("ck_campaigns_3", "num_nonnulls(message_text,media_ref)>0");
                table.HasCheckConstraint("ck_campaigns_4", "(audience_kind='active_since')=(active_since IS NOT NULL)");
                table.HasCheckConstraint("ck_campaigns_5", "(audience_kind='language')=(audience_language IS NOT NULL)");
                table.HasCheckConstraint("ck_campaigns_6", "status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL");
                table.HasCheckConstraint("ck_campaigns_7", "isfinite(active_since)");
                table.HasCheckConstraint("ck_campaigns_8", "isfinite(approved_at)");
                table.HasCheckConstraint("ck_campaigns_9", "isfinite(created_at)");
                table.HasCheckConstraint("ck_campaigns_10", "isfinite(started_at)");
                table.HasCheckConstraint("ck_campaigns_11", "isfinite(completed_at)");
                table.HasCheckConstraint("ck_campaigns_12", "length(language) BETWEEN 1 AND 160 AND btrim(language)<>''");
                table.HasCheckConstraint("ck_campaigns_13", "length(audience_language) BETWEEN 1 AND 160 AND btrim(audience_language)<>''");
                table.HasCheckConstraint("ck_campaigns_14", "length(message_text)<=16384");
                table.HasCheckConstraint("ck_campaigns_15", "length(media_ref) BETWEEN 1 AND 1024 AND btrim(media_ref)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var batch_id = row.Property(v => v.BatchId).HasColumnName("batch_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        batch_id.IsRequired();
        batch_id.ValueGeneratedNever();
        if (sqlite) batch_id.UseCollation("NOCASE");
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.IsRequired();
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var language = row.Property(v => v.Language).HasColumnName("language").HasColumnType(sqlite ? "TEXT" : "text");
        language.IsRequired();
        language.HasMaxLength(160);
        var audience_kind = row.Property(v => v.AudienceKind).HasColumnName("audience_kind").HasColumnType(sqlite ? "TEXT" : "text");
        audience_kind.IsRequired();
        var active_since = row.Property(v => v.ActiveSince).HasColumnName("active_since").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) active_since.HasConversion(EfValues.UtcTime);
        var audience_language = row.Property(v => v.AudienceLanguage).HasColumnName("audience_language").HasColumnType(sqlite ? "TEXT" : "text");
        audience_language.HasMaxLength(160);
        var message_text = row.Property(v => v.MessageText).HasColumnName("message_text").HasColumnType(sqlite ? "TEXT" : "text");
        message_text.HasMaxLength(16384);
        var media_ref = row.Property(v => v.MediaRef).HasColumnName("media_ref").HasColumnType(sqlite ? "TEXT" : "text");
        media_ref.HasMaxLength(1024);
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        status.HasDefaultValueSql(sqlite ? "'draft'" : "'draft'");
        var created_by_admin_id = row.Property(v => v.CreatedByAdminId).HasColumnName("created_by_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        created_by_admin_id.IsRequired();
        created_by_admin_id.ValueGeneratedNever();
        if (sqlite) created_by_admin_id.UseCollation("NOCASE");
        var approved_at = row.Property(v => v.ApprovedAt).HasColumnName("approved_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) approved_at.HasConversion(EfValues.UtcTime);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var started_at = row.Property(v => v.StartedAt).HasColumnName("started_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) started_at.HasConversion(EfValues.UtcTime);
        var completed_at = row.Property(v => v.CompletedAt).HasColumnName("completed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) completed_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.BatchId, v.BrandId, v.Language }).IsUnique();
        row.HasIndex(v => new { v.Id, v.BatchId, v.BrandId }).IsUnique();
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("campaigns_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.CreatedByAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("campaigns_created_by_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
