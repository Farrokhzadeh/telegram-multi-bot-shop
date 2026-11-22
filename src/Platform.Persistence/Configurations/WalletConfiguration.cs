using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class WalletConfiguration(bool sqlite) : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> row)
    {
        row.ToTable("wallets", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_wallets_1", "user_id IS NULL OR akanti_uuid(user_id)");
                table.HasCheckConstraint("ck_wallets_2", "balance IS NULL OR (akanti_decimal(balance)=1)");
                table.HasCheckConstraint("ck_wallets_3", "akanti_decimal_compare(balance,'0') >= 0");
                table.HasCheckConstraint("ck_wallets_4", "version >= 0");
                table.HasCheckConstraint("ck_wallets_5", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_wallets_1", "balance >= 0");
                table.HasCheckConstraint("ck_wallets_2", "version >= 0");
                table.HasCheckConstraint("ck_wallets_3", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_wallets_4", "balance NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
            }
        });
        row.HasKey(v => v.UserId);
        var user_id = row.Property(v => v.UserId).HasColumnName("user_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        user_id.IsRequired();
        user_id.ValueGeneratedNever();
        if (sqlite) user_id.UseCollation("NOCASE");
        var balance = row.Property(v => v.Balance).HasColumnName("balance").HasColumnType(sqlite ? "TEXT" : "numeric");
        balance.IsRequired();
        balance.HasDefaultValueSql(sqlite ? "0" : "0");
        if (sqlite) balance.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var version = row.Property(v => v.Version).HasColumnName("version").HasColumnType(sqlite ? "INTEGER" : "bigint");
        version.IsRequired();
        version.IsConcurrencyToken();
        version.HasDefaultValueSql(sqlite ? "0" : "0");
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        row.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("wallets_user_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
