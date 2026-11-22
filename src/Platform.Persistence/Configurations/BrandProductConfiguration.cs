using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class BrandProductConfiguration(bool sqlite) : IEntityTypeConfiguration<BrandProduct>
{
    public void Configure(EntityTypeBuilder<BrandProduct> row)
    {
        row.ToTable("brand_products", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_brand_products_1", "brand_id IS NULL OR akanti_uuid(brand_id)");
                table.HasCheckConstraint("ck_brand_products_2", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_brand_products_3", "visible IS NULL OR (visible IN (0,1))");
                table.HasCheckConstraint("ck_brand_products_4", "title_mode IN ('inherit','override')");
                table.HasCheckConstraint("ck_brand_products_5", "title_override IS NULL OR (length(title_override) BETWEEN 1 AND 160 AND trim(title_override)<>'')");
                table.HasCheckConstraint("ck_brand_products_6", "description_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_7", "description_override IS NULL OR (length(description_override)<=16384)");
                table.HasCheckConstraint("ck_brand_products_8", "icon_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_9", "icon_override IS NULL OR (length(icon_override) BETWEEN 1 AND 1024)");
                table.HasCheckConstraint("ck_brand_products_10", "guidance_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_11", "guidance_override IS NULL OR (length(guidance_override)<=16384)");
                table.HasCheckConstraint("ck_brand_products_12", "price_usdt IS NULL OR (akanti_decimal(price_usdt)=1)");
                table.HasCheckConstraint("ck_brand_products_13", "akanti_decimal_compare(price_usdt,'0') > 0");
                table.HasCheckConstraint("ck_brand_products_14", "price_toman IS NULL OR (akanti_decimal(price_toman)=1)");
                table.HasCheckConstraint("ck_brand_products_15", "akanti_decimal_compare(price_toman,'0') > 0");
                table.HasCheckConstraint("ck_brand_products_16", "auto_price_toman IS NULL OR (auto_price_toman IN (0,1))");
                table.HasCheckConstraint("ck_brand_products_17", "allow_wallet IS NULL OR (allow_wallet IN (0,1))");
                table.HasCheckConstraint("ck_brand_products_18", "allow_crypto IS NULL OR (allow_crypto IN (0,1))");
                table.HasCheckConstraint("ck_brand_products_19", "allow_bank IS NULL OR (allow_bank IN (0,1))");
                table.HasCheckConstraint("ck_brand_products_20", "min_quantity > 0");
                table.HasCheckConstraint("ck_brand_products_21", "max_quantity >= min_quantity");
                table.HasCheckConstraint("ck_brand_products_22", "revision >= 0");
                table.HasCheckConstraint("ck_brand_products_23", "(title_mode='override')=(title_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_24", "(description_mode='override')=(description_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_25", "(icon_mode='override')=(icon_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_26", "(guidance_mode='override')=(guidance_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_27", "NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL");
                table.HasCheckConstraint("ck_brand_products_28", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_brand_products_1", "title_mode IN ('inherit','override')");
                table.HasCheckConstraint("ck_brand_products_2", "description_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_3", "icon_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_4", "guidance_mode IN ('inherit','override','clear')");
                table.HasCheckConstraint("ck_brand_products_5", "price_usdt > 0");
                table.HasCheckConstraint("ck_brand_products_6", "price_toman > 0");
                table.HasCheckConstraint("ck_brand_products_7", "min_quantity > 0");
                table.HasCheckConstraint("ck_brand_products_8", "max_quantity >= min_quantity");
                table.HasCheckConstraint("ck_brand_products_9", "revision >= 0");
                table.HasCheckConstraint("ck_brand_products_10", "(title_mode='override')=(title_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_11", "(description_mode='override')=(description_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_12", "(icon_mode='override')=(icon_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_13", "(guidance_mode='override')=(guidance_override IS NOT NULL)");
                table.HasCheckConstraint("ck_brand_products_14", "NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL");
                table.HasCheckConstraint("ck_brand_products_15", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_brand_products_16", "length(title_override) BETWEEN 1 AND 160 AND btrim(title_override)<>''");
                table.HasCheckConstraint("ck_brand_products_17", "length(description_override)<=16384");
                table.HasCheckConstraint("ck_brand_products_18", "length(icon_override) BETWEEN 1 AND 1024 AND btrim(icon_override)<>''");
                table.HasCheckConstraint("ck_brand_products_19", "length(guidance_override)<=16384");
                table.HasCheckConstraint("ck_brand_products_20", "price_usdt NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_brand_products_21", "price_toman NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
            }
        });
        row.HasKey(v => new { v.BrandId, v.ProductId });
        var brand_id = row.Property(v => v.BrandId).HasColumnName("brand_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        brand_id.IsRequired();
        brand_id.ValueGeneratedNever();
        if (sqlite) brand_id.UseCollation("NOCASE");
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.IsRequired();
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var visible = row.Property(v => v.Visible).HasColumnName("visible").HasColumnType(sqlite ? "INTEGER" : "boolean");
        visible.IsRequired();
        visible.HasDefaultValueSql(sqlite ? "false" : "false");
        var title_mode = row.Property(v => v.TitleMode).HasColumnName("title_mode").HasColumnType(sqlite ? "TEXT" : "text");
        title_mode.IsRequired();
        title_mode.HasDefaultValueSql(sqlite ? "'inherit'" : "'inherit'");
        var title_override = row.Property(v => v.TitleOverride).HasColumnName("title_override").HasColumnType(sqlite ? "TEXT" : "text");
        title_override.HasMaxLength(160);
        var description_mode = row.Property(v => v.DescriptionMode).HasColumnName("description_mode").HasColumnType(sqlite ? "TEXT" : "text");
        description_mode.IsRequired();
        description_mode.HasDefaultValueSql(sqlite ? "'inherit'" : "'inherit'");
        var description_override = row.Property(v => v.DescriptionOverride).HasColumnName("description_override").HasColumnType(sqlite ? "TEXT" : "text");
        description_override.HasMaxLength(16384);
        var icon_mode = row.Property(v => v.IconMode).HasColumnName("icon_mode").HasColumnType(sqlite ? "TEXT" : "text");
        icon_mode.IsRequired();
        icon_mode.HasDefaultValueSql(sqlite ? "'inherit'" : "'inherit'");
        var icon_override = row.Property(v => v.IconOverride).HasColumnName("icon_override").HasColumnType(sqlite ? "TEXT" : "text");
        icon_override.HasMaxLength(1024);
        var guidance_mode = row.Property(v => v.GuidanceMode).HasColumnName("guidance_mode").HasColumnType(sqlite ? "TEXT" : "text");
        guidance_mode.IsRequired();
        guidance_mode.HasDefaultValueSql(sqlite ? "'inherit'" : "'inherit'");
        var guidance_override = row.Property(v => v.GuidanceOverride).HasColumnName("guidance_override").HasColumnType(sqlite ? "TEXT" : "text");
        guidance_override.HasMaxLength(16384);
        var price_usdt = row.Property(v => v.PriceUsdt).HasColumnName("price_usdt").HasColumnType(sqlite ? "TEXT" : "numeric");
        price_usdt.IsRequired();
        if (sqlite) price_usdt.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var price_toman = row.Property(v => v.PriceToman).HasColumnName("price_toman").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) price_toman.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var auto_price_toman = row.Property(v => v.AutoPriceToman).HasColumnName("auto_price_toman").HasColumnType(sqlite ? "INTEGER" : "boolean");
        auto_price_toman.IsRequired();
        auto_price_toman.HasDefaultValueSql(sqlite ? "true" : "true");
        auto_price_toman.HasSentinel(true);
        var allow_wallet = row.Property(v => v.AllowWallet).HasColumnName("allow_wallet").HasColumnType(sqlite ? "INTEGER" : "boolean");
        allow_wallet.IsRequired();
        allow_wallet.HasDefaultValueSql(sqlite ? "true" : "true");
        allow_wallet.HasSentinel(true);
        var allow_crypto = row.Property(v => v.AllowCrypto).HasColumnName("allow_crypto").HasColumnType(sqlite ? "INTEGER" : "boolean");
        allow_crypto.IsRequired();
        allow_crypto.HasDefaultValueSql(sqlite ? "false" : "false");
        var allow_bank = row.Property(v => v.AllowBank).HasColumnName("allow_bank").HasColumnType(sqlite ? "INTEGER" : "boolean");
        allow_bank.IsRequired();
        allow_bank.HasDefaultValueSql(sqlite ? "false" : "false");
        var min_quantity = row.Property(v => v.MinQuantity).HasColumnName("min_quantity").HasColumnType(sqlite ? "INTEGER" : "integer");
        min_quantity.IsRequired();
        min_quantity.HasDefaultValueSql(sqlite ? "1" : "1");
        var max_quantity = row.Property(v => v.MaxQuantity).HasColumnName("max_quantity").HasColumnType(sqlite ? "INTEGER" : "integer");
        max_quantity.IsRequired();
        max_quantity.HasDefaultValueSql(sqlite ? "100" : "100");
        var revision = row.Property(v => v.Revision).HasColumnName("revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision.IsRequired();
        revision.IsConcurrencyToken();
        revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        row.HasOne<Brand>().WithMany().HasForeignKey(v => v.BrandId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("brand_products_brand_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("brand_products_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
    }
}
