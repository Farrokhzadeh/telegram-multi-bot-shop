using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Platform.Persistence.Migrations;

internal static class PlainContentBaseline
{
    internal const string MigrationId = "20261126000000_InitialPlaintext";

    private sealed record Field(string OldProperty,string? Property,string OldColumn,string? Column,bool Required = false);
    private sealed record Change(string Entity,string Table,Field[] Fields,(string Name,string? Sql)[] Checks);

    private static readonly Field[] BrandFields =
    [
        new("BotTokenCiphertext","BotToken","bot_token_ciphertext","bot_token",true),
        new("BankNumberCiphertext","BankNumber","bank_number_ciphertext","bank_number",false),
        new("BankHolderCiphertext","BankHolder","bank_holder_ciphertext","bank_holder",false),
        new("BotTokenKeyId",null,"bot_token_key_id",null),
        new("BankKeyId",null,"bank_key_id",null),
    ];

    private static readonly Field[] InventoryItemFields =
    [
        new("CredentialCiphertext","CredentialContent","credential_ciphertext","credential_content",true),
        new("CredentialKeyId",null,"credential_key_id",null),
    ];

    private static readonly Field[] PaymentFields =
    [
        new("BankDestinationCiphertext","BankDestination","bank_destination_ciphertext","bank_destination",false),
        new("PendingEvidenceCiphertext","PendingEvidence","pending_evidence_ciphertext","pending_evidence",false),
        new("BankDestinationKeyId",null,"bank_destination_key_id",null),
        new("PendingEvidenceKeyId",null,"pending_evidence_key_id",null),
    ];

    private static readonly Field[] SupportMessageFields =
    [
        new("BodyCiphertext","BodyContent","body_ciphertext","body_content",false),
        new("BodyKeyId",null,"body_key_id",null),
    ];

    private static readonly Field[] DiscountFields =
    [
        new("CodeCiphertext","CodeContent","code_ciphertext","code_content",true),
        new("CodeKeyId",null,"code_key_id",null),
    ];

    private static readonly Field[] FulfillmentFields =
    [
        new("DeliveredContentKeyId",null,"delivered_content_key_id",null),
    ];

    private static readonly Change[] SqliteChanges =
    [
        new("Brand","brands",BrandFields,
        [
            ("ck_brands_10","length(bot_token) > 0"),
            ("ck_brands_11",null),
            ("ck_brands_22",null),
            ("ck_brands_33","((bank_number IS NOT NULL) + (bank_holder IS NOT NULL) + (bank_card_fingerprint IS NOT NULL) + (bank_changed_at IS NOT NULL)) IN (0,4)"),
        ]),
        new("InventoryItem","inventory_items",InventoryItemFields,
        [
            ("ck_inventory_items_3","length(credential_content)>0"),
            ("ck_inventory_items_4",null),
        ]),
        new("Payment","payments",PaymentFields,
        [
            ("ck_payments_27",null),
            ("ck_payments_33","length(pending_evidence) BETWEEN 2 AND 65536"),
            ("ck_payments_34",null),
            ("ck_payments_38",null),
            ("ck_payments_39",null),
            ("ck_payments_40","(evidence_asset_ref IS NOT NULL OR pending_evidence IS NOT NULL) = (evidence_digest IS NOT NULL)"),
            ("ck_payments_41","pending_evidence IS NULL OR (evidence_asset_ref IS NULL AND order_id IS NOT NULL AND ipn_key_id IS NOT NULL AND first_payment_observed_at IS NOT NULL AND status IN ('confirming','review'))"),
            ("ck_payments_48","(payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination IS NULL) OR\n           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND (bank_destination IS NOT NULL OR coalesce(legacy_kind='manual_deposit',false)) AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)"),
            ("ck_payments_52","ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND (evidence_asset_ref IS NOT NULL OR pending_evidence IS NOT NULL))"),
        ]),
        new("SupportMessage","support_messages",SupportMessageFields,
        [
            ("ck_support_messages_6",null),
            ("ck_support_messages_10",null),
            ("ck_support_messages_11","((body_content IS NOT NULL) + (asset_ref IS NOT NULL))>0"),
        ]),
        new("Discount","discounts",DiscountFields,
        [
            ("ck_discounts_4","length(code_content)>0"),
            ("ck_discounts_5",null),
        ]),
        new("Fulfillment","fulfillments",FulfillmentFields,
        [
            ("ck_fulfillments_9",null),
            ("ck_fulfillments_16","((delivered_content IS NOT NULL) + (content_kind IS NOT NULL)) IN (0,2)"),
        ]),
    ];

