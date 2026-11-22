using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class BotActivityEventConfiguration(bool sqlite) : IEntityTypeConfiguration<BotActivityEvent>
{
    public void Configure(EntityTypeBuilder<BotActivityEvent> row)
    {
        row.ToTable("bot_activity_events", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_bot_activity_events_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_bot_activity_events_2", "user_id IS NULL OR akanti_uuid(user_id)");
                table.HasCheckConstraint("ck_bot_activity_events_3", "event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow','payment')");
                table.HasCheckConstraint("ck_bot_activity_events_4", "source_kind IN ('command','callback','flow','system')");
                table.HasCheckConstraint("ck_bot_activity_events_5", "action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected','topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled','flow_abandoned')");
                table.HasCheckConstraint("ck_bot_activity_events_6", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_bot_activity_events_7", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_bot_activity_events_8", "support_ticket_id IS NULL OR akanti_uuid(support_ticket_id)");
                table.HasCheckConstraint("ck_bot_activity_events_9", "quantity_selected > 0");
                table.HasCheckConstraint("ck_bot_activity_events_10", "flow_revision >= 0");
                table.HasCheckConstraint("ck_bot_activity_events_11", "telegram_update_id >= 0");
                table.HasCheckConstraint("ck_bot_activity_events_12", "outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')");
                table.HasCheckConstraint("ck_bot_activity_events_13", "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')");
                table.HasCheckConstraint("ck_bot_activity_events_14", "(event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR\n           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR\n           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected')) OR\n           (event_type='support' AND action_code IN ('support_open','support_reply')) OR\n           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR\n           (event_type='discount' AND action_code='discounts_open') OR\n           (event_type='policy' AND action_code='policy_check') OR\n           (event_type='flow' AND action_code IN ('flow_expired','flow_abandoned')) OR\n           (event_type='payment' AND action_code IN ('topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled'))");
                table.HasCheckConstraint("ck_bot_activity_events_15", "action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR product_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_16", "action_code NOT IN ('quantity_enter','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR quantity_selected IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_17", "action_code <> 'support_reply' OR support_ticket_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_18", "action_code NOT IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') OR order_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_19", "action_code NOT IN ('flow_expired','flow_abandoned','quantity_rejected') OR flow_revision IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_20", "action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)");
                table.HasCheckConstraint("ck_bot_activity_events_21", "action_code <> 'checkout_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND order_id IS NULL AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL)");
                table.HasCheckConstraint("ck_bot_activity_events_22", "action_code NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR (source_kind='system' AND\n        outcome_code=CASE WHEN action_code='checkout_funded' THEN 'accepted' ELSE 'cancelled' END)");
                table.HasCheckConstraint("ck_bot_activity_events_23", "event_type<>'payment' OR (source_kind='system' AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL\n        AND outcome_code=CASE action_code WHEN 'payment_denied' THEN 'rejected' WHEN 'payment_review' THEN 'blocked' ELSE 'accepted' END)");
                table.HasCheckConstraint("ck_bot_activity_events_24", "occurred_at IS NULL OR akanti_utc_time(occurred_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_bot_activity_events_1", "event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow','payment')");
                table.HasCheckConstraint("ck_bot_activity_events_2", "source_kind IN ('command','callback','flow','system')");
                table.HasCheckConstraint("ck_bot_activity_events_3", "action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected','topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled','flow_abandoned')");
                table.HasCheckConstraint("ck_bot_activity_events_4", "quantity_selected > 0");
                table.HasCheckConstraint("ck_bot_activity_events_5", "flow_revision >= 0");
                table.HasCheckConstraint("ck_bot_activity_events_6", "telegram_update_id >= 0");
                table.HasCheckConstraint("ck_bot_activity_events_7", "outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')");
                table.HasCheckConstraint("ck_bot_activity_events_8", "(event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR\n           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR\n           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected')) OR\n           (event_type='support' AND action_code IN ('support_open','support_reply')) OR\n           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR\n           (event_type='discount' AND action_code='discounts_open') OR\n           (event_type='policy' AND action_code='policy_check') OR\n           (event_type='flow' AND action_code IN ('flow_expired','flow_abandoned')) OR\n           (event_type='payment' AND action_code IN ('topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled'))");
                table.HasCheckConstraint("ck_bot_activity_events_9", "action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR product_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_10", "action_code NOT IN ('quantity_enter','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR quantity_selected IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_11", "action_code <> 'support_reply' OR support_ticket_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_12", "action_code NOT IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') OR order_id IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_13", "action_code NOT IN ('flow_expired','flow_abandoned','quantity_rejected') OR flow_revision IS NOT NULL");
                table.HasCheckConstraint("ck_bot_activity_events_14", "action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)");
                table.HasCheckConstraint("ck_bot_activity_events_15", "action_code <> 'checkout_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND order_id IS NULL AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL)");
                table.HasCheckConstraint("ck_bot_activity_events_16", "action_code NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR (source_kind='system' AND\n        outcome_code=CASE WHEN action_code='checkout_funded' THEN 'accepted' ELSE 'cancelled' END)");
                table.HasCheckConstraint("ck_bot_activity_events_17", "event_type<>'payment' OR (source_kind='system' AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL\n        AND outcome_code=CASE action_code WHEN 'payment_denied' THEN 'rejected' WHEN 'payment_review' THEN 'blocked' ELSE 'accepted' END)");
                table.HasCheckConstraint("ck_bot_activity_events_18", "isfinite(occurred_at)");
                table.HasCheckConstraint("ck_bot_activity_events_19", "length(idempotency_key) BETWEEN 1 AND 160 AND btrim(idempotency_key)<>''");
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
        var event_type = row.Property(v => v.EventType).HasColumnName("event_type").HasColumnType(sqlite ? "TEXT" : "text");
        event_type.IsRequired();
        var source_kind = row.Property(v => v.SourceKind).HasColumnName("source_kind").HasColumnType(sqlite ? "TEXT" : "text");
        source_kind.IsRequired();
        var action_code = row.Property(v => v.ActionCode).HasColumnName("action_code").HasColumnType(sqlite ? "TEXT" : "text");
        action_code.IsRequired();
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var support_ticket_id = row.Property(v => v.SupportTicketId).HasColumnName("support_ticket_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        support_ticket_id.ValueGeneratedNever();
        if (sqlite) support_ticket_id.UseCollation("NOCASE");
        var quantity_selected = row.Property(v => v.QuantitySelected).HasColumnName("quantity_selected").HasColumnType(sqlite ? "INTEGER" : "integer");
        var flow_revision = row.Property(v => v.FlowRevision).HasColumnName("flow_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        flow_revision.IsConcurrencyToken();
        var telegram_update_id = row.Property(v => v.TelegramUpdateId).HasColumnName("telegram_update_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var outcome_code = row.Property(v => v.OutcomeCode).HasColumnName("outcome_code").HasColumnType(sqlite ? "TEXT" : "text");
        outcome_code.IsRequired();
        var idempotency_key = row.Property(v => v.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType(sqlite ? "TEXT" : "text");
        idempotency_key.IsRequired();
        idempotency_key.HasMaxLength(160);
        var occurred_at = row.Property(v => v.OccurredAt).HasColumnName("occurred_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        occurred_at.IsRequired();
        occurred_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) occurred_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.UserId, v.IdempotencyKey }).IsUnique();
        row.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("bot_activity_events_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("bot_activity_events_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("bot_activity_events_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupportTicket>().WithMany().HasForeignKey(v => v.SupportTicketId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("bot_activity_events_support_ticket_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.OccurredAt, v.Id }).HasDatabaseName("activity_retention");
        row.HasIndex(v => new { v.UserId, v.OccurredAt, v.Id }).HasDatabaseName("activity_member_time").IsDescending(false, true, true);
    }
}
