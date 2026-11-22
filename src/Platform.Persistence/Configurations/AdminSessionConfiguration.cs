using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class AdminSessionConfiguration(bool sqlite) : IEntityTypeConfiguration<AdminSession>
{
    public void Configure(EntityTypeBuilder<AdminSession> row)
    {
        row.ToTable("admin_sessions", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_admin_sessions_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_admin_sessions_2", "admin_id IS NULL OR akanti_uuid(admin_id)");
                table.HasCheckConstraint("ck_admin_sessions_3", "length(token_hash)=32");
                table.HasCheckConstraint("ck_admin_sessions_4", "auth_version_at_issue >= 0");
                table.HasCheckConstraint("ck_admin_sessions_5", "expires_at > issued_at");
                table.HasCheckConstraint("ck_admin_sessions_6", "revoked_at IS NULL OR revoked_at >= issued_at");
                table.HasCheckConstraint("ck_admin_sessions_7", "issued_at IS NULL OR akanti_utc_time(issued_at)");
                table.HasCheckConstraint("ck_admin_sessions_8", "expires_at IS NULL OR akanti_utc_time(expires_at)");
                table.HasCheckConstraint("ck_admin_sessions_9", "revoked_at IS NULL OR akanti_utc_time(revoked_at)");
                table.HasCheckConstraint("ck_admin_sessions_10", "last_used_at IS NULL OR akanti_utc_time(last_used_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_admin_sessions_1", "octet_length(token_hash)=32");
                table.HasCheckConstraint("ck_admin_sessions_2", "auth_version_at_issue >= 0");
                table.HasCheckConstraint("ck_admin_sessions_3", "expires_at > issued_at");
                table.HasCheckConstraint("ck_admin_sessions_4", "revoked_at IS NULL OR revoked_at >= issued_at");
                table.HasCheckConstraint("ck_admin_sessions_5", "isfinite(issued_at)");
                table.HasCheckConstraint("ck_admin_sessions_6", "isfinite(expires_at)");
                table.HasCheckConstraint("ck_admin_sessions_7", "isfinite(revoked_at)");
                table.HasCheckConstraint("ck_admin_sessions_8", "isfinite(last_used_at)");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var admin_id = row.Property(v => v.AdminId).HasColumnName("admin_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        admin_id.IsRequired();
        admin_id.ValueGeneratedNever();
        if (sqlite) admin_id.UseCollation("NOCASE");
        var token_hash = row.Property(v => v.TokenHash).HasColumnName("token_hash").HasColumnType(sqlite ? "BLOB" : "bytea");
        token_hash.IsRequired();
        var auth_version_at_issue = row.Property(v => v.AuthVersionAtIssue).HasColumnName("auth_version_at_issue").HasColumnType(sqlite ? "INTEGER" : "bigint");
        auth_version_at_issue.IsRequired();
        var issued_at = row.Property(v => v.IssuedAt).HasColumnName("issued_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        issued_at.IsRequired();
        issued_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) issued_at.HasConversion(EfValues.UtcTime);
        var expires_at = row.Property(v => v.ExpiresAt).HasColumnName("expires_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        expires_at.IsRequired();
        if (sqlite) expires_at.HasConversion(EfValues.UtcTime);
        var revoked_at = row.Property(v => v.RevokedAt).HasColumnName("revoked_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) revoked_at.HasConversion(EfValues.UtcTime);
        var last_used_at = row.Property(v => v.LastUsedAt).HasColumnName("last_used_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) last_used_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => v.TokenHash).IsUnique();
        row.HasOne<Admin>().WithMany().HasForeignKey(v => v.AdminId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("admin_sessions_admin_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
