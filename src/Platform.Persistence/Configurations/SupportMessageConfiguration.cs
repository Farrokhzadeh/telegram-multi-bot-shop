using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class SupportMessageConfiguration(bool sqlite) : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> row)
    {
        row.ToTable("support_messages", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_support_messages_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_support_messages_2", "ticket_id IS NULL OR akanti_uuid(ticket_id)");
                table.HasCheckConstraint("ck_support_messages_3", "sender_kind IN ('customer','admin','system')");
                table.HasCheckConstraint("ck_support_messages_4", "sender_admin_id IS NULL OR akanti_uuid(sender_admin_id)");
                table.HasCheckConstraint("ck_support_messages_5", "sender_telegram_id > 0");
                table.HasCheckConstraint("ck_support_messages_7", "asset_ref IS NULL OR (length(asset_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_support_messages_8", "telegram_message_id > 0");
                table.HasCheckConstraint("ck_support_messages_9", "(sender_kind='admin')=(sender_admin_id IS NOT NULL)");
                table.HasCheckConstraint("ck_support_messages_11", "((body_content IS NOT NULL) + (asset_ref IS NOT NULL))>0");
                table.HasCheckConstraint("ck_support_messages_12", "created_at IS NULL OR akanti_utc_time(created_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_support_messages_1", "sender_kind IN ('customer','admin','system')");
                table.HasCheckConstraint("ck_support_messages_2", "sender_telegram_id > 0");
                table.HasCheckConstraint("ck_support_messages_3", "telegram_message_id > 0");
                table.HasCheckConstraint("ck_support_messages_4", "(sender_kind='admin')=(sender_admin_id IS NOT NULL)");
                table.HasCheckConstraint("ck_support_messages_6", "num_nonnulls(body_content,asset_ref)>0");
                table.HasCheckConstraint("ck_support_messages_7", "isfinite(created_at)");
                table.HasCheckConstraint("ck_support_messages_9", "length(asset_ref) BETWEEN 1 AND 1024 AND btrim(asset_ref)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var ticket_id = row.Property(v => v.TicketId).HasColumnName("ticket_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        ticket_id.IsRequired();
        ticket_id.ValueGeneratedNever();
        if (sqlite) ticket_id.UseCollation("NOCASE");
        var sender_kind = row.Property(v => v.SenderKind).HasColumnName("sender_kind").HasColumnType(sqlite ? "TEXT" : "text");
        sender_kind.IsRequired();
        var sender_admin_id = row.Property(v => v.SenderAdminId).HasColumnName("sender_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        sender_admin_id.ValueGeneratedNever();
        if (sqlite) sender_admin_id.UseCollation("NOCASE");
        var sender_telegram_id = row.Property(v => v.SenderTelegramId).HasColumnName("sender_telegram_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        row.Ignore(v => v.BodyCiphertext);
        var body_content = row.Property(v => v.BodyContent).HasColumnName("body_content").HasColumnType(sqlite ? "BLOB" : "bytea");
        row.Ignore(v => v.BodyKeyId);
        var asset_ref = row.Property(v => v.AssetRef).HasColumnName("asset_ref").HasColumnType(sqlite ? "TEXT" : "text");
        asset_ref.HasMaxLength(1024);
        var telegram_message_id = row.Property(v => v.TelegramMessageId).HasColumnName("telegram_message_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        row.HasOne<SupportTicket>().WithMany().HasForeignKey(v => v.TicketId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("support_messages_ticket_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.SenderAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("support_messages_sender_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.TicketId, v.TelegramMessageId }).HasDatabaseName("support_external_message_once").IsUnique().HasFilter(sqlite ? "telegram_message_id IS NOT NULL" : "telegram_message_id IS NOT NULL");
    }
}
