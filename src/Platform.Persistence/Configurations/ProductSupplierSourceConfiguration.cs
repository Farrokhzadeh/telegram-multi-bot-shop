using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Persistence.Entities;

namespace Platform.Persistence.Configurations;

internal sealed class ProductSupplierSourceConfiguration(bool sqlite) : IEntityTypeConfiguration<ProductSupplierSource>
{
    public void Configure(EntityTypeBuilder<ProductSupplierSource> row)
    {
        row.ToTable("product_supplier_sources", sqlite ? null : "shop", table =>
        {
            if (sqlite)
            {
                table.HasCheckConstraint("ck_product_supplier_sources_1", "id IS NULL OR akanti_uuid(id)");
                table.HasCheckConstraint("ck_product_supplier_sources_2", "product_id IS NULL OR akanti_uuid(product_id)");
                table.HasCheckConstraint("ck_product_supplier_sources_3", "provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')");
                table.HasCheckConstraint("ck_product_supplier_sources_4", "external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_5", "external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_6", "enabled IS NULL OR (enabled IN (0,1))");
                table.HasCheckConstraint("ck_product_supplier_sources_7", "revision > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_8", "observation_revision >= 0");
                table.HasCheckConstraint("ck_product_supplier_sources_9", "observed_source_revision > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_10", "observed_account_ref IS NULL OR (length(observed_account_ref) BETWEEN 1 AND 160 AND trim(observed_account_ref)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_11", "availability_code IN ('unknown','available','out_of_stock','ineligible','error')");
                table.HasCheckConstraint("ck_product_supplier_sources_12", "available_quantity >= 0");
                table.HasCheckConstraint("ck_product_supplier_sources_13", "observed_unit_cost IS NULL OR (akanti_decimal(observed_unit_cost)=1)");
                table.HasCheckConstraint("ck_product_supplier_sources_14", "akanti_decimal_compare(observed_unit_cost,'0') > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_15", "observed_currency IS NULL OR (length(observed_currency) BETWEEN 1 AND 160 AND trim(observed_currency)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_16", "eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')");
                table.HasCheckConstraint("ck_product_supplier_sources_17", "quote_ref IS NULL OR (length(quote_ref) BETWEEN 1 AND 160 AND trim(quote_ref)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_18", "observation_error_code IS NULL OR (length(observation_error_code) BETWEEN 1 AND 160 AND trim(observation_error_code)<>'')");
                table.HasCheckConstraint("ck_product_supplier_sources_19", "((observed_unit_cost IS NOT NULL) + (observed_currency IS NOT NULL)) IN (0,2)");
                table.HasCheckConstraint("ck_product_supplier_sources_20", "availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)");
                table.HasCheckConstraint("ck_product_supplier_sources_21", "availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')");
                table.HasCheckConstraint("ck_product_supplier_sources_22", "availability_code <> 'out_of_stock' OR available_quantity=0");
                table.HasCheckConstraint("ck_product_supplier_sources_23", "observed_at IS NULL OR akanti_utc_time(observed_at)");
                table.HasCheckConstraint("ck_product_supplier_sources_24", "created_at IS NULL OR akanti_utc_time(created_at)");
                table.HasCheckConstraint("ck_product_supplier_sources_25", "updated_at IS NULL OR akanti_utc_time(updated_at)");
            }
            else
            {
                table.HasCheckConstraint("ck_product_supplier_sources_1", "provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')");
                table.HasCheckConstraint("ck_product_supplier_sources_2", "revision > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_3", "observation_revision >= 0");
                table.HasCheckConstraint("ck_product_supplier_sources_4", "observed_source_revision > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_5", "availability_code IN ('unknown','available','out_of_stock','ineligible','error')");
                table.HasCheckConstraint("ck_product_supplier_sources_6", "available_quantity >= 0");
                table.HasCheckConstraint("ck_product_supplier_sources_7", "observed_unit_cost > 0");
                table.HasCheckConstraint("ck_product_supplier_sources_8", "eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')");
                table.HasCheckConstraint("ck_product_supplier_sources_9", "num_nonnulls(observed_unit_cost,observed_currency) IN (0,2)");
                table.HasCheckConstraint("ck_product_supplier_sources_10", "availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)");
                table.HasCheckConstraint("ck_product_supplier_sources_11", "availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')");
                table.HasCheckConstraint("ck_product_supplier_sources_12", "availability_code <> 'out_of_stock' OR available_quantity=0");
                table.HasCheckConstraint("ck_product_supplier_sources_13", "isfinite(observed_at)");
                table.HasCheckConstraint("ck_product_supplier_sources_14", "isfinite(created_at)");
                table.HasCheckConstraint("ck_product_supplier_sources_15", "isfinite(updated_at)");
                table.HasCheckConstraint("ck_product_supplier_sources_16", "length(external_product_id) BETWEEN 1 AND 160 AND btrim(external_product_id)<>''");
                table.HasCheckConstraint("ck_product_supplier_sources_17", "length(external_variant_id) BETWEEN 1 AND 160 AND btrim(external_variant_id)<>''");
                table.HasCheckConstraint("ck_product_supplier_sources_18", "length(observed_account_ref) BETWEEN 1 AND 160 AND btrim(observed_account_ref)<>''");
                table.HasCheckConstraint("ck_product_supplier_sources_19", "observed_unit_cost NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                table.HasCheckConstraint("ck_product_supplier_sources_20", "length(observed_currency) BETWEEN 1 AND 160 AND btrim(observed_currency)<>''");
                table.HasCheckConstraint("ck_product_supplier_sources_21", "length(quote_ref) BETWEEN 1 AND 160 AND btrim(quote_ref)<>''");
                table.HasCheckConstraint("ck_product_supplier_sources_22", "length(observation_error_code) BETWEEN 1 AND 160 AND btrim(observation_error_code)<>''");
            }
        });
        row.HasKey(v => v.Id);
        var id = row.Property(v => v.Id).HasColumnName("id").HasColumnType(sqlite ? "TEXT" : "uuid");
        id.IsRequired();
        id.ValueGeneratedNever();
        if (sqlite) id.UseCollation("NOCASE");
        var product_id = row.Property(v => v.ProductId).HasColumnName("product_id").HasColumnType(sqlite ? "TEXT" : "uuid");
        product_id.IsRequired();
        product_id.ValueGeneratedNever();
        if (sqlite) product_id.UseCollation("NOCASE");
        var provider_code = row.Property(v => v.ProviderCode).HasColumnName("provider_code").HasColumnType(sqlite ? "TEXT" : "text");
        provider_code.IsRequired();
        var external_product_id = row.Property(v => v.ExternalProductId).HasColumnName("external_product_id").HasColumnType(sqlite ? "TEXT" : "text");
        external_product_id.IsRequired();
        external_product_id.HasMaxLength(160);
        var external_variant_id = row.Property(v => v.ExternalVariantId).HasColumnName("external_variant_id").HasColumnType(sqlite ? "TEXT" : "text");
        external_variant_id.HasMaxLength(160);
        var enabled = row.Property(v => v.Enabled).HasColumnName("enabled").HasColumnType(sqlite ? "INTEGER" : "boolean");
        enabled.IsRequired();
        enabled.HasDefaultValueSql(sqlite ? "false" : "false");
        var revision = row.Property(v => v.Revision).HasColumnName("revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        revision.IsRequired();
        revision.IsConcurrencyToken();
        revision.HasDefaultValueSql(sqlite ? "1" : "1");
        var observation_revision = row.Property(v => v.ObservationRevision).HasColumnName("observation_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        observation_revision.IsRequired();
        observation_revision.IsConcurrencyToken();
        observation_revision.HasDefaultValueSql(sqlite ? "0" : "0");
        var observed_source_revision = row.Property(v => v.ObservedSourceRevision).HasColumnName("observed_source_revision").HasColumnType(sqlite ? "INTEGER" : "bigint");
        var observed_account_ref = row.Property(v => v.ObservedAccountRef).HasColumnName("observed_account_ref").HasColumnType(sqlite ? "TEXT" : "text");
        observed_account_ref.HasMaxLength(160);
        var observed_at = row.Property(v => v.ObservedAt).HasColumnName("observed_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        if (sqlite) observed_at.HasConversion(EfValues.UtcTime);
        var availability_code = row.Property(v => v.AvailabilityCode).HasColumnName("availability_code").HasColumnType(sqlite ? "TEXT" : "text");
        availability_code.IsRequired();
        availability_code.HasDefaultValueSql(sqlite ? "'unknown'" : "'unknown'");
        var available_quantity = row.Property(v => v.AvailableQuantity).HasColumnName("available_quantity").HasColumnType(sqlite ? "INTEGER" : "integer");
        var observed_unit_cost = row.Property(v => v.ObservedUnitCost).HasColumnName("observed_unit_cost").HasColumnType(sqlite ? "TEXT" : "numeric");
        if (sqlite) observed_unit_cost.Metadata.SetTypeMapping(ExactSqliteDecimalMapping.Instance);
        var observed_currency = row.Property(v => v.ObservedCurrency).HasColumnName("observed_currency").HasColumnType(sqlite ? "TEXT" : "text");
        observed_currency.HasMaxLength(160);
        var eligibility_code = row.Property(v => v.EligibilityCode).HasColumnName("eligibility_code").HasColumnType(sqlite ? "TEXT" : "text");
        eligibility_code.IsRequired();
        eligibility_code.HasDefaultValueSql(sqlite ? "'unknown'" : "'unknown'");
        var quote_ref = row.Property(v => v.QuoteRef).HasColumnName("quote_ref").HasColumnType(sqlite ? "TEXT" : "text");
        quote_ref.HasMaxLength(160);
        var observation_error_code = row.Property(v => v.ObservationErrorCode).HasColumnName("observation_error_code").HasColumnType(sqlite ? "TEXT" : "text");
        observation_error_code.HasMaxLength(160);
        var created_at = row.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        created_at.IsRequired();
        created_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) created_at.HasConversion(EfValues.UtcTime);
        var updated_at = row.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType(sqlite ? "TEXT" : "timestamptz");
        updated_at.IsRequired();
        updated_at.HasDefaultValueSql(sqlite ? "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')" : "CURRENT_TIMESTAMP");
        if (sqlite) updated_at.HasConversion(EfValues.UtcTime);
        row.HasIndex(v => new { v.ProductId, v.Id }).IsUnique();
        row.HasOne<Product>().WithMany().HasForeignKey(v => v.ProductId).HasPrincipalKey(v => v.Id).OnDelete(DeleteBehavior.Restrict).HasConstraintName("product_supplier_sources_product_id_fk").HasAnnotation("Akanti:Deferrability", "DEFERRABLE INITIALLY IMMEDIATE");
        row.HasIndex(v => new { v.ProductId, v.ProviderCode, v.ExternalProductId }).HasDatabaseName("source_without_variant").IsUnique().HasFilter(sqlite ? "external_variant_id IS NULL" : "external_variant_id IS NULL");
        row.HasIndex(v => new { v.ProductId, v.ProviderCode, v.ExternalProductId, v.ExternalVariantId }).HasDatabaseName("source_with_variant").IsUnique().HasFilter(sqlite ? "external_variant_id IS NOT NULL" : "external_variant_id IS NOT NULL");
    }
}
