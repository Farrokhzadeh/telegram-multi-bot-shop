using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class SupplierRequestAttemptConfiguration(bool sqlite) : IEntityTypeConfiguration<SupplierRequestAttempt>
{
    public void Configure(EntityTypeBuilder<SupplierRequestAttempt> row)
    {
        row.ToTable("supplier_request_attempts", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_supplier_request_attempts_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_supplier_request_attempts_2", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                table.HasCheckConstraint("ck_supplier_request_attempts_3", "attempt_no > 0");
                table.HasCheckConstraint("ck_supplier_request_attempts_4", "operation IN ('submit','status_lookup','export')");
                table.HasCheckConstraint("ck_supplier_request_attempts_5", "outcome IN ('in_flight','succeeded','failed','timeout','unknown')");
                table.HasCheckConstraint("ck_supplier_request_attempts_6", "parent_submit_attempt_id IS NULL OR akanti_uuid(parent_submit_attempt_id)");
                table.HasCheckConstraint("ck_supplier_request_attempts_7", "admin_authorization_audit_id IS NULL OR akanti_uuid(admin_authorization_audit_id)");
                table.HasCheckConstraint("ck_supplier_request_attempts_8", "purchase_idempotency_key IS NULL OR (length(purchase_idempotency_key) BETWEEN 1 AND 160 AND trim(purchase_idempotency_key)<>'')");
                table.HasCheckConstraint("ck_supplier_request_attempts_9", "max_charge_amount IS NULL OR (akanti_decimal(max_charge_amount)=1)");
                table.HasCheckConstraint("ck_supplier_request_attempts_10", "akanti_decimal_compare(max_charge_amount,'0') > 0");
                table.HasCheckConstraint("ck_supplier_request_attempts_11", "http_status BETWEEN 100 AND 599");
                table.HasCheckConstraint("ck_supplier_request_attempts_12", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 160 AND trim(error_code)<>'')");
                table.HasCheckConstraint("ck_supplier_request_attempts_13", "remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')");
                table.HasCheckConstraint("ck_supplier_request_attempts_14", "evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_supplier_request_attempts_15", "(outcome='in_flight')=(finished_at IS NULL)");
                table.HasCheckConstraint("ck_supplier_request_attempts_16", "finished_at IS NULL OR finished_at >= started_at");
                table.HasCheckConstraint("ck_supplier_request_attempts_17", "(operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR\n           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)");
                table.HasCheckConstraint("ck_supplier_request_attempts_18", "started_at IS NULL OR akanti_utc_time(started_at)");
                table.HasCheckConstraint("ck_supplier_request_attempts_19", "finished_at IS NULL OR akanti_utc_time(finished_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_supplier_request_attempts_1", "attempt_no > 0");
                table.HasCheckConstraint("ck_supplier_request_attempts_2", "operation IN ('submit','status_lookup','export')");
                table.HasCheckConstraint("ck_supplier_request_attempts_3", "outcome IN ('in_flight','succeeded','failed','timeout','unknown')");
                table.HasCheckConstraint("ck_supplier_request_attempts_4", "max_charge_amount > 0");
                table.HasCheckConstraint("ck_supplier_request_attempts_5", "http_status BETWEEN 100 AND 599");
                table.HasCheckConstraint("ck_supplier_request_attempts_6", "(outcome='in_flight')=(finished_at IS NULL)");
                table.HasCheckConstraint("ck_supplier_request_attempts_7", "finished_at IS NULL OR finished_at >= started_at");
                table.HasCheckConstraint("ck_supplier_request_attempts_8", "(operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR\n           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)");
                table.HasCheckConstraint("ck_supplier_request_attempts_9", "isfinite(started_at)");
                table.HasCheckConstraint("ck_supplier_request_attempts_10", "isfinite(finished_at)");
                table.HasCheckConstraint("ck_supplier_request_attempts_11", "length(purchase_idempotency_key) BETWEEN 1 AND 160 AND btrim(purchase_idempotency_key)<>''");
                table.HasCheckConstraint("ck_supplier_request_attempts_12", "max_charge_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_supplier_request_attempts_13", "length(error_code) BETWEEN 1 AND 160 AND btrim(error_code)<>''");
                table.HasCheckConstraint("ck_supplier_request_attempts_14", "length(remote_ref) BETWEEN 1 AND 160 AND btrim(remote_ref)<>''");
                table.HasCheckConstraint("ck_supplier_request_attempts_15", "length(evidence_ref) BETWEEN 1 AND 1024 AND btrim(evidence_ref)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var supplier_request_id = row.Property(v => v.SupplierRequestId).HasColumnName("supplier_request_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        supplier_request_id.IsRequired();
        supplier_request_id.ValueGeneratedNever();
        if (sqlite) supplier_request_id.UseCollation("NOCASE");
        var attempt_no = row.Property(v => v.AttemptNo).HasColumnName("attempt_no").HasColumnType(sqlite ? "INTEGER" : "bigint");
        attempt_no.IsRequired();
        var operation = row.Property(v => v.Operation).HasColumnName("operation").HasColumnType(sqlite ? "TEXT" : "text");
        operation.IsRequired();
        var outcome = row.Property(v => v.Outcome).HasColumnName("outcome").HasColumnType(sqlite ? "TEXT" : "text");
        outcome.IsRequired();
        outcome.HasDefaultValueSql(sqlite ? "'in_flight'" : "'in_flight'");
        var parent_submit_attempt_id = row.Property(v => v.ParentSubmitAttemptId).HasColumnName("parent_submit_attempt_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        parent_submit_attempt_id.ValueGeneratedNever();
        if (sqlite) parent_submit_attempt_id.UseCollation("NOCASE");
        var admin_authorization_audit_id = row.Property(v => v.AdminAuthorizationAuditId).HasColumnName("admin_authorization_audit_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        admin_authorization_audit_id.ValueGeneratedNever();
        if (sqlite) admin_authorization_audit_id.UseCollation("NOCASE");
        var purchase_idempotency_key = row.Property(v => v.PurchaseIdempotencyKey).HasColumnName("purchase_idempotency_key").HasColumnType(sqlite ? "TEXT" : "text");
        purchase_idempotency_key.HasMaxLength(160);
        var max_charge_amount = row.Property(v => v.MaxChargeAmount).HasColumnName("max_charge_amount").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) max_charge_amount.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var http_status = row.Property(v => v.HttpStatus).HasColumnName("http_status").HasColumnType(sqlite ? "INTEGER" : "integer");
        var error_code = row.Property(v => v.ErrorCode).HasColumnName("error_code").HasColumnType(sqlite ? "TEXT" : "text");
        error_code.HasMaxLength(160);
        var remote_ref = row.Property(v => v.RemoteRef).HasColumnName("remote_ref").HasColumnType(sqlite ? "TEXT" : "text");
        remote_ref.HasMaxLength(160);
        var evidence_ref = row.Property(v => v.EvidenceRef).HasColumnName("evidence_ref").HasColumnType(sqlite ? "TEXT" : "text");
        evidence_ref.HasMaxLength(1024);
        var started_at = row.Property(v => v.StartedAt).HasColumnName("started_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        started_at.IsRequired();
        started_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) started_at.HasConversion(EfValues.UtcTime);
        var finished_at = row.Property(v => v.FinishedAt).HasColumnName("finished_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) finished_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.SupplierRequestId, v.AttemptNo }).IsUnique();
        row.HasOne<SupplierRequest>().WithMany().HasForeignKey(v => v.SupplierRequestId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_request_attempts_supplier_request_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupplierRequestAttempt>().WithMany().HasForeignKey(v => v.ParentSubmitAttemptId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_request_attempts_parent_submit_attempt_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.AdminAuthorizationAuditId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("supplier_request_attempts_admin_authorization_audit_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
