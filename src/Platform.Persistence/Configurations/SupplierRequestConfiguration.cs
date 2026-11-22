using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class SupplierRequestConfiguration(bool sqlite) : IEntityTypeConfiguration<SupplierRequest>
{
    public void Configure(EntityTypeBuilder<SupplierRequest> row)
    {
        row.ToTable("supplier_requests", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_supplier_requests_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_supplier_requests_2", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_supplier_requests_3", "requested_quantity > 0");
                table.HasCheckConstraint("ck_supplier_requests_4", "product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)");
                table.HasCheckConstraint("ck_supplier_requests_5", "source_revision > 0");
                table.HasCheckConstraint("ck_supplier_requests_6", "provider_code IS NULL OR (length(provider_code) BETWEEN 1 AND 160 AND trim(provider_code)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_7", "account_ref IS NULL OR (length(account_ref) BETWEEN 1 AND 160 AND trim(account_ref)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_8", "external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_9", "external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_10", "local_intent_key IS NULL OR (length(local_intent_key) BETWEEN 1 AND 160 AND trim(local_intent_key)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_11", "remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_12", "length(request_sha256)=32");
                table.HasCheckConstraint("ck_supplier_requests_13", "status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')");
                table.HasCheckConstraint("ck_supplier_requests_14", "revision >= 0");
                table.HasCheckConstraint("ck_supplier_requests_15", "review_code IS NULL OR (review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'))");
                table.HasCheckConstraint("ck_supplier_requests_16", "evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_supplier_requests_17", "cost_baseline_amount IS NULL OR (akanti_decimal(cost_baseline_amount)=1)");
                table.HasCheckConstraint("ck_supplier_requests_18", "akanti_decimal_compare(cost_baseline_amount,'0') > 0");
                table.HasCheckConstraint("ck_supplier_requests_19", "cost_currency IS NULL OR (length(cost_currency) BETWEEN 1 AND 160 AND trim(cost_currency)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_20", "cost_quote_ref IS NULL OR (length(cost_quote_ref) BETWEEN 1 AND 160 AND trim(cost_quote_ref)<>'')");
                table.HasCheckConstraint("ck_supplier_requests_21", "approved_cost_ceiling IS NULL OR (akanti_decimal(approved_cost_ceiling)=1)");
                table.HasCheckConstraint("ck_supplier_requests_22", "akanti_decimal_compare(approved_cost_ceiling,'0') > 0");
                table.HasCheckConstraint("ck_supplier_requests_23", "approval_audit_id IS NULL OR akanti_uuid(approval_audit_id)");
                table.HasCheckConstraint("ck_supplier_requests_24", "((cost_baseline_amount IS NOT NULL) + (cost_currency IS NOT NULL) + (cost_quote_ref IS NOT NULL) + (cost_observed_at IS NOT NULL)) IN (0,4)");
                table.HasCheckConstraint("ck_supplier_requests_25", "((approved_cost_ceiling IS NOT NULL) + (approval_audit_id IS NOT NULL)) IN (0,2)");
                table.HasCheckConstraint("ck_supplier_requests_26", "status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))");
                table.HasCheckConstraint("ck_supplier_requests_27", "status <> 'blocked' OR review_code IS NOT NULL");
                table.HasCheckConstraint("ck_supplier_requests_28", "next_check_at IS NULL OR akanti_utc_time(next_check_at)");
                table.HasCheckConstraint("ck_supplier_requests_29", "cost_observed_at IS NULL OR akanti_utc_time(cost_observed_at)");
                table.HasCheckConstraint("ck_supplier_requests_30", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_supplier_requests_31", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_supplier_requests_1", "requested_quantity > 0");
                table.HasCheckConstraint("ck_supplier_requests_2", "source_revision > 0");
                table.HasCheckConstraint("ck_supplier_requests_3", "octet_length(request_sha256)=32");
                table.HasCheckConstraint("ck_supplier_requests_4", "status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')");
                table.HasCheckConstraint("ck_supplier_requests_5", "revision >= 0");
                table.HasCheckConstraint("ck_supplier_requests_6", "cost_baseline_amount > 0");
                table.HasCheckConstraint("ck_supplier_requests_7", "approved_cost_ceiling > 0");
                table.HasCheckConstraint("ck_supplier_requests_8", "num_nonnulls(cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at) IN (0,4)");
                table.HasCheckConstraint("ck_supplier_requests_9", "num_nonnulls(approved_cost_ceiling,approval_audit_id) IN (0,2)");
                table.HasCheckConstraint("ck_supplier_requests_10", "status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))");
                table.HasCheckConstraint("ck_supplier_requests_11", "status <> 'blocked' OR review_code IS NOT NULL");
                table.HasCheckConstraint("ck_supplier_requests_12", "isfinite(next_check_at)");
                table.HasCheckConstraint("ck_supplier_requests_13", "isfinite(cost_observed_at)");
                table.HasCheckConstraint("ck_supplier_requests_14", "isfinite(created_at)");
                table.HasCheckConstraint("ck_supplier_requests_15", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_supplier_requests_16", "length(provider_code) BETWEEN 1 AND 160 AND btrim(provider_code)<>''");
                table.HasCheckConstraint("ck_supplier_requests_17", "length(account_ref) BETWEEN 1 AND 160 AND btrim(account_ref)<>''");
                table.HasCheckConstraint("ck_supplier_requests_18", "length(external_product_id) BETWEEN 1 AND 160 AND btrim(external_product_id)<>''");
                table.HasCheckConstraint("ck_supplier_requests_19", "length(external_variant_id) BETWEEN 1 AND 160 AND btrim(external_variant_id)<>''");
                table.HasCheckConstraint("ck_supplier_requests_20", "length(local_intent_key) BETWEEN 1 AND 160 AND btrim(local_intent_key)<>''");
                table.HasCheckConstraint("ck_supplier_requests_21", "length(remote_ref) BETWEEN 1 AND 160 AND btrim(remote_ref)<>''");
                table.HasCheckConstraint("ck_supplier_requests_22", "review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery')");
                table.HasCheckConstraint("ck_supplier_requests_23", "length(evidence_ref) BETWEEN 1 AND 1024 AND btrim(evidence_ref)<>''");
                table.HasCheckConstraint("ck_supplier_requests_24", "cost_baseline_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_supplier_requests_25", "length(cost_currency) BETWEEN 1 AND 160 AND btrim(cost_currency)<>''");
                table.HasCheckConstraint("ck_supplier_requests_26", "length(cost_quote_ref) BETWEEN 1 AND 160 AND btrim(cost_quote_ref)<>''");
                table.HasCheckConstraint("ck_supplier_requests_27", "approved_cost_ceiling NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
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
        var requested_quantity = row.Property(v => v.RequestedQuantity).HasColumnName("requested_quantity").HasColumnType(sqlite ? "INTEGER" : "integer");
        requested_quantity.IsRequired();
        var product_supplier_source_id = row.Property(v => v.ProductSupplierSourceId).HasColumnName("product_supplier_source_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_supplier_source_id.ValueGeneratedNever();
        if (sqlite) product_supplier_source_id.UseCollation("NOCASE");
        var source_revision = row.Property(v => v.SourceRevision).HasColumnName("source_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var provider_code = row.Property(v => v.ProviderCode).HasColumnName("provider_code").HasColumnType(sqlite ? "TEXT" : "text");
        provider_code.HasMaxLength(160);
        var account_ref = row.Property(v => v.AccountRef).HasColumnName("account_ref").HasColumnType(sqlite ? "TEXT" : "text");
        account_ref.HasMaxLength(160);
        var external_product_id = row.Property(v => v.ExternalProductId).HasColumnName("external_product_id").HasColumnType(sqlite ? "TEXT" : "text");
        external_product_id.HasMaxLength(160);
        var external_variant_id = row.Property(v => v.ExternalVariantId).HasColumnName("external_variant_id").HasColumnType(sqlite ? "TEXT" : "text");
        external_variant_id.HasMaxLength(160);
        var local_intent_key = row.Property(v => v.LocalIntentKey).HasColumnName("local_intent_key").HasColumnType(sqlite ? "TEXT" : "text");
        local_intent_key.IsRequired();
        local_intent_key.HasMaxLength(160);
        var remote_ref = row.Property(v => v.RemoteRef).HasColumnName("remote_ref").HasColumnType(sqlite ? "TEXT" : "text");
        remote_ref.HasMaxLength(160);
        var request_sha256 = row.Property(v => v.RequestSha256).HasColumnName("request_sha256").HasColumnType(sqlite ? "BLOB" : "bytea");
        request_sha256.IsRequired();
        var status = row.Property(v => v.Status).HasColumnName("status").HasColumnType(sqlite ? "TEXT" : "text");
        status.IsRequired();
        status.HasDefaultValueSql(sqlite ? "'blocked'" : "'blocked'");
        var revision = row.Property(v => v.Revision).HasColumnName("revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision.IsRequired();
        revision.IsConcurrencyToken();
        revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var review_code = row.Property(v => v.ReviewCode).HasColumnName("review_code").HasColumnType(sqlite ? "TEXT" : "text");
        var next_check_at = row.Property(v => v.NextCheckAt).HasColumnName("next_check_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) next_check_at.HasConversion(EfValues.UtcTime);
        var evidence_ref = row.Property(v => v.EvidenceRef).HasColumnName("evidence_ref").HasColumnType(sqlite ? "TEXT" : "text");
        evidence_ref.HasMaxLength(1024);
        var cost_baseline_amount = row.Property(v => v.CostBaselineAmount).HasColumnName("cost_baseline_amount").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) cost_baseline_amount.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var cost_currency = row.Property(v => v.CostCurrency).HasColumnName("cost_currency").HasColumnType(sqlite ? "TEXT" : "text");
        cost_currency.HasMaxLength(160);
        var cost_quote_ref = row.Property(v => v.CostQuoteRef).HasColumnName("cost_quote_ref").HasColumnType(sqlite ? "TEXT" : "text");
        cost_quote_ref.HasMaxLength(160);
        var cost_observed_at = row.Property(v => v.CostObservedAt).HasColumnName("cost_observed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) cost_observed_at.HasConversion(EfValues.UtcTime);
        var approved_cost_ceiling = row.Property(v => v.ApprovedCostCeiling).HasColumnName("approved_cost_ceiling").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) approved_cost_ceiling.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var approval_audit_id = row.Property(v => v.ApprovalAuditId).HasColumnName("approval_audit_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        approval_audit_id.ValueGeneratedNever();
        if (sqlite) approval_audit_id.UseCollation("NOCASE");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.LocalIntentKey).IsUnique();
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_requests_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<ProductSupplierSource>().WithMany().HasForeignKey(v => v.ProductSupplierSourceId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_requests_product_supplier_source_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.ApprovalAuditId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_requests_approval_audit_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => v.OrderId).HasDatabaseName("supplier_order_unresolved").IsUnique().HasFilter(sqlite ? "status NOT IN ('failed','resolved','succeeded')" : "status NOT IN ('failed','resolved','succeeded')");
    }
}
