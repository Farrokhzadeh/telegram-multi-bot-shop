using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class AdminConfiguration(bool sqlite) : IEntityTypeConfiguration<Admin>
{
    public void Configure(EntityTypeBuilder<Admin> row)
    {
        row.ToTable("admins", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_admins_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_admins_2", "login IS NULL OR (length(login) BETWEEN 1 AND 160 AND trim(login)<>'')");
                table.HasCheckConstraint("ck_admins_3", "authentication_mode IN ('password','external')");
                table.HasCheckConstraint("ck_admins_4", "password_verifier IS NULL OR (length(password_verifier) BETWEEN 1 AND 160 AND trim(password_verifier)<>'')");
                table.HasCheckConstraint("ck_admins_5", "external_subject IS NULL OR (length(external_subject) BETWEEN 1 AND 160 AND trim(external_subject)<>'')");
                table.HasCheckConstraint("ck_admins_6", "auth_version >= 0");
                table.HasCheckConstraint("ck_admins_7", "telegram_user_id > 0");
                table.HasCheckConstraint("ck_admins_8", "public_contact_handle IS NULL OR (length(public_contact_handle) BETWEEN 1 AND 160 AND trim(public_contact_handle)<>'')");
                table.HasCheckConstraint("ck_admins_9", "telegram_verified_by_admin_id IS NULL OR akanti_uuid(telegram_verified_by_admin_id)");
                table.HasCheckConstraint("ck_admins_10", "telegram_verification_audit_id IS NULL OR akanti_uuid(telegram_verification_audit_id)");
                table.HasCheckConstraint("ck_admins_11", "length(binding_challenge_hash)=32");
                table.HasCheckConstraint("ck_admins_12", "binding_candidate_telegram_id > 0");
                table.HasCheckConstraint("ck_admins_13", "(authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR\n           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)");
                table.HasCheckConstraint("ck_admins_14", "((telegram_user_id IS NOT NULL) + (telegram_verified_by_admin_id IS NOT NULL) + (telegram_verified_at IS NOT NULL) + (telegram_verification_audit_id IS NOT NULL)) IN (0,4)");
                table.HasCheckConstraint("ck_admins_15", "public_contact_handle IS NULL OR telegram_user_id IS NOT NULL");
                table.HasCheckConstraint("ck_admins_16", "((binding_challenge_hash IS NOT NULL) + (binding_candidate_telegram_id IS NOT NULL) + (binding_expires_at IS NOT NULL)) IN (0,3)");
                table.HasCheckConstraint("ck_admins_17", "disabled_at IS NULL OR akanti_utc_time(disabled_at)");
                table.HasCheckConstraint("ck_admins_18", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_admins_19", "last_login_at IS NULL OR akanti_utc_time(last_login_at)");
                table.HasCheckConstraint("ck_admins_20", "telegram_verified_at IS NULL OR akanti_utc_time(telegram_verified_at)");
                table.HasCheckConstraint("ck_admins_21", "binding_expires_at IS NULL OR akanti_utc_time(binding_expires_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_admins_1", "authentication_mode IN ('password','external')");
                table.HasCheckConstraint("ck_admins_2", "auth_version >= 0");
                table.HasCheckConstraint("ck_admins_3", "telegram_user_id > 0");
                table.HasCheckConstraint("ck_admins_4", "octet_length(binding_challenge_hash)=32");
                table.HasCheckConstraint("ck_admins_5", "binding_candidate_telegram_id > 0");
                table.HasCheckConstraint("ck_admins_6", "(authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR\n           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)");
                table.HasCheckConstraint("ck_admins_7", "num_nonnulls(telegram_user_id,telegram_verified_by_admin_id,telegram_verified_at,telegram_verification_audit_id) IN (0,4)");
                table.HasCheckConstraint("ck_admins_8", "public_contact_handle IS NULL OR telegram_user_id IS NOT NULL");
                table.HasCheckConstraint("ck_admins_9", "num_nonnulls(binding_challenge_hash,binding_candidate_telegram_id,binding_expires_at) IN (0,3)");
                table.HasCheckConstraint("ck_admins_10", "isfinite(disabled_at)");
                table.HasCheckConstraint("ck_admins_11", "isfinite(created_at)");
                table.HasCheckConstraint("ck_admins_12", "isfinite(last_login_at)");
                table.HasCheckConstraint("ck_admins_13", "isfinite(telegram_verified_at)");
                table.HasCheckConstraint("ck_admins_14", "isfinite(binding_expires_at)");
                table.HasCheckConstraint("ck_admins_15", "length(login) BETWEEN 1 AND 160 AND btrim(login)<>''");
                table.HasCheckConstraint("ck_admins_16", "length(password_verifier) BETWEEN 1 AND 160 AND btrim(password_verifier)<>''");
                table.HasCheckConstraint("ck_admins_17", "length(external_subject) BETWEEN 1 AND 160 AND btrim(external_subject)<>''");
                table.HasCheckConstraint("ck_admins_18", "length(public_contact_handle) BETWEEN 1 AND 160 AND btrim(public_contact_handle)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var login = row.Property(v => v.Login).HasColumnName("login").HasColumnType(sqlite ? "TEXT" : "text");
        login.IsRequired();
        login.HasMaxLength(160);
        var authentication_mode = row.Property(v => v.AuthenticationMode).HasColumnName("authentication_mode").HasColumnType(sqlite ? "TEXT" : "text");
        authentication_mode.IsRequired();
        var password_verifier = row.Property(v => v.PasswordVerifier).HasColumnName("password_verifier").HasColumnType(sqlite ? "TEXT" : "text");
        password_verifier.HasMaxLength(160);
        var external_subject = row.Property(v => v.ExternalSubject).HasColumnName("external_subject").HasColumnType(sqlite ? "TEXT" : "text");
        external_subject.HasMaxLength(160);
        var disabled_at = row.Property(v => v.DisabledAt).HasColumnName("disabled_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) disabled_at.HasConversion(EfValues.UtcTime);
        var auth_version = row.Property(v => v.AuthVersion).HasColumnName("auth_version").HasColumnType(sqlite ? "INTEGER" : "bigint");
        auth_version.IsRequired();
        auth_version.IsConcurrencyToken();
        auth_version.HasDefaultValueSql(sqlite ? "0" : "0");
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var last_login_at = row.Property(v => v.LastLoginAt).HasColumnName("last_login_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) last_login_at.HasConversion(EfValues.UtcTime);
        var telegram_user_id = row.Property(v => v.TelegramUserId).HasColumnName("telegram_user_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var public_contact_handle = row.Property(v => v.PublicContactHandle).HasColumnName("public_contact_handle").HasColumnType(sqlite ? "TEXT" : "text");
        public_contact_handle.HasMaxLength(160);
        var telegram_verified_by_admin_id = row.Property(v => v.TelegramVerifiedByAdminId).HasColumnName("telegram_verified_by_admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        telegram_verified_by_admin_id.ValueGeneratedNever();
        if (sqlite) telegram_verified_by_admin_id.UseCollation("NOCASE");
        var telegram_verified_at = row.Property(v => v.TelegramVerifiedAt).HasColumnName("telegram_verified_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) telegram_verified_at.HasConversion(EfValues.UtcTime);
        var telegram_verification_audit_id = row.Property(v => v.TelegramVerificationAuditId).HasColumnName("telegram_verification_audit_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        telegram_verification_audit_id.ValueGeneratedNever();
        if (sqlite) telegram_verification_audit_id.UseCollation("NOCASE");
        var binding_challenge_hash = row.Property(v => v.BindingChallengeHash).HasColumnName("binding_challenge_hash").HasColumnType(sqlite ? "BLOB" : "bytea");
        var binding_candidate_telegram_id = row.Property(v => v.BindingCandidateTelegramId).HasColumnName("binding_candidate_telegram_id").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var binding_expires_at = row.Property(v => v.BindingExpiresAt).HasColumnName("binding_expires_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) binding_expires_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.Login).IsUnique();
        row.HasIndex(v => v.TelegramUserId).IsUnique();
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.TelegramVerifiedByAdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("admins_telegram_verified_by_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<AuditEvent>().WithMany().HasForeignKey(v => v.TelegramVerificationAuditId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("admins_telegram_verification_audit_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
