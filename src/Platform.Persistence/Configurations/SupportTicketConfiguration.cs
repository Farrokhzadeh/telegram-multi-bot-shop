using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class SupportTicketConfiguration(bool sqlite) : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> row)
    {
        row.ToTable("support_tickets", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_support_tickets_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_support_tickets_2", "user_id IS NULL OR akanti_uuid(user_id)");
                table.HasCheckConstraint("ck_support_tickets_3", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_support_tickets_4", "category IN ('general','order','payment','preorder_cancellation')");
                table.HasCheckConstraint("ck_support_tickets_5", "status IN ('open','assigned','waiting_customer','closed')");
                table.HasCheckConstraint("ck_support_tickets_6", "source IN ('customer','admin','system')");
                table.HasCheckConstraint("ck_support_tickets_7", "assigned_admin_id IS NULL OR akanti_uuid(assigned_admin_id)");
                table.HasCheckConstraint("ck_support_tickets_8", "(status='closed')=(closed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_support_tickets_9", "category <> 'preorder_cancellation' OR order_id IS NOT NULL");
                table.HasCheckConstraint("ck_support_tickets_10", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_support_tickets_11", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                table.HasCheckConstraint("ck_support_tickets_12", "closed_at IS NULL OR akanti_utc_time(closed_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_support_tickets_1", "category IN ('general','order','payment','preorder_cancellation')");
                table.HasCheckConstraint("ck_support_tickets_2", "status IN ('open','assigned','waiting_customer','closed')");
                table.HasCheckConstraint("ck_support_tickets_3", "source IN ('customer','admin','system')");
                table.HasCheckConstraint("ck_support_tickets_4", "(status='closed')=(closed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_support_tickets_5", "category <> 'preorder_cancellation' OR order_id IS NOT NULL");
                table.HasCheckConstraint("ck_support_tickets_6", "isfinite(created_at)");
                table.HasCheckConstraint("ck_support_tickets_7", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_support_tickets_8", "isfinite(closed_at)");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var user_id = row.Property(v => v.UserId).HasColumnName("user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        user_id.IsRequired();
        user_id.ValueGeneratedNever();
        if (sqlite) user_id.UseCollation("NOCASE");
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var category = row.Property(v => v.Category).HasColumnName("category").HasColumnType(sqlite ? "TEXT" : "text");
        category.IsRequired();
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        status.HasDefaultValueSql(sqlite ? "'open'" : "'open'");
        var source = row.Property(v => v.Source).HasColumnName("source").HasColumnType(sqlite ? "TEXT" : "text");
        source.IsRequired();
        var assigned_admin_id = row.Property(v => v.AssignedAdminId).HasColumnName("assigned_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        assigned_admin_id.ValueGeneratedNever();
        if (sqlite) assigned_admin_id.UseCollation("NOCASE");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        var closed_at = row.Property(v => v.ClosedAt).HasColumnName("closed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) closed_at.HasConversion(EfValues.UtcTime);
        row.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("support_tickets_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("support_tickets_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.AssignedAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("support_tickets_assigned_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
