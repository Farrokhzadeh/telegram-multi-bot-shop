using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class JobConfiguration(bool sqlite) : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> row)
    {
        row.ToTable("jobs", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_jobs_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_jobs_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_jobs_3", "job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')");
                table.HasCheckConstraint("ck_jobs_4", "payment_id IS NULL OR akanti_uuid(payment_id)");
                table.HasCheckConstraint("ck_jobs_5", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_jobs_6", "fulfillment_id IS NULL OR akanti_uuid(fulfillment_id)");
                table.HasCheckConstraint("ck_jobs_7", "campaign_id IS NULL OR akanti_uuid(campaign_id)");
                table.HasCheckConstraint("ck_jobs_8", "discount_id IS NULL OR akanti_uuid(discount_id)");
                table.HasCheckConstraint("ck_jobs_9", "support_message_id IS NULL OR akanti_uuid(support_message_id)");
                table.HasCheckConstraint("ck_jobs_10", "telegram_delivery_id IS NULL OR akanti_uuid(telegram_delivery_id)");
                table.HasCheckConstraint("ck_jobs_11", "product_stock_subscription_id IS NULL OR akanti_uuid(product_stock_subscription_id)");
                table.HasCheckConstraint("ck_jobs_12", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                table.HasCheckConstraint("ck_jobs_13", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_jobs_14", "product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)");
                table.HasCheckConstraint("ck_jobs_15", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                table.HasCheckConstraint("ck_jobs_16", "origin_revision >= 0");
                table.HasCheckConstraint("ck_jobs_17", "status IN ('pending','leased','succeeded','retry','failed','cancelled')");
                table.HasCheckConstraint("ck_jobs_18", "attempts >= 0");
                table.HasCheckConstraint("ck_jobs_19", "locked_by IS NULL OR (length(locked_by) BETWEEN 1 AND 160 AND trim(locked_by)<>'')");
                table.HasCheckConstraint("ck_jobs_20", "dedupe_key IS NULL OR (length(dedupe_key) BETWEEN 1 AND 160 AND trim(dedupe_key)<>'')");
                table.HasCheckConstraint("ck_jobs_21", "last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 160 AND trim(last_error_code)<>'')");
                table.HasCheckConstraint("ck_jobs_22", "(status='leased')=(locked_by IS NOT NULL)");
                table.HasCheckConstraint("ck_jobs_23", "(locked_by IS NULL)=(lease_until IS NULL)");
                table.HasCheckConstraint("ck_jobs_24", "status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL");
                table.HasCheckConstraint("ck_jobs_25", "available_at IS NULL OR akanti_utc_time(available_at)");
                table.HasCheckConstraint("ck_jobs_26", "lease_until IS NULL OR akanti_utc_time(lease_until)");
                table.HasCheckConstraint("ck_jobs_27", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_jobs_28", "completed_at IS NULL OR akanti_utc_time(completed_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_jobs_1", "job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')");
                table.HasCheckConstraint("ck_jobs_2", "origin_revision >= 0");
                table.HasCheckConstraint("ck_jobs_3", "status IN ('pending','leased','succeeded','retry','failed','cancelled')");
                table.HasCheckConstraint("ck_jobs_4", "attempts >= 0");
                table.HasCheckConstraint("ck_jobs_5", "(status='leased')=(locked_by IS NOT NULL)");
                table.HasCheckConstraint("ck_jobs_6", "(locked_by IS NULL)=(lease_until IS NULL)");
                table.HasCheckConstraint("ck_jobs_7", "status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL");
                table.HasCheckConstraint("ck_jobs_8", "isfinite(available_at)");
                table.HasCheckConstraint("ck_jobs_9", "isfinite(lease_until)");
                table.HasCheckConstraint("ck_jobs_10", "isfinite(created_at)");
                table.HasCheckConstraint("ck_jobs_11", "isfinite(completed_at)");
                table.HasCheckConstraint("ck_jobs_12", "length(locked_by) BETWEEN 1 AND 160 AND btrim(locked_by)<>''");
                table.HasCheckConstraint("ck_jobs_13", "length(dedupe_key) BETWEEN 1 AND 160 AND btrim(dedupe_key)<>''");
                table.HasCheckConstraint("ck_jobs_14", "length(last_error_code) BETWEEN 1 AND 160 AND btrim(last_error_code)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var job_kind = row.Property(v => v.JobKind).HasColumnName("job_kind").HasColumnType(sqlite ? "TEXT" : "text");
        job_kind.IsRequired();
        var payment_id = row.Property(v => v.PaymentId).HasColumnName("payment_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        payment_id.ValueGeneratedNever();
        if (sqlite) payment_id.UseCollation("NOCASE");
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var fulfillment_id = row.Property(v => v.FulfillmentId).HasColumnName("fulfillment_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        fulfillment_id.ValueGeneratedNever();
        if (sqlite) fulfillment_id.UseCollation("NOCASE");
        var campaign_id = row.Property(v => v.CampaignId).HasColumnName("campaign_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        campaign_id.ValueGeneratedNever();
        if (sqlite) campaign_id.UseCollation("NOCASE");
        var discount_id = row.Property(v => v.DiscountId).HasColumnName("discount_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        discount_id.ValueGeneratedNever();
        if (sqlite) discount_id.UseCollation("NOCASE");
        var support_message_id = row.Property(v => v.SupportMessageId).HasColumnName("support_message_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        support_message_id.ValueGeneratedNever();
        if (sqlite) support_message_id.UseCollation("NOCASE");
        var telegram_delivery_id = row.Property(v => v.TelegramDeliveryId).HasColumnName("telegram_delivery_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        telegram_delivery_id.ValueGeneratedNever();
        if (sqlite) telegram_delivery_id.UseCollation("NOCASE");
        var product_stock_subscription_id = row.Property(v => v.ProductStockSubscriptionId).HasColumnName("product_stock_subscription_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_stock_subscription_id.ValueGeneratedNever();
        if (sqlite) product_stock_subscription_id.UseCollation("NOCASE");
        var supplier_request_id = row.Property(v => v.SupplierRequestId).HasColumnName("supplier_request_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        supplier_request_id.ValueGeneratedNever();
        if (sqlite) supplier_request_id.UseCollation("NOCASE");
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var product_supplier_source_id = row.Property(v => v.ProductSupplierSourceId).HasColumnName("product_supplier_source_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_supplier_source_id.ValueGeneratedNever();
        if (sqlite) product_supplier_source_id.UseCollation("NOCASE");
        var audit_event_id = row.Property(v => v.AuditEventId).HasColumnName("audit_event_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        audit_event_id.ValueGeneratedNever();
        if (sqlite) audit_event_id.UseCollation("NOCASE");
        var origin_revision = row.Property(v => v.OriginRevision).HasColumnName("origin_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        status.HasDefaultValueSql(sqlite ? "'pending'" : "'pending'");
        var attempts = row.Property(v => v.Attempts).HasColumnName("attempts").HasColumnType(sqlite ? "INTEGER" : "bigint");
        attempts.IsRequired();
        attempts.HasDefaultValueSql(sqlite ? "0" : "0");
        var available_at = row.Property(v => v.AvailableAt).HasColumnName("available_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        available_at.IsRequired();
        available_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) available_at.HasConversion(EfValues.UtcTime);
        var locked_by = row.Property(v => v.LockedBy).HasColumnName("locked_by").HasColumnType(sqlite ? "TEXT" : "text");
        locked_by.HasMaxLength(160);
        var lease_until = row.Property(v => v.LeaseUntil).HasColumnName("lease_until").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) lease_until.HasConversion(EfValues.UtcTime);
        var dedupe_key = row.Property(v => v.DedupeKey).HasColumnName("dedupe_key").HasColumnType(sqlite ? "TEXT" : "text");
        dedupe_key.IsRequired();
        dedupe_key.HasMaxLength(160);
        var last_error_code = row.Property(v => v.LastErrorCode).HasColumnName("last_error_code").HasColumnType(sqlite ? "TEXT" : "text");
        last_error_code.HasMaxLength(160);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var completed_at = row.Property(v => v.CompletedAt).HasColumnName("completed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) completed_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.DedupeKey).IsUnique();
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Payment>().WithMany().HasForeignKey(v => v.PaymentId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_payment_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Fulfillment>().WithMany().HasForeignKey(v => v.FulfillmentId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_fulfillment_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Campaign>().WithMany().HasForeignKey(v => v.CampaignId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_campaign_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Discount>().WithMany().HasForeignKey(v => v.DiscountId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_discount_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupportMessage>().WithMany().HasForeignKey(v => v.SupportMessageId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_support_message_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<TelegramDelivery>().WithMany().HasForeignKey(v => v.TelegramDeliveryId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_telegram_delivery_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<ProductStockSubscription>().WithMany().HasForeignKey(v => v.ProductStockSubscriptionId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_product_stock_subscription_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupplierRequest>().WithMany().HasForeignKey(v => v.SupplierRequestId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_supplier_request_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<ProductSupplierSource>().WithMany().HasForeignKey(v => v.ProductSupplierSourceId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_product_supplier_source_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.AuditEventId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("jobs_audit_event_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.AvailableAt, v.Id }).HasDatabaseName("jobs_claimable").HasFilter(sqlite ? "status IN ('pending','retry')" : "status IN ('pending','retry')");
    }
}