    private static readonly Change[] PostgresChanges =
    [
        new("Brand","brands",BrandFields,
        [
            ("ck_brands_3","octet_length(bot_token) > 0"),
            ("ck_brands_10","num_nonnulls(bank_number,bank_holder,bank_card_fingerprint,bank_changed_at) IN (0,4)"),
            ("ck_brands_25",null),
            ("ck_brands_26",null),
        ]),
        new("InventoryItem","inventory_items",InventoryItemFields,
        [
            ("ck_inventory_items_1","octet_length(credential_content)>0"),
            ("ck_inventory_items_11",null),
        ]),
        new("Payment","payments",PaymentFields,
        [
            ("ck_payments_13","octet_length(pending_evidence) BETWEEN 2 AND 65536"),
            ("ck_payments_16",null),
            ("ck_payments_17",null),
            ("ck_payments_18","(evidence_asset_ref IS NOT NULL OR pending_evidence IS NOT NULL) = (evidence_digest IS NOT NULL)"),
            ("ck_payments_19","pending_evidence IS NULL OR (evidence_asset_ref IS NULL AND order_id IS NOT NULL AND ipn_key_id IS NOT NULL AND first_payment_observed_at IS NOT NULL AND status IN ('confirming','review'))"),
            ("ck_payments_26","(payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination IS NULL) OR\n           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND (bank_destination IS NOT NULL OR coalesce(legacy_kind='manual_deposit',false)) AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)"),
            ("ck_payments_30","ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND (evidence_asset_ref IS NOT NULL OR pending_evidence IS NOT NULL))"),
            ("ck_payments_48",null),
            ("ck_payments_52",null),
        ]),
        new("SupportMessage","support_messages",SupportMessageFields,
        [
            ("ck_support_messages_5",null),
            ("ck_support_messages_6","num_nonnulls(body_content,asset_ref)>0"),
            ("ck_support_messages_8",null),
        ]),
        new("Discount","discounts",DiscountFields,
        [
            ("ck_discounts_2","octet_length(code_content)>0"),
            ("ck_discounts_14",null),
        ]),
        new("Fulfillment","fulfillments",FulfillmentFields,
        [
            ("ck_fulfillments_8","num_nonnulls(delivered_content,content_kind) IN (0,2)"),
            ("ck_fulfillments_15",null),
        ]),
    ];

    internal static void Create(MigrationBuilder destination, Migration legacy, bool sqlite)
    {
        legacy.ActiveProvider = destination.ActiveProvider ?? throw new InvalidOperationException("The migration provider is unavailable.");
        var changes = (sqlite ? SqliteChanges : PostgresChanges).ToDictionary(change => change.Table, StringComparer.Ordinal);
        foreach (var operation in legacy.UpOperations)
        {
            if (operation is SqlOperation legacySql)
            {
                if (legacySql.Sql != (sqlite ? EfMigrationGuards.SqliteBaseline : EfMigrationGuards.PostgresBaseline) &&
                    (!sqlite || legacySql.Sql != "PRAGMA user_version=1;"))
                    throw new InvalidOperationException("Unexpected SQL in the pinned schema baseline.");
                continue;
            }
            if (operation is CreateTableOperation table && changes.TryGetValue(table.Name, out var change))
            {
                foreach (var field in change.Fields)
                {
                    var column = table.Columns.Single(column => column.Name == field.OldColumn);
                    if (field.Column is null) table.Columns.Remove(column);
                    else column.Name = field.Column;
                }
                foreach (var (name, sql) in change.Checks)
                {
                    var constraint = table.CheckConstraints.SingleOrDefault(constraint => constraint.Name == name);
                    if (sql is null)
                    {
                        if (constraint is null) throw new InvalidOperationException("Plaintext baseline source constraint is missing.");
                        table.CheckConstraints.Remove(constraint);
                    }
                    else if (constraint is null)
                        table.CheckConstraints.Add(new AddCheckConstraintOperation { Name = name, Table = table.Name, Schema = table.Schema, Sql = sql });
                    else constraint.Sql = sql;
                }
                table.CheckConstraints.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            }
            destination.Operations.Add(operation);
        }
        destination.Sql(sqlite ? EfMigrationGuards.SqliteBaseline : PlainTargetSchema.PostgresGuards());
        destination.Sql(sqlite ? $"PRAGMA user_version={PlainTargetSchema.SqliteVersion};" : $"COMMENT ON SCHEMA shop IS '{PlainTargetSchema.PostgresFormat}';");
    }

    internal static void UpdateModel(ModelBuilder builder, bool sqlite)
    {
        foreach (var change in sqlite ? SqliteChanges : PostgresChanges)
        {
            var entity = builder.Entity("Platform.Persistence.Entities." + change.Entity);
            foreach (var field in change.Fields)
            {
                entity.Ignore(field.OldProperty);
                if (field.Property is not null)
                    entity.Property<byte[]>(field.Property).HasColumnName(field.Column!).HasColumnType(sqlite ? "BLOB" : "bytea").IsRequired(field.Required);
            }
            foreach (var check in change.Checks)
                entity.Metadata.RemoveCheckConstraint(check.Name);
            entity.ToTable(change.Table, sqlite ? null : "shop", table =>
            {
                foreach (var (name,sql) in change.Checks)
                    if (sql is not null) table.HasCheckConstraint(name,sql);
            });
        }
    }
}
