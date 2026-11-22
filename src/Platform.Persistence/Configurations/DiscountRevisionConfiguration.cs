using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class DiscountRevisionConfiguration(bool sqlite) : IEntityTypeConfiguration<DiscountRevision>
{
    public void Configure(EntityTypeBuilder<DiscountRevision> row)
    {
        row.ToTable("discount_revisions", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_discount_revisions_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_discount_revisions_2", "discount_id IS NULL OR akanti_uuid(discount_id)");
                table.HasCheckConstraint("ck_discount_revisions_3", "revision_no >= 0");
                table.HasCheckConstraint("ck_discount_revisions_4", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                table.HasCheckConstraint("ck_discount_revisions_5", "percent_off IS NULL OR (akanti_decimal(percent_off)=1)");
                table.HasCheckConstraint("ck_discount_revisions_6", "(akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)");
                table.HasCheckConstraint("ck_discount_revisions_7", "max_total_uses >= 0");
                table.HasCheckConstraint("ck_discount_revisions_8", "max_uses_per_user >= 0");
                table.HasCheckConstraint("ck_discount_revisions_9", "redeemed_count_at_revision >= 0");
                table.HasCheckConstraint("ck_discount_revisions_10", "held_units_at_revision >= 0");
                table.HasCheckConstraint("ck_discount_revisions_11", "active IS NULL OR (active IN (0,1))");
                table.HasCheckConstraint("ck_discount_revisions_12", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_discount_revisions_13", "eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)");
                table.HasCheckConstraint("ck_discount_revisions_14", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                table.HasCheckConstraint("ck_discount_revisions_15", "starts_at IS NULL OR akanti_utc_time(starts_at)");
                table.HasCheckConstraint("ck_discount_revisions_16", "ends_at IS NULL OR akanti_utc_time(ends_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_discount_revisions_1", "revision_no >= 0");
                table.HasCheckConstraint("ck_discount_revisions_2", "percent_off BETWEEN 0 AND 100");
                table.HasCheckConstraint("ck_discount_revisions_3", "max_total_uses >= 0");
                table.HasCheckConstraint("ck_discount_revisions_4", "max_uses_per_user >= 0");
                table.HasCheckConstraint("ck_discount_revisions_5", "redeemed_count_at_revision >= 0");
                table.HasCheckConstraint("ck_discount_revisions_6", "held_units_at_revision >= 0");
                table.HasCheckConstraint("ck_discount_revisions_7", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                table.HasCheckConstraint("ck_discount_revisions_8", "isfinite(starts_at)");
                table.HasCheckConstraint("ck_discount_revisions_9", "isfinite(ends_at)");
                table.HasCheckConstraint("ck_discount_revisions_10", "percent_off NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var discount_id = row.Property(v => v.DiscountId).HasColumnName("discount_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        discount_id.IsRequired();
        discount_id.ValueGeneratedNever();
        if (sqlite) discount_id.UseCollation("NOCASE");
        var revision_no = row.Property(v => v.RevisionNo).HasColumnName("revision_no").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision_no.IsRequired();
        var audit_event_id = row.Property(v => v.AuditEventId).HasColumnName("audit_event_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        audit_event_id.IsRequired();
        audit_event_id.ValueGeneratedNever();
        if (sqlite) audit_event_id.UseCollation("NOCASE");
        var percent_off = row.Property(v => v.PercentOff).HasColumnName("percent_off").HasColumnType(sqlite ? "TEXT" : "numeric");
        percent_off.IsRequired();
        if (sqlite) percent_off.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var max_total_uses = row.Property(v => v.MaxTotalUses).HasColumnName("max_total_uses").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var max_uses_per_user = row.Property(v => v.MaxUsesPerUser).HasColumnName("max_uses_per_user").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var usage_adjustment_units = row.Property(v => v.UsageAdjustmentUnits).HasColumnName("usage_adjustment_units").HasColumnType(sqlite ? "INTEGER" : "bigint");
        usage_adjustment_units.IsRequired();
        var redeemed_count_at_revision = row.Property(v => v.RedeemedCountAtRevision).HasColumnName("redeemed_count_at_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        redeemed_count_at_revision.IsRequired();
        var held_units_at_revision = row.Property(v => v.HeldUnitsAtRevision).HasColumnName("held_units_at_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        held_units_at_revision.IsRequired();
        var starts_at = row.Property(v => v.StartsAt).HasColumnName("starts_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) starts_at.HasConversion(EfValues.UtcTime);
        var ends_at = row.Property(v => v.EndsAt).HasColumnName("ends_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) ends_at.HasConversion(EfValues.UtcTime);
        var active = row.Property(v => v.Active).HasColumnName("active").HasColumnType(sqlite ? "INTEGER" : "boolean");
        active.IsRequired();
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var eligible_user_id = row.Property(v => v.EligibleUserId).HasColumnName("eligible_user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        eligible_user_id.ValueGeneratedNever();
        if (sqlite) eligible_user_id.UseCollation("NOCASE");
        row.HasIndex(v => v.AuditEventId).IsUnique();
        row.HasIndex(v => new { v.DiscountId, v.RevisionNo }).IsUnique();
        row.HasOne<Discount>().WithMany().HasForeignKey(v => v.DiscountId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_revisions_discount_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.AuditEventId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_revisions_audit_event_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_revisions_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<User>().WithMany().HasForeignKey(v => v.EligibleUserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("discount_revisions_eligible_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
