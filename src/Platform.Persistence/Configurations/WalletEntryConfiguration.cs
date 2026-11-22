using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class WalletEntryConfiguration(bool sqlite) : IEntityTypeConfiguration<WalletEntry>
{
    public void Configure(EntityTypeBuilder<WalletEntry> row)
    {
        row.ToTable("wallet_entries", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_wallet_entries_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_wallet_entries_2", "user_id IS NULL OR akanti_uuid(user_id)");
                table.HasCheckConstraint("ck_wallet_entries_3", "signed_amount IS NULL OR (akanti_decimal(signed_amount)=1)");
                table.HasCheckConstraint("ck_wallet_entries_4", "akanti_decimal_compare(signed_amount,'0') <> 0");
                table.HasCheckConstraint("ck_wallet_entries_5", "balance_after IS NULL OR (akanti_decimal(balance_after)=1)");
                table.HasCheckConstraint("ck_wallet_entries_6", "akanti_decimal_compare(balance_after,'0') >= 0");
                table.HasCheckConstraint("ck_wallet_entries_7", "posting_version > 0");
                table.HasCheckConstraint("ck_wallet_entries_8", "movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')");
                table.HasCheckConstraint("ck_wallet_entries_9", "is_posting IS NULL OR (is_posting IN (0,1))");
                table.HasCheckConstraint("ck_wallet_entries_10", "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')");
                table.HasCheckConstraint("ck_wallet_entries_11", "order_id IS NULL OR akanti_uuid(order_id)");
                table.HasCheckConstraint("ck_wallet_entries_12", "payment_id IS NULL OR akanti_uuid(payment_id)");
                table.HasCheckConstraint("ck_wallet_entries_13", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                table.HasCheckConstraint("ck_wallet_entries_14", "legacy_import_batch IS NULL OR akanti_uuid(legacy_import_batch)");
                table.HasCheckConstraint("ck_wallet_entries_15", "admin_id IS NULL OR akanti_uuid(admin_id)");
                table.HasCheckConstraint("ck_wallet_entries_16", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                table.HasCheckConstraint("ck_wallet_entries_17", "reason IS NULL OR (length(reason)<=16384)");
                table.HasCheckConstraint("ck_wallet_entries_18", "(is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR\n           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)");
                table.HasCheckConstraint("ck_wallet_entries_19", "movement_kind <> 'purchase_debit' OR akanti_decimal_compare(signed_amount,'0') < 0");
                table.HasCheckConstraint("ck_wallet_entries_20", "movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR akanti_decimal_compare(signed_amount,'0') > 0");
                table.HasCheckConstraint("ck_wallet_entries_21", "(movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='legacy_history' AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND ((order_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND ((payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND ((supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((payment_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0) OR\n           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0)");
                table.HasCheckConstraint("ck_wallet_entries_22", "created_at IS NULL OR akanti_utc_time(created_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_wallet_entries_1", "signed_amount <> 0");
                table.HasCheckConstraint("ck_wallet_entries_2", "balance_after >= 0");
                table.HasCheckConstraint("ck_wallet_entries_3", "posting_version > 0");
                table.HasCheckConstraint("ck_wallet_entries_4", "movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')");
                table.HasCheckConstraint("ck_wallet_entries_5", "(is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR\n           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)");
                table.HasCheckConstraint("ck_wallet_entries_6", "movement_kind <> 'purchase_debit' OR signed_amount < 0");
                table.HasCheckConstraint("ck_wallet_entries_7", "movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR signed_amount > 0");
                table.HasCheckConstraint("ck_wallet_entries_8", "(movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR\n           (movement_kind='legacy_history' AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR\n           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND num_nonnulls(order_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND num_nonnulls(payment_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND num_nonnulls(supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(payment_id,legacy_import_batch)=0) OR\n           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,legacy_import_batch)=0)");
                table.HasCheckConstraint("ck_wallet_entries_9", "isfinite(created_at)");
                table.HasCheckConstraint("ck_wallet_entries_10", "signed_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_wallet_entries_11", "balance_after NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_wallet_entries_12", "length(idempotency_key) BETWEEN 1 AND 160 AND btrim(idempotency_key)<>''");
                table.HasCheckConstraint("ck_wallet_entries_13", "length(reason)<=16384");
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
        var signed_amount = row.Property(v => v.SignedAmount).HasColumnName("signed_amount").HasColumnType(sqlite ? "TEXT" : "numeric");
        signed_amount.IsRequired();
        if (sqlite) signed_amount.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var balance_after = row.Property(v => v.BalanceAfter).HasColumnName("balance_after").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) balance_after.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var posting_version = row.Property(v => v.PostingVersion).HasColumnName("posting_version").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var movement_kind = row.Property(v => v.MovementKind).HasColumnName("movement_kind").HasColumnType(sqlite ? "TEXT" : "text");
        movement_kind.IsRequired();
        var is_posting = row.Property(v => v.IsPosting).HasColumnName("is_posting").HasColumnType(sqlite ? "INTEGER" : "boolean");
        is_posting.IsRequired();
        var idempotency_key = row.Property(v => v.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType(sqlite ? "TEXT" : "text");
        idempotency_key.IsRequired();
        idempotency_key.HasMaxLength(160);
        var order_id = row.Property(v => v.OrderId).HasColumnName("order_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        order_id.ValueGeneratedNever();
        if (sqlite) order_id.UseCollation("NOCASE");
        var payment_id = row.Property(v => v.PaymentId).HasColumnName("payment_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        payment_id.ValueGeneratedNever();
        if (sqlite) payment_id.UseCollation("NOCASE");
        var supplier_request_id = row.Property(v => v.SupplierRequestId).HasColumnName("supplier_request_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        supplier_request_id.ValueGeneratedNever();
        if (sqlite) supplier_request_id.UseCollation("NOCASE");
        var legacy_import_batch = row.Property(v => v.LegacyImportBatch).HasColumnName("legacy_import_batch").HasColumnType(sqlite ? "TEXT" : "uuid");
        legacy_import_batch.ValueGeneratedNever();
        if (sqlite) legacy_import_batch.UseCollation("NOCASE");
        var admin_id = row.Property(v => v.AdminId).HasColumnName("admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        admin_id.ValueGeneratedNever();
        if (sqlite) admin_id.UseCollation("NOCASE");
        var audit_event_id = row.Property(v => v.AuditEventId).HasColumnName("audit_event_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        audit_event_id.ValueGeneratedNever();
        if (sqlite) audit_event_id.UseCollation("NOCASE");
        var reason = row.Property(v => v.Reason).HasColumnName("reason").HasColumnType(sqlite ? "TEXT" : "text");
        reason.IsRequired();
        reason.HasMaxLength(16384);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.UserId, v.IdempotencyKey }).IsUnique();
        row.HasOne<Wallet>().WithMany().HasForeignKey(v => v.UserId).HasPrincipalKey(v => v.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Order>().WithMany().HasForeignKey(v => v.OrderId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_order_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Payment>().WithMany().HasForeignKey(v => v.PaymentId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_payment_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<SupplierRequest>().WithMany().HasForeignKey(v => v.SupplierRequestId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_supplier_request_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.AdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.AuditEventId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallet_entries_audit_event_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.UserId, v.PostingVersion }).HasDatabaseName("wallet_posting_version").IsUnique().HasFilter(sqlite ? "is_posting" : "is_posting");
        row.HasIndex(v => v.UserId).HasDatabaseName("wallet_opening_once").IsUnique().HasFilter(sqlite ? "movement_kind='opening_balance' AND is_posting" : "movement_kind='opening_balance' AND is_posting");
        row.HasIndex(v => v.OrderId).HasDatabaseName("wallet_supplier_resolution_once").IsUnique().HasFilter(sqlite ? "movement_kind='supplier_wallet_credit'" : "movement_kind='supplier_wallet_credit'");
        row.HasIndex(v => v.PaymentId).HasDatabaseName("wallet_payment_credit_once").IsUnique().HasFilter(sqlite ? "movement_kind IN ('wallet_topup','immediate_crypto_shortage_credit','late_crypto_credit')" : "movement_kind IN ('wallet_topup','immediate_crypto_shortage_credit','late_crypto_credit')");
    }
}
