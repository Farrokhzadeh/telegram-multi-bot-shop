using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Persistence.Migrations.Sqlite
{
    public partial class InitialEfBaseline : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    admin_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    token_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    auth_version_at_issue = table.Column<long>(type: "INTEGER", nullable: false),
                    issued_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    expires_at = table.Column<string>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<string>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.id);
                    table.CheckConstraint("ck_admin_sessions_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_admin_sessions_10", "last_used_at IS NULL OR akanti_utc_time(last_used_at)");
                    table.CheckConstraint("ck_admin_sessions_2", "admin_id IS NULL OR akanti_uuid(admin_id)");
                    table.CheckConstraint("ck_admin_sessions_3", "length(token_hash)=32");
                    table.CheckConstraint("ck_admin_sessions_4", "auth_version_at_issue >= 0");
                    table.CheckConstraint("ck_admin_sessions_5", "expires_at > issued_at");
                    table.CheckConstraint("ck_admin_sessions_6", "revoked_at IS NULL OR revoked_at >= issued_at");
                    table.CheckConstraint("ck_admin_sessions_7", "issued_at IS NULL OR akanti_utc_time(issued_at)");
                    table.CheckConstraint("ck_admin_sessions_8", "expires_at IS NULL OR akanti_utc_time(expires_at)");
                    table.CheckConstraint("ck_admin_sessions_9", "revoked_at IS NULL OR akanti_utc_time(revoked_at)");
                });

            migrationBuilder.CreateTable(
                name: "admins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    login = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    authentication_mode = table.Column<string>(type: "TEXT", nullable: false),
                    password_verifier = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    external_subject = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    disabled_at = table.Column<string>(type: "TEXT", nullable: true),
                    auth_version = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    last_login_at = table.Column<string>(type: "TEXT", nullable: true),
                    telegram_user_id = table.Column<long>(type: "INTEGER", nullable: true),
                    public_contact_handle = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    telegram_verified_by_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    telegram_verified_at = table.Column<string>(type: "TEXT", nullable: true),
                    telegram_verification_audit_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    binding_challenge_hash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    binding_candidate_telegram_id = table.Column<long>(type: "INTEGER", nullable: true),
                    binding_expires_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admins", x => x.id);
                    table.CheckConstraint("ck_admins_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_admins_10", "telegram_verification_audit_id IS NULL OR akanti_uuid(telegram_verification_audit_id)");
                    table.CheckConstraint("ck_admins_11", "length(binding_challenge_hash)=32");
                    table.CheckConstraint("ck_admins_12", "binding_candidate_telegram_id > 0");
                    table.CheckConstraint("ck_admins_13", "(authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR\n           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)");
                    table.CheckConstraint("ck_admins_14", "((telegram_user_id IS NOT NULL) + (telegram_verified_by_admin_id IS NOT NULL) + (telegram_verified_at IS NOT NULL) + (telegram_verification_audit_id IS NOT NULL)) IN (0,4)");
                    table.CheckConstraint("ck_admins_15", "public_contact_handle IS NULL OR telegram_user_id IS NOT NULL");
                    table.CheckConstraint("ck_admins_16", "((binding_challenge_hash IS NOT NULL) + (binding_candidate_telegram_id IS NOT NULL) + (binding_expires_at IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_admins_17", "disabled_at IS NULL OR akanti_utc_time(disabled_at)");
                    table.CheckConstraint("ck_admins_18", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_admins_19", "last_login_at IS NULL OR akanti_utc_time(last_login_at)");
                    table.CheckConstraint("ck_admins_2", "login IS NULL OR (length(login) BETWEEN 1 AND 160 AND trim(login)<>'')");
                    table.CheckConstraint("ck_admins_20", "telegram_verified_at IS NULL OR akanti_utc_time(telegram_verified_at)");
                    table.CheckConstraint("ck_admins_21", "binding_expires_at IS NULL OR akanti_utc_time(binding_expires_at)");
                    table.CheckConstraint("ck_admins_3", "authentication_mode IN ('password','external')");
                    table.CheckConstraint("ck_admins_4", "password_verifier IS NULL OR (length(password_verifier) BETWEEN 1 AND 160 AND trim(password_verifier)<>'')");
                    table.CheckConstraint("ck_admins_5", "external_subject IS NULL OR (length(external_subject) BETWEEN 1 AND 160 AND trim(external_subject)<>'')");
                    table.CheckConstraint("ck_admins_6", "auth_version >= 0");
                    table.CheckConstraint("ck_admins_7", "telegram_user_id > 0");
                    table.CheckConstraint("ck_admins_8", "public_contact_handle IS NULL OR (length(public_contact_handle) BETWEEN 1 AND 160 AND trim(public_contact_handle)<>'')");
                    table.CheckConstraint("ck_admins_9", "telegram_verified_by_admin_id IS NULL OR akanti_uuid(telegram_verified_by_admin_id)");
                    table.ForeignKey(
                        name: "admins_telegram_verified_by_admin_id_fk",
                        column: x => x.telegram_verified_by_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "brands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    internal_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    public_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    market = table.Column<string>(type: "TEXT", nullable: false),
                    default_language = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    logo_asset_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    bot_username = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    bot_id = table.Column<long>(type: "INTEGER", nullable: false),
                    bot_token_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    bot_token_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    allow_wallet = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    allow_crypto = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    allow_bank = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    allow_wallet_topup = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    zero_balance_topup = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    require_channel_join = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    support_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    fx_enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    customer_support_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    bank_number_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    bank_holder_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    bank_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    bank_card_fingerprint = table.Column<byte[]>(type: "BLOB", nullable: true),
                    bank_changed_at = table.Column<string>(type: "TEXT", nullable: true),
                    current_fx = table.Column<decimal>(type: "TEXT", nullable: true),
                    fx_source = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    fx_fetched_at = table.Column<string>(type: "TEXT", nullable: true),
                    fx_error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    pricing_rounding_step = table.Column<decimal>(type: "TEXT", nullable: false, defaultValueSql: "5000"),
                    max_exchange_rate_age_seconds = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "3600"),
                    telegram_update_highwater = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "-1"),
                    telegram_update_window = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "(zeroblob(128))"),
                    policy_version = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brands", x => x.id);
                    table.CheckConstraint("ck_brands_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_brands_10", "length(bot_token_ciphertext) > 0");
                    table.CheckConstraint("ck_brands_11", "bot_token_key_id IS NULL OR (length(bot_token_key_id) BETWEEN 1 AND 160 AND trim(bot_token_key_id)<>'')");
                    table.CheckConstraint("ck_brands_12", "active IS NULL OR (active IN (0,1))");
                    table.CheckConstraint("ck_brands_13", "allow_wallet IS NULL OR (allow_wallet IN (0,1))");
                    table.CheckConstraint("ck_brands_14", "allow_crypto IS NULL OR (allow_crypto IN (0,1))");
                    table.CheckConstraint("ck_brands_15", "allow_bank IS NULL OR (allow_bank IN (0,1))");
                    table.CheckConstraint("ck_brands_16", "allow_wallet_topup IS NULL OR (allow_wallet_topup IN (0,1))");
                    table.CheckConstraint("ck_brands_17", "zero_balance_topup IS NULL OR (zero_balance_topup IN (0,1))");
                    table.CheckConstraint("ck_brands_18", "require_channel_join IS NULL OR (require_channel_join IN (0,1))");
                    table.CheckConstraint("ck_brands_19", "support_enabled IS NULL OR (support_enabled IN (0,1))");
                    table.CheckConstraint("ck_brands_2", "code IS NULL OR (length(code) BETWEEN 1 AND 160 AND trim(code)<>'')");
                    table.CheckConstraint("ck_brands_20", "fx_enabled IS NULL OR (fx_enabled IN (0,1))");
                    table.CheckConstraint("ck_brands_21", "customer_support_admin_id IS NULL OR akanti_uuid(customer_support_admin_id)");
                    table.CheckConstraint("ck_brands_22", "bank_key_id IS NULL OR (length(bank_key_id) BETWEEN 1 AND 160 AND trim(bank_key_id)<>'')");
                    table.CheckConstraint("ck_brands_23", "current_fx IS NULL OR (akanti_decimal(current_fx)=1)");
                    table.CheckConstraint("ck_brands_24", "akanti_decimal_compare(current_fx,'0') > 0");
                    table.CheckConstraint("ck_brands_25", "fx_source IS NULL OR (length(fx_source) BETWEEN 1 AND 160 AND trim(fx_source)<>'')");
                    table.CheckConstraint("ck_brands_26", "fx_error_code IS NULL OR (length(fx_error_code) BETWEEN 1 AND 160 AND trim(fx_error_code)<>'')");
                    table.CheckConstraint("ck_brands_27", "pricing_rounding_step IS NULL OR (akanti_decimal(pricing_rounding_step)=1)");
                    table.CheckConstraint("ck_brands_28", "akanti_decimal_compare(pricing_rounding_step,'0') > 0");
                    table.CheckConstraint("ck_brands_29", "max_exchange_rate_age_seconds > 0");
                    table.CheckConstraint("ck_brands_3", "internal_name IS NULL OR (length(internal_name) BETWEEN 1 AND 160 AND trim(internal_name)<>'')");
                    table.CheckConstraint("ck_brands_30", "telegram_update_highwater >= -1");
                    table.CheckConstraint("ck_brands_31", "length(telegram_update_window)=128");
                    table.CheckConstraint("ck_brands_32", "policy_version >= 0");
                    table.CheckConstraint("ck_brands_33", "((bank_number_ciphertext IS NOT NULL) + (bank_holder_ciphertext IS NOT NULL) + (bank_key_id IS NOT NULL) + (bank_card_fingerprint IS NOT NULL) + (bank_changed_at IS NOT NULL)) IN (0,5)");
                    table.CheckConstraint("ck_brands_34", "bank_card_fingerprint IS NULL OR length(bank_card_fingerprint)=32");
                    table.CheckConstraint("ck_brands_35", "NOT allow_bank OR (market='iran' AND bank_card_fingerprint IS NOT NULL)");
                    table.CheckConstraint("ck_brands_36", "((current_fx IS NOT NULL) + (fx_source IS NOT NULL) + (fx_fetched_at IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_brands_37", "NOT (active AND support_enabled) OR customer_support_admin_id IS NOT NULL");
                    table.CheckConstraint("ck_brands_38", "bank_changed_at IS NULL OR akanti_utc_time(bank_changed_at)");
                    table.CheckConstraint("ck_brands_39", "fx_fetched_at IS NULL OR akanti_utc_time(fx_fetched_at)");
                    table.CheckConstraint("ck_brands_4", "public_name IS NULL OR (length(public_name) BETWEEN 1 AND 160 AND trim(public_name)<>'')");
                    table.CheckConstraint("ck_brands_40", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_brands_41", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_brands_5", "market IN ('iran','international')");
                    table.CheckConstraint("ck_brands_6", "default_language IS NULL OR (length(default_language) BETWEEN 1 AND 160 AND trim(default_language)<>'')");
                    table.CheckConstraint("ck_brands_7", "logo_asset_ref IS NULL OR (length(logo_asset_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_brands_8", "bot_username IS NULL OR (length(bot_username) BETWEEN 1 AND 160 AND trim(bot_username)<>'')");
                    table.CheckConstraint("ck_brands_9", "bot_id > 0");
                    table.ForeignKey(
                        name: "brands_customer_support_admin_id_fk",
                        column: x => x.customer_support_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "brand_channels",
                columns: table => new
                {
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    chat_id = table.Column<long>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    invite_link = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    required = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand_channels", x => new { x.brand_id, x.chat_id });
                    table.CheckConstraint("ck_brand_channels_1", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_brand_channels_2", "chat_id < 0");
                    table.CheckConstraint("ck_brand_channels_3", "title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')");
                    table.CheckConstraint("ck_brand_channels_4", "invite_link IS NULL OR (length(invite_link) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_brand_channels_5", "required IS NULL OR (required IN (0,1))");
                    table.CheckConstraint("ck_brand_channels_6", "active IS NULL OR (active IN (0,1))");
                    table.CheckConstraint("ck_brand_channels_7", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_brand_channels_8", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.ForeignKey(
                        name: "brand_channels_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    batch_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    language = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    audience_kind = table.Column<string>(type: "TEXT", nullable: false),
                    active_since = table.Column<string>(type: "TEXT", nullable: true),
                    audience_language = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    message_text = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    media_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'draft'"),
                    created_by_admin_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    approved_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    started_at = table.Column<string>(type: "TEXT", nullable: true),
                    completed_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.id);
                    table.UniqueConstraint("AK_campaigns_id_batch_id_brand_id", x => new { x.id, x.batch_id, x.brand_id });
                    table.CheckConstraint("ck_campaigns_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_campaigns_10", "created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)");
                    table.CheckConstraint("ck_campaigns_11", "((message_text IS NOT NULL) + (media_ref IS NOT NULL))>0");
                    table.CheckConstraint("ck_campaigns_12", "(audience_kind='active_since')=(active_since IS NOT NULL)");
                    table.CheckConstraint("ck_campaigns_13", "(audience_kind='language')=(audience_language IS NOT NULL)");
                    table.CheckConstraint("ck_campaigns_14", "status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL");
                    table.CheckConstraint("ck_campaigns_15", "active_since IS NULL OR akanti_utc_time(active_since)");
                    table.CheckConstraint("ck_campaigns_16", "approved_at IS NULL OR akanti_utc_time(approved_at)");
                    table.CheckConstraint("ck_campaigns_17", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_campaigns_18", "started_at IS NULL OR akanti_utc_time(started_at)");
                    table.CheckConstraint("ck_campaigns_19", "completed_at IS NULL OR akanti_utc_time(completed_at)");
                    table.CheckConstraint("ck_campaigns_2", "batch_id IS NULL OR akanti_uuid(batch_id)");
                    table.CheckConstraint("ck_campaigns_3", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_campaigns_4", "language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')");
                    table.CheckConstraint("ck_campaigns_5", "audience_kind IN ('all','active_since','language')");
                    table.CheckConstraint("ck_campaigns_6", "audience_language IS NULL OR (length(audience_language) BETWEEN 1 AND 160 AND trim(audience_language)<>'')");
                    table.CheckConstraint("ck_campaigns_7", "message_text IS NULL OR (length(message_text)<=16384)");
                    table.CheckConstraint("ck_campaigns_8", "media_ref IS NULL OR (length(media_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_campaigns_9", "status IN ('draft','approved','running','completed','cancelled')");
                    table.ForeignKey(
                        name: "campaigns_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "campaigns_created_by_admin_id_fk",
                        column: x => x.created_by_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "legacy_id_map",
                columns: table => new
                {
                    source_brand = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    source_table = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    source_key_kind = table.Column<string>(type: "TEXT", nullable: false),
                    legacy_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    target_kind = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    target_brand_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    source_fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    import_batch = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    import_manifest_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    migrated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_id_map", x => new { x.source_brand, x.source_table, x.source_key_kind, x.legacy_id });
                    table.CheckConstraint("ck_legacy_id_map_1", "source_brand IS NULL OR (length(source_brand) BETWEEN 1 AND 160 AND trim(source_brand)<>'')");
                    table.CheckConstraint("ck_legacy_id_map_10", "import_manifest_ref IS NULL OR (length(import_manifest_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_legacy_id_map_11", "migrated_at IS NULL OR akanti_utc_time(migrated_at)");
                    table.CheckConstraint("ck_legacy_id_map_2", "source_table IS NULL OR (length(source_table) BETWEEN 1 AND 160 AND trim(source_table)<>'')");
                    table.CheckConstraint("ck_legacy_id_map_3", "source_key_kind IN ('primary','public_order_reference')");
                    table.CheckConstraint("ck_legacy_id_map_4", "legacy_id IS NULL OR (length(legacy_id) BETWEEN 1 AND 160 AND trim(legacy_id)<>'')");
                    table.CheckConstraint("ck_legacy_id_map_5", "target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')");
                    table.CheckConstraint("ck_legacy_id_map_6", "target_id IS NULL OR akanti_uuid(target_id)");
                    table.CheckConstraint("ck_legacy_id_map_7", "target_brand_id IS NULL OR akanti_uuid(target_brand_id)");
                    table.CheckConstraint("ck_legacy_id_map_8", "length(source_fingerprint)=32");
                    table.CheckConstraint("ck_legacy_id_map_9", "import_batch IS NULL OR akanti_uuid(import_batch)");
                    table.ForeignKey(
                        name: "legacy_id_map_target_brand_id_fk",
                        column: x => x.target_brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    actor_kind = table.Column<string>(type: "TEXT", nullable: false),
                    actor_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    actor_user_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    entity_kind = table.Column<string>(type: "TEXT", nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    reason = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    previous_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    new_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    command_sha256 = table.Column<byte[]>(type: "BLOB", nullable: true),
                    before_channel_enforcement = table.Column<bool>(type: "INTEGER", nullable: true),
                    after_channel_enforcement = table.Column<bool>(type: "INTEGER", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_audit_events_10", "previous_revision >= 0");
                    table.CheckConstraint("ck_audit_events_11", "new_revision >= 0");
                    table.CheckConstraint("ck_audit_events_12", "correlation_id IS NULL OR akanti_uuid(correlation_id)");
                    table.CheckConstraint("ck_audit_events_13", "length(command_sha256)=32");
                    table.CheckConstraint("ck_audit_events_14", "before_channel_enforcement IS NULL OR (before_channel_enforcement IN (0,1))");
                    table.CheckConstraint("ck_audit_events_15", "after_channel_enforcement IS NULL OR (after_channel_enforcement IN (0,1))");
                    table.CheckConstraint("ck_audit_events_16", "(actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR\n           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR\n           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)");
                    table.CheckConstraint("ck_audit_events_17", "(entity_kind='platform')=(entity_id IS NULL)");
                    table.CheckConstraint("ck_audit_events_18", "(action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR\n           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)");
                    table.CheckConstraint("ck_audit_events_19", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_audit_events_2", "actor_kind IN ('admin','customer','system')");
                    table.CheckConstraint("ck_audit_events_3", "actor_admin_id IS NULL OR akanti_uuid(actor_admin_id)");
                    table.CheckConstraint("ck_audit_events_4", "actor_user_id IS NULL OR akanti_uuid(actor_user_id)");
                    table.CheckConstraint("ck_audit_events_5", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_audit_events_6", "action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')");
                    table.CheckConstraint("ck_audit_events_7", "entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')");
                    table.CheckConstraint("ck_audit_events_8", "entity_id IS NULL OR akanti_uuid(entity_id)");
                    table.CheckConstraint("ck_audit_events_9", "reason IS NULL OR (length(reason)<=16384)");
                    table.ForeignKey(
                        name: "audit_events_actor_admin_id_fk",
                        column: x => x.actor_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "audit_events_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bot_activity_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_kind = table.Column<string>(type: "TEXT", nullable: false),
                    action_code = table.Column<string>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    support_ticket_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    quantity_selected = table.Column<int>(type: "INTEGER", nullable: true),
                    flow_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    telegram_update_id = table.Column<long>(type: "INTEGER", nullable: true),
                    outcome_code = table.Column<string>(type: "TEXT", nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    occurred_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_activity_events", x => x.id);
                    table.CheckConstraint("ck_bot_activity_events_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_bot_activity_events_10", "flow_revision >= 0");
                    table.CheckConstraint("ck_bot_activity_events_11", "telegram_update_id >= 0");
                    table.CheckConstraint("ck_bot_activity_events_12", "outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')");
                    table.CheckConstraint("ck_bot_activity_events_13", "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')");
                    table.CheckConstraint("ck_bot_activity_events_14", "(event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR\n           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR\n           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected')) OR\n           (event_type='support' AND action_code IN ('support_open','support_reply')) OR\n           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR\n           (event_type='discount' AND action_code='discounts_open') OR\n           (event_type='policy' AND action_code='policy_check') OR\n           (event_type='flow' AND action_code IN ('flow_expired','flow_abandoned')) OR\n           (event_type='payment' AND action_code IN ('topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled'))");
                    table.CheckConstraint("ck_bot_activity_events_15", "action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR product_id IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_16", "action_code NOT IN ('quantity_enter','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR quantity_selected IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_17", "action_code <> 'support_reply' OR support_ticket_id IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_18", "action_code NOT IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') OR order_id IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_19", "action_code NOT IN ('flow_expired','flow_abandoned','quantity_rejected') OR flow_revision IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_2", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_bot_activity_events_20", "action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)");
                    table.CheckConstraint("ck_bot_activity_events_21", "action_code <> 'checkout_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND order_id IS NULL AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL)");
                    table.CheckConstraint("ck_bot_activity_events_22", "action_code NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR (source_kind='system' AND\n        outcome_code=CASE WHEN action_code='checkout_funded' THEN 'accepted' ELSE 'cancelled' END)");
                    table.CheckConstraint("ck_bot_activity_events_23", "event_type<>'payment' OR (source_kind='system' AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL\n        AND outcome_code=CASE action_code WHEN 'payment_denied' THEN 'rejected' WHEN 'payment_review' THEN 'blocked' ELSE 'accepted' END)");
                    table.CheckConstraint("ck_bot_activity_events_24", "occurred_at IS NULL OR akanti_utc_time(occurred_at)");
                    table.CheckConstraint("ck_bot_activity_events_3", "event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow','payment')");
                    table.CheckConstraint("ck_bot_activity_events_4", "source_kind IN ('command','callback','flow','system')");
                    table.CheckConstraint("ck_bot_activity_events_5", "action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected','topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled','flow_abandoned')");
                    table.CheckConstraint("ck_bot_activity_events_6", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_bot_activity_events_7", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_bot_activity_events_8", "support_ticket_id IS NULL OR akanti_uuid(support_ticket_id)");
                    table.CheckConstraint("ck_bot_activity_events_9", "quantity_selected > 0");
                });

            migrationBuilder.CreateTable(
                name: "brand_products",
                columns: table => new
                {
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    visible = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    title_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'inherit'"),
                    title_override = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    description_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'inherit'"),
                    description_override = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    icon_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'inherit'"),
                    icon_override = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    guidance_mode = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'inherit'"),
                    guidance_override = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    price_usdt = table.Column<decimal>(type: "TEXT", nullable: false),
                    price_toman = table.Column<decimal>(type: "TEXT", nullable: true),
                    auto_price_toman = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    allow_wallet = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    allow_crypto = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    allow_bank = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    min_quantity = table.Column<int>(type: "INTEGER", nullable: false, defaultValueSql: "1"),
                    max_quantity = table.Column<int>(type: "INTEGER", nullable: false, defaultValueSql: "100"),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand_products", x => new { x.brand_id, x.product_id });
                    table.CheckConstraint("ck_brand_products_1", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_brand_products_10", "guidance_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_11", "guidance_override IS NULL OR (length(guidance_override)<=16384)");
                    table.CheckConstraint("ck_brand_products_12", "price_usdt IS NULL OR (akanti_decimal(price_usdt)=1)");
                    table.CheckConstraint("ck_brand_products_13", "akanti_decimal_compare(price_usdt,'0') > 0");
                    table.CheckConstraint("ck_brand_products_14", "price_toman IS NULL OR (akanti_decimal(price_toman)=1)");
                    table.CheckConstraint("ck_brand_products_15", "akanti_decimal_compare(price_toman,'0') > 0");
                    table.CheckConstraint("ck_brand_products_16", "auto_price_toman IS NULL OR (auto_price_toman IN (0,1))");
                    table.CheckConstraint("ck_brand_products_17", "allow_wallet IS NULL OR (allow_wallet IN (0,1))");
                    table.CheckConstraint("ck_brand_products_18", "allow_crypto IS NULL OR (allow_crypto IN (0,1))");
                    table.CheckConstraint("ck_brand_products_19", "allow_bank IS NULL OR (allow_bank IN (0,1))");
                    table.CheckConstraint("ck_brand_products_2", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_brand_products_20", "min_quantity > 0");
                    table.CheckConstraint("ck_brand_products_21", "max_quantity >= min_quantity");
                    table.CheckConstraint("ck_brand_products_22", "revision >= 0");
                    table.CheckConstraint("ck_brand_products_23", "(title_mode='override')=(title_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_24", "(description_mode='override')=(description_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_25", "(icon_mode='override')=(icon_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_26", "(guidance_mode='override')=(guidance_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_27", "NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL");
                    table.CheckConstraint("ck_brand_products_28", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_brand_products_3", "visible IS NULL OR (visible IN (0,1))");
                    table.CheckConstraint("ck_brand_products_4", "title_mode IN ('inherit','override')");
                    table.CheckConstraint("ck_brand_products_5", "title_override IS NULL OR (length(title_override) BETWEEN 1 AND 160 AND trim(title_override)<>'')");
                    table.CheckConstraint("ck_brand_products_6", "description_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_7", "description_override IS NULL OR (length(description_override)<=16384)");
                    table.CheckConstraint("ck_brand_products_8", "icon_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_9", "icon_override IS NULL OR (length(icon_override) BETWEEN 1 AND 1024)");
                    table.ForeignKey(
                        name: "brand_products_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discount_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    discount_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    revision_no = table.Column<long>(type: "INTEGER", nullable: false),
                    audit_event_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    percent_off = table.Column<decimal>(type: "TEXT", nullable: false),
                    max_total_uses = table.Column<long>(type: "INTEGER", nullable: true),
                    max_uses_per_user = table.Column<long>(type: "INTEGER", nullable: true),
                    usage_adjustment_units = table.Column<long>(type: "INTEGER", nullable: false),
                    redeemed_count_at_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    held_units_at_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    starts_at = table.Column<string>(type: "TEXT", nullable: true),
                    ends_at = table.Column<string>(type: "TEXT", nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    eligible_user_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_revisions", x => x.id);
                    table.CheckConstraint("ck_discount_revisions_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_discount_revisions_10", "held_units_at_revision >= 0");
                    table.CheckConstraint("ck_discount_revisions_11", "active IS NULL OR (active IN (0,1))");
                    table.CheckConstraint("ck_discount_revisions_12", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_discount_revisions_13", "eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)");
                    table.CheckConstraint("ck_discount_revisions_14", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                    table.CheckConstraint("ck_discount_revisions_15", "starts_at IS NULL OR akanti_utc_time(starts_at)");
                    table.CheckConstraint("ck_discount_revisions_16", "ends_at IS NULL OR akanti_utc_time(ends_at)");
                    table.CheckConstraint("ck_discount_revisions_2", "discount_id IS NULL OR akanti_uuid(discount_id)");
                    table.CheckConstraint("ck_discount_revisions_3", "revision_no >= 0");
                    table.CheckConstraint("ck_discount_revisions_4", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                    table.CheckConstraint("ck_discount_revisions_5", "percent_off IS NULL OR (akanti_decimal(percent_off)=1)");
                    table.CheckConstraint("ck_discount_revisions_6", "(akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)");
                    table.CheckConstraint("ck_discount_revisions_7", "max_total_uses >= 0");
                    table.CheckConstraint("ck_discount_revisions_8", "max_uses_per_user >= 0");
                    table.CheckConstraint("ck_discount_revisions_9", "redeemed_count_at_revision >= 0");
                    table.ForeignKey(
                        name: "discount_revisions_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discount_uses",
                columns: table => new
                {
                    discount_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    discount_revision_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    units_covered = table.Column<int>(type: "INTEGER", nullable: false),
                    saved_amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    saved_currency = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    transitioned_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_uses", x => new { x.discount_id, x.order_id });
                    table.CheckConstraint("ck_discount_uses_1", "discount_id IS NULL OR akanti_uuid(discount_id)");
                    table.CheckConstraint("ck_discount_uses_10", "transitioned_at IS NULL OR akanti_utc_time(transitioned_at)");
                    table.CheckConstraint("ck_discount_uses_2", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_discount_uses_3", "discount_revision_id IS NULL OR akanti_uuid(discount_revision_id)");
                    table.CheckConstraint("ck_discount_uses_4", "units_covered > 0");
                    table.CheckConstraint("ck_discount_uses_5", "saved_amount IS NULL OR (akanti_decimal(saved_amount)=1)");
                    table.CheckConstraint("ck_discount_uses_6", "akanti_decimal_compare(saved_amount,'0') >= 0");
                    table.CheckConstraint("ck_discount_uses_7", "saved_currency IN ('USD','TOMAN')");
                    table.CheckConstraint("ck_discount_uses_8", "status IN ('held','redeemed','released')");
                    table.CheckConstraint("ck_discount_uses_9", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.ForeignKey(
                        name: "discount_uses_discount_revision_id_fk",
                        column: x => x.discount_revision_id,
                        principalTable: "discount_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    code_digest = table.Column<byte[]>(type: "BLOB", nullable: false),
                    code_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    code_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    display_prefix = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    issuance_batch_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    percent_off = table.Column<decimal>(type: "TEXT", nullable: false),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    eligible_user_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    source_order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    max_total_uses = table.Column<long>(type: "INTEGER", nullable: true),
                    max_uses_per_user = table.Column<long>(type: "INTEGER", nullable: true),
                    redeemed_count = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    usage_adjustment_units = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    starts_at = table.Column<string>(type: "TEXT", nullable: true),
                    ends_at = table.Column<string>(type: "TEXT", nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    created_by_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discounts", x => x.id);
                    table.CheckConstraint("ck_discounts_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_discounts_10", "(akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)");
                    table.CheckConstraint("ck_discounts_11", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_discounts_12", "eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)");
                    table.CheckConstraint("ck_discounts_13", "source_order_id IS NULL OR akanti_uuid(source_order_id)");
                    table.CheckConstraint("ck_discounts_14", "max_total_uses >= 0");
                    table.CheckConstraint("ck_discounts_15", "max_uses_per_user >= 0");
                    table.CheckConstraint("ck_discounts_16", "redeemed_count >= 0");
                    table.CheckConstraint("ck_discounts_17", "active IS NULL OR (active IN (0,1))");
                    table.CheckConstraint("ck_discounts_18", "revision >= 0");
                    table.CheckConstraint("ck_discounts_19", "created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)");
                    table.CheckConstraint("ck_discounts_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_discounts_20", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                    table.CheckConstraint("ck_discounts_21", "(kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR\n           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)");
                    table.CheckConstraint("ck_discounts_22", "starts_at IS NULL OR akanti_utc_time(starts_at)");
                    table.CheckConstraint("ck_discounts_23", "ends_at IS NULL OR akanti_utc_time(ends_at)");
                    table.CheckConstraint("ck_discounts_24", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_discounts_3", "length(code_digest)=32");
                    table.CheckConstraint("ck_discounts_4", "length(code_ciphertext)>0");
                    table.CheckConstraint("ck_discounts_5", "code_key_id IS NULL OR (length(code_key_id) BETWEEN 1 AND 160 AND trim(code_key_id)<>'')");
                    table.CheckConstraint("ck_discounts_6", "display_prefix IS NULL OR (length(display_prefix) BETWEEN 1 AND 160 AND trim(display_prefix)<>'')");
                    table.CheckConstraint("ck_discounts_7", "issuance_batch_id IS NULL OR akanti_uuid(issuance_batch_id)");
                    table.CheckConstraint("ck_discounts_8", "kind IN ('prepaid_entitlement','goodwill','promotion')");
                    table.CheckConstraint("ck_discounts_9", "percent_off IS NULL OR (akanti_decimal(percent_off)=1)");
                    table.ForeignKey(
                        name: "discounts_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "discounts_created_by_admin_id_fk",
                        column: x => x.created_by_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fulfillments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    unit_no = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'unfilled'"),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    remote_item_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    supplier_item_key = table.Column<byte[]>(type: "BLOB", nullable: true),
                    delivered_content = table.Column<byte[]>(type: "BLOB", nullable: true),
                    delivered_content_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    content_kind = table.Column<string>(type: "TEXT", nullable: true),
                    resolution_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    resolution_note = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    manifest_generation = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    completed_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fulfillments", x => x.id);
                    table.CheckConstraint("ck_fulfillments_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_fulfillments_10", "content_kind IN ('account','key','code','text','file')");
                    table.CheckConstraint("ck_fulfillments_11", "resolution_admin_id IS NULL OR akanti_uuid(resolution_admin_id)");
                    table.CheckConstraint("ck_fulfillments_12", "resolution_note IS NULL OR (length(resolution_note)<=16384)");
                    table.CheckConstraint("ck_fulfillments_13", "manifest_generation IS NULL OR akanti_uuid(manifest_generation)");
                    table.CheckConstraint("ck_fulfillments_14", "kind IS NOT NULL OR status='unfilled'");
                    table.CheckConstraint("ck_fulfillments_15", "(kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)");
                    table.CheckConstraint("ck_fulfillments_16", "((delivered_content IS NOT NULL) + (delivered_content_key_id IS NOT NULL) + (content_kind IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_fulfillments_17", "status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL");
                    table.CheckConstraint("ck_fulfillments_18", "status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_fulfillments_19", "status <> 'retained_admin' OR resolution_admin_id IS NOT NULL");
                    table.CheckConstraint("ck_fulfillments_2", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_fulfillments_20", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_fulfillments_21", "completed_at IS NULL OR akanti_utc_time(completed_at)");
                    table.CheckConstraint("ck_fulfillments_3", "unit_no > 0");
                    table.CheckConstraint("ck_fulfillments_4", "kind IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_fulfillments_5", "status IN ('unfilled','acquired','ready','released','retained_admin')");
                    table.CheckConstraint("ck_fulfillments_6", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_fulfillments_7", "remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')");
                    table.CheckConstraint("ck_fulfillments_8", "length(supplier_item_key)=32");
                    table.CheckConstraint("ck_fulfillments_9", "delivered_content_key_id IS NULL OR (length(delivered_content_key_id) BETWEEN 1 AND 160 AND trim(delivered_content_key_id)<>'')");
                    table.ForeignKey(
                        name: "fulfillments_resolution_admin_id_fk",
                        column: x => x.resolution_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    credential_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    credential_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    source_fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    origin_kind = table.Column<string>(type: "TEXT", nullable: false),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    remote_item_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    supplier_item_key = table.Column<byte[]>(type: "BLOB", nullable: true),
                    state = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'available'"),
                    assigned_fulfillment_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    allocated_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_items", x => x.id);
                    table.CheckConstraint("ck_inventory_items_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_inventory_items_10", "state IN ('available','allocated','retired','admin_held')");
                    table.CheckConstraint("ck_inventory_items_11", "assigned_fulfillment_id IS NULL OR akanti_uuid(assigned_fulfillment_id)");
                    table.CheckConstraint("ck_inventory_items_12", "(state='allocated')=(assigned_fulfillment_id IS NOT NULL)");
                    table.CheckConstraint("ck_inventory_items_13", "(assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)");
                    table.CheckConstraint("ck_inventory_items_14", "(origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR\n           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')");
                    table.CheckConstraint("ck_inventory_items_15", "allocated_at IS NULL OR akanti_utc_time(allocated_at)");
                    table.CheckConstraint("ck_inventory_items_16", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_inventory_items_2", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_inventory_items_3", "length(credential_ciphertext)>0");
                    table.CheckConstraint("ck_inventory_items_4", "credential_key_id IS NULL OR (length(credential_key_id) BETWEEN 1 AND 160 AND trim(credential_key_id)<>'')");
                    table.CheckConstraint("ck_inventory_items_5", "length(source_fingerprint)=32");
                    table.CheckConstraint("ck_inventory_items_6", "origin_kind IN ('local_import','supplier_bonus')");
                    table.CheckConstraint("ck_inventory_items_7", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_inventory_items_8", "remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')");
                    table.CheckConstraint("ck_inventory_items_9", "length(supplier_item_key)=32");
                    table.ForeignKey(
                        name: "inventory_items_assigned_fulfillment_id_fk",
                        column: x => x.assigned_fulfillment_id,
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    job_kind = table.Column<string>(type: "TEXT", nullable: false),
                    payment_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    fulfillment_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    campaign_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    discount_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    support_message_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    telegram_delivery_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    product_stock_subscription_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    product_supplier_source_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    audit_event_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    origin_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'pending'"),
                    attempts = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    available_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    locked_by = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    lease_until = table.Column<string>(type: "TEXT", nullable: true),
                    dedupe_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    completed_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_jobs_10", "telegram_delivery_id IS NULL OR akanti_uuid(telegram_delivery_id)");
                    table.CheckConstraint("ck_jobs_11", "product_stock_subscription_id IS NULL OR akanti_uuid(product_stock_subscription_id)");
                    table.CheckConstraint("ck_jobs_12", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_jobs_13", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_jobs_14", "product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)");
                    table.CheckConstraint("ck_jobs_15", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                    table.CheckConstraint("ck_jobs_16", "origin_revision >= 0");
                    table.CheckConstraint("ck_jobs_17", "status IN ('pending','leased','succeeded','retry','failed','cancelled')");
                    table.CheckConstraint("ck_jobs_18", "attempts >= 0");
                    table.CheckConstraint("ck_jobs_19", "locked_by IS NULL OR (length(locked_by) BETWEEN 1 AND 160 AND trim(locked_by)<>'')");
                    table.CheckConstraint("ck_jobs_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_jobs_20", "dedupe_key IS NULL OR (length(dedupe_key) BETWEEN 1 AND 160 AND trim(dedupe_key)<>'')");
                    table.CheckConstraint("ck_jobs_21", "last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 160 AND trim(last_error_code)<>'')");
                    table.CheckConstraint("ck_jobs_22", "(status='leased')=(locked_by IS NOT NULL)");
                    table.CheckConstraint("ck_jobs_23", "(locked_by IS NULL)=(lease_until IS NULL)");
                    table.CheckConstraint("ck_jobs_24", "status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL");
                    table.CheckConstraint("ck_jobs_25", "available_at IS NULL OR akanti_utc_time(available_at)");
                    table.CheckConstraint("ck_jobs_26", "lease_until IS NULL OR akanti_utc_time(lease_until)");
                    table.CheckConstraint("ck_jobs_27", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_jobs_28", "completed_at IS NULL OR akanti_utc_time(completed_at)");
                    table.CheckConstraint("ck_jobs_3", "job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')");
                    table.CheckConstraint("ck_jobs_4", "payment_id IS NULL OR akanti_uuid(payment_id)");
                    table.CheckConstraint("ck_jobs_5", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_jobs_6", "fulfillment_id IS NULL OR akanti_uuid(fulfillment_id)");
                    table.CheckConstraint("ck_jobs_7", "campaign_id IS NULL OR akanti_uuid(campaign_id)");
                    table.CheckConstraint("ck_jobs_8", "discount_id IS NULL OR akanti_uuid(discount_id)");
                    table.CheckConstraint("ck_jobs_9", "support_message_id IS NULL OR akanti_uuid(support_message_id)");
                    table.ForeignKey(
                        name: "jobs_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_campaign_id_fk",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_discount_id_fk",
                        column: x => x.discount_id,
                        principalTable: "discounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_fulfillment_id_fk",
                        column: x => x.fulfillment_id,
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    order_kind = table.Column<string>(type: "TEXT", nullable: false),
                    payment_status = table.Column<string>(type: "TEXT", nullable: false),
                    fulfillment_status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'unfulfilled'"),
                    currency = table.Column<string>(type: "TEXT", nullable: false),
                    payment_method = table.Column<string>(type: "TEXT", nullable: false),
                    unit_price_usdt = table.Column<decimal>(type: "TEXT", nullable: false),
                    unit_price_toman = table.Column<decimal>(type: "TEXT", nullable: true),
                    fx_rate_snapshot = table.Column<decimal>(type: "TEXT", nullable: true),
                    fx_source_snapshot = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    fx_fetched_at_snapshot = table.Column<string>(type: "TEXT", nullable: true),
                    auto_price_toman_snapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    pricing_rounding_step_snapshot = table.Column<decimal>(type: "TEXT", nullable: false),
                    price_calculation_version = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    discount_pct_snapshot = table.Column<decimal>(type: "TEXT", nullable: true),
                    discount_units_snapshot = table.Column<int>(type: "INTEGER", nullable: true),
                    payment_due_at = table.Column<string>(type: "TEXT", nullable: true),
                    payment_confirmed_at = table.Column<string>(type: "TEXT", nullable: true),
                    brand_public_name_snapshot = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    product_title_snapshot = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    product_description_snapshot = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    product_icon_snapshot = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    product_guidance_snapshot = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    payment_terms_snapshot = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    fulfillment_kind_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    local_first_snapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_preorder = table.Column<bool>(type: "INTEGER", nullable: false),
                    review_code = table.Column<string>(type: "TEXT", nullable: true),
                    hold_at = table.Column<string>(type: "TEXT", nullable: true),
                    hold_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    hold_reason = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    resolution_kind = table.Column<string>(type: "TEXT", nullable: true),
                    resolution_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    resolution_at = table.Column<string>(type: "TEXT", nullable: true),
                    resolution_audit_event_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    resolution_wallet_entry_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    delivery_generation = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    manifest_asset_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    manifest_digest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    manifest_prepared_at = table.Column<string>(type: "TEXT", nullable: true),
                    release_authorized_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_orders_10", "payment_method IN ('wallet','crypto','bank','no_payment_required')");
                    table.CheckConstraint("ck_orders_11", "unit_price_usdt IS NULL OR (akanti_decimal(unit_price_usdt)=1)");
                    table.CheckConstraint("ck_orders_12", "akanti_decimal_compare(unit_price_usdt,'0') > 0");
                    table.CheckConstraint("ck_orders_13", "unit_price_toman IS NULL OR (akanti_decimal(unit_price_toman)=1)");
                    table.CheckConstraint("ck_orders_14", "akanti_decimal_compare(unit_price_toman,'0') > 0");
                    table.CheckConstraint("ck_orders_15", "fx_rate_snapshot IS NULL OR (akanti_decimal(fx_rate_snapshot)=1)");
                    table.CheckConstraint("ck_orders_16", "akanti_decimal_compare(fx_rate_snapshot,'0') > 0");
                    table.CheckConstraint("ck_orders_17", "fx_source_snapshot IS NULL OR (length(fx_source_snapshot) BETWEEN 1 AND 160 AND trim(fx_source_snapshot)<>'')");
                    table.CheckConstraint("ck_orders_18", "auto_price_toman_snapshot IS NULL OR (auto_price_toman_snapshot IN (0,1))");
                    table.CheckConstraint("ck_orders_19", "pricing_rounding_step_snapshot IS NULL OR (akanti_decimal(pricing_rounding_step_snapshot)=1)");
                    table.CheckConstraint("ck_orders_2", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_orders_20", "akanti_decimal_compare(pricing_rounding_step_snapshot,'0') > 0");
                    table.CheckConstraint("ck_orders_21", "price_calculation_version IS NULL OR (length(price_calculation_version) BETWEEN 1 AND 160 AND trim(price_calculation_version)<>'')");
                    table.CheckConstraint("ck_orders_22", "discount_pct_snapshot IS NULL OR (akanti_decimal(discount_pct_snapshot)=1)");
                    table.CheckConstraint("ck_orders_23", "(akanti_decimal_compare(discount_pct_snapshot,'0') >= 0 AND akanti_decimal_compare(discount_pct_snapshot,'100') <= 0)");
                    table.CheckConstraint("ck_orders_24", "discount_units_snapshot > 0");
                    table.CheckConstraint("ck_orders_25", "brand_public_name_snapshot IS NULL OR (length(brand_public_name_snapshot) BETWEEN 1 AND 160 AND trim(brand_public_name_snapshot)<>'')");
                    table.CheckConstraint("ck_orders_26", "product_title_snapshot IS NULL OR (length(product_title_snapshot) BETWEEN 1 AND 160 AND trim(product_title_snapshot)<>'')");
                    table.CheckConstraint("ck_orders_27", "product_description_snapshot IS NULL OR (length(product_description_snapshot)<=16384)");
                    table.CheckConstraint("ck_orders_28", "product_icon_snapshot IS NULL OR (length(product_icon_snapshot) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_orders_29", "product_guidance_snapshot IS NULL OR (length(product_guidance_snapshot)<=16384)");
                    table.CheckConstraint("ck_orders_3", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_orders_30", "payment_terms_snapshot IS NULL OR (length(payment_terms_snapshot)<=16384)");
                    table.CheckConstraint("ck_orders_31", "fulfillment_kind_snapshot IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_orders_32", "local_first_snapshot IS NULL OR (local_first_snapshot IN (0,1))");
                    table.CheckConstraint("ck_orders_33", "is_preorder IS NULL OR (is_preorder IN (0,1))");
                    table.CheckConstraint("ck_orders_34", "review_code IS NULL OR (review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'))");
                    table.CheckConstraint("ck_orders_35", "hold_admin_id IS NULL OR akanti_uuid(hold_admin_id)");
                    table.CheckConstraint("ck_orders_36", "hold_reason IS NULL OR (length(hold_reason)<=16384)");
                    table.CheckConstraint("ck_orders_37", "resolution_kind IN ('replaced','unpaid_expiry','local_preorder_cancellation','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','manual_fulfilled')");
                    table.CheckConstraint("ck_orders_38", "resolution_admin_id IS NULL OR akanti_uuid(resolution_admin_id)");
                    table.CheckConstraint("ck_orders_39", "resolution_audit_event_id IS NULL OR akanti_uuid(resolution_audit_event_id)");
                    table.CheckConstraint("ck_orders_4", "quantity > 0");
                    table.CheckConstraint("ck_orders_40", "resolution_wallet_entry_id IS NULL OR akanti_uuid(resolution_wallet_entry_id)");
                    table.CheckConstraint("ck_orders_41", "delivery_generation IS NULL OR akanti_uuid(delivery_generation)");
                    table.CheckConstraint("ck_orders_42", "manifest_asset_ref IS NULL OR (length(manifest_asset_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_orders_43", "length(manifest_digest)=32");
                    table.CheckConstraint("ck_orders_44", "((hold_at IS NOT NULL) + (hold_admin_id IS NOT NULL) + (hold_reason IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_orders_45", "((manifest_asset_ref IS NOT NULL) + (manifest_digest IS NOT NULL) + (manifest_prepared_at IS NOT NULL) + (delivery_generation IS NOT NULL)) IN (0,4)");
                    table.CheckConstraint("ck_orders_46", "release_authorized_at IS NULL OR (manifest_prepared_at IS NOT NULL AND release_authorized_at >= manifest_prepared_at)");
                    table.CheckConstraint("ck_orders_47", "((fx_rate_snapshot IS NOT NULL) + (fx_source_snapshot IS NOT NULL) + (fx_fetched_at_snapshot IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_orders_48", "((discount_pct_snapshot IS NOT NULL) + (discount_units_snapshot IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_orders_49", "discount_units_snapshot IS NULL OR discount_units_snapshot=quantity");
                    table.CheckConstraint("ck_orders_5", "revision >= 0");
                    table.CheckConstraint("ck_orders_50", "(currency='TOMAN')=(payment_method='bank')");
                    table.CheckConstraint("ck_orders_51", "currency <> 'TOMAN' OR unit_price_toman IS NOT NULL");
                    table.CheckConstraint("ck_orders_52", "payment_method <> 'no_payment_required' OR (payment_status='not_required' AND discount_pct_snapshot IS NOT NULL AND discount_pct_snapshot=100)");
                    table.CheckConstraint("ck_orders_53", "payment_status <> 'not_required' OR payment_method='no_payment_required'");
                    table.CheckConstraint("ck_orders_54", "payment_status NOT IN ('paid','not_required') OR payment_confirmed_at IS NOT NULL");
                    table.CheckConstraint("ck_orders_55", "payment_status NOT IN ('awaiting_payment','under_review','confirmation_pending') OR (payment_due_at IS NOT NULL AND payment_method IN ('crypto','bank'))");
                    table.CheckConstraint("ck_orders_56", "payment_due_at IS NULL OR payment_due_at=akanti_add_minutes(created_at,30)");
                    table.CheckConstraint("ck_orders_57", "NOT is_preorder OR (order_kind='product_purchase' AND fulfillment_kind_snapshot='local')");
                    table.CheckConstraint("ck_orders_58", "(resolution_kind IS NULL AND ((resolution_admin_id IS NOT NULL) + (resolution_at IS NOT NULL) + (resolution_audit_event_id IS NOT NULL) + (resolution_wallet_entry_id IS NOT NULL))=0) OR\n           (resolution_kind IS NOT NULL AND resolution_at IS NOT NULL)");
                    table.CheckConstraint("ck_orders_59", "resolution_kind NOT IN ('local_preorder_cancellation','supplier_wallet_credit','manual_fulfilled') OR\n           (resolution_admin_id IS NOT NULL AND resolution_audit_event_id IS NOT NULL)");
                    table.CheckConstraint("ck_orders_6", "order_kind IN ('product_purchase','prepaid_units')");
                    table.CheckConstraint("ck_orders_60", "resolution_kind NOT IN ('immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit') OR resolution_wallet_entry_id IS NOT NULL");
                    table.CheckConstraint("ck_orders_61", "resolution_kind <> 'local_preorder_cancellation' OR (is_preorder AND resolution_wallet_entry_id IS NULL AND fulfillment_status='cancelled')");
                    table.CheckConstraint("ck_orders_62", "resolution_kind NOT IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') OR fulfillment_status='compensated'");
                    table.CheckConstraint("ck_orders_63", "fx_fetched_at_snapshot IS NULL OR akanti_utc_time(fx_fetched_at_snapshot)");
                    table.CheckConstraint("ck_orders_64", "payment_due_at IS NULL OR akanti_utc_time(payment_due_at)");
                    table.CheckConstraint("ck_orders_65", "payment_confirmed_at IS NULL OR akanti_utc_time(payment_confirmed_at)");
                    table.CheckConstraint("ck_orders_66", "hold_at IS NULL OR akanti_utc_time(hold_at)");
                    table.CheckConstraint("ck_orders_67", "resolution_at IS NULL OR akanti_utc_time(resolution_at)");
                    table.CheckConstraint("ck_orders_68", "manifest_prepared_at IS NULL OR akanti_utc_time(manifest_prepared_at)");
                    table.CheckConstraint("ck_orders_69", "release_authorized_at IS NULL OR akanti_utc_time(release_authorized_at)");
                    table.CheckConstraint("ck_orders_7", "payment_status IN ('awaiting_payment','under_review','confirmation_pending','paid','not_required','expired','cancelled')");
                    table.CheckConstraint("ck_orders_70", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_orders_71", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_orders_8", "fulfillment_status IN ('unfulfilled','waiting_stock','acquiring','requires_admin','ready','delivering','completed','cancelled','compensated')");
                    table.CheckConstraint("ck_orders_9", "currency IN ('USDT','TOMAN')");
                    table.ForeignKey(
                        name: "orders_hold_admin_id_fk",
                        column: x => x.hold_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "orders_resolution_admin_id_fk",
                        column: x => x.resolution_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "orders_resolution_audit_event_id_fk",
                        column: x => x.resolution_audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    topup_user_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    payment_method = table.Column<string>(type: "TEXT", nullable: false),
                    quote_currency = table.Column<string>(type: "TEXT", nullable: false),
                    amount_due = table.Column<decimal>(type: "TEXT", nullable: false),
                    amount_received = table.Column<decimal>(type: "TEXT", nullable: true),
                    provider_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    provider_payment_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    hosted_invoice_started_at = table.Column<string>(type: "TEXT", nullable: true),
                    hosted_invoice_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    hosted_invoice_url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    idempotency_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    network_asset = table.Column<string>(type: "TEXT", nullable: true),
                    expected_network_amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    ipn_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ipn_signature = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ipn_payload_sha256 = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ipn_verified_at = table.Column<string>(type: "TEXT", nullable: true),
                    network_amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    provider_confirmations = table.Column<long>(type: "INTEGER", nullable: true),
                    first_payment_observed_at = table.Column<string>(type: "TEXT", nullable: true),
                    confirmed_at = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'created'"),
                    provider_status_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    bank_destination_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    bank_destination_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    receipt_asset_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    review_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    reviewed_at = table.Column<string>(type: "TEXT", nullable: true),
                    review_reason = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    compensating_wallet_entry_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    evidence_asset_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    pending_evidence_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    pending_evidence_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    evidence_digest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    legacy_kind = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.id);
                    table.CheckConstraint("ck_payments_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_payments_10", "provider_code IS NULL OR (length(provider_code) BETWEEN 1 AND 160 AND trim(provider_code)<>'')");
                    table.CheckConstraint("ck_payments_11", "provider_payment_ref IS NULL OR (length(provider_payment_ref) BETWEEN 1 AND 160 AND trim(provider_payment_ref)<>'')");
                    table.CheckConstraint("ck_payments_12", "hosted_invoice_ref IS NULL OR (length(hosted_invoice_ref) BETWEEN 1 AND 160 AND trim(hosted_invoice_ref)<>'')");
                    table.CheckConstraint("ck_payments_13", "length(hosted_invoice_ref) BETWEEN 1 AND 64 AND hosted_invoice_ref NOT GLOB '*[^0-9]*'");
                    table.CheckConstraint("ck_payments_14", "hosted_invoice_url IS NULL OR (length(hosted_invoice_url) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_payments_15", "idempotency_ref IS NULL OR (length(idempotency_ref) BETWEEN 1 AND 160 AND trim(idempotency_ref)<>'')");
                    table.CheckConstraint("ck_payments_16", "network_asset IN ('USDTTRC20','USDTBSC')");
                    table.CheckConstraint("ck_payments_17", "expected_network_amount IS NULL OR (akanti_decimal(expected_network_amount)=1)");
                    table.CheckConstraint("ck_payments_18", "akanti_decimal_compare(expected_network_amount,'0') > 0");
                    table.CheckConstraint("ck_payments_19", "ipn_key_id IS NULL OR (length(ipn_key_id) BETWEEN 1 AND 160 AND trim(ipn_key_id)<>'')");
                    table.CheckConstraint("ck_payments_2", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_payments_20", "length(ipn_signature)=64");
                    table.CheckConstraint("ck_payments_21", "length(ipn_payload_sha256)=32");
                    table.CheckConstraint("ck_payments_22", "network_amount IS NULL OR (akanti_decimal(network_amount)=1)");
                    table.CheckConstraint("ck_payments_23", "akanti_decimal_compare(network_amount,'0') >= 0");
                    table.CheckConstraint("ck_payments_24", "provider_confirmations >= 0");
                    table.CheckConstraint("ck_payments_25", "status IN ('created','awaiting','observed','confirming','settled','rejected','expired','review')");
                    table.CheckConstraint("ck_payments_26", "provider_status_code IS NULL OR (length(provider_status_code) BETWEEN 1 AND 160 AND trim(provider_status_code)<>'')");
                    table.CheckConstraint("ck_payments_27", "bank_destination_key_id IS NULL OR (length(bank_destination_key_id) BETWEEN 1 AND 160 AND trim(bank_destination_key_id)<>'')");
                    table.CheckConstraint("ck_payments_28", "receipt_asset_ref IS NULL OR (length(receipt_asset_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_payments_29", "review_admin_id IS NULL OR akanti_uuid(review_admin_id)");
                    table.CheckConstraint("ck_payments_3", "topup_user_id IS NULL OR akanti_uuid(topup_user_id)");
                    table.CheckConstraint("ck_payments_30", "review_reason IS NULL OR (length(review_reason)<=16384)");
                    table.CheckConstraint("ck_payments_31", "compensating_wallet_entry_id IS NULL OR akanti_uuid(compensating_wallet_entry_id)");
                    table.CheckConstraint("ck_payments_32", "evidence_asset_ref IS NULL OR (length(evidence_asset_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_payments_33", "length(pending_evidence_ciphertext) BETWEEN 1 AND 66000");
                    table.CheckConstraint("ck_payments_34", "pending_evidence_key_id IS NULL OR (length(pending_evidence_key_id) BETWEEN 1 AND 160 AND trim(pending_evidence_key_id)<>'')");
                    table.CheckConstraint("ck_payments_35", "length(evidence_digest)=32");
                    table.CheckConstraint("ck_payments_36", "legacy_kind IS NULL OR (length(legacy_kind) BETWEEN 1 AND 160 AND trim(legacy_kind)<>'')");
                    table.CheckConstraint("ck_payments_37", "((order_id IS NOT NULL) + (topup_user_id IS NOT NULL))=1");
                    table.CheckConstraint("ck_payments_38", "((bank_destination_ciphertext IS NOT NULL) + (bank_destination_key_id IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_payments_39", "((pending_evidence_ciphertext IS NOT NULL) + (pending_evidence_key_id IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_payments_4", "payment_method IN ('crypto','bank')");
                    table.CheckConstraint("ck_payments_40", "(evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL) = (evidence_digest IS NOT NULL)");
                    table.CheckConstraint("ck_payments_41", "pending_evidence_ciphertext IS NULL OR (evidence_asset_ref IS NULL AND order_id IS NOT NULL AND ipn_key_id IS NOT NULL AND first_payment_observed_at IS NOT NULL AND status IN ('confirming','review'))");
                    table.CheckConstraint("ck_payments_42", "((review_admin_id IS NOT NULL) + (reviewed_at IS NOT NULL) + (review_reason IS NOT NULL)) IN (0,3)");
                    table.CheckConstraint("ck_payments_43", "provider_payment_ref IS NULL OR provider_code IS NOT NULL");
                    table.CheckConstraint("ck_payments_44", "hosted_invoice_started_at IS NULL OR akanti_utc_time(hosted_invoice_started_at)");
                    table.CheckConstraint("ck_payments_45", "((hosted_invoice_ref IS NOT NULL) + (hosted_invoice_url IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_payments_46", "hosted_invoice_started_at IS NULL OR (payment_method='crypto' AND provider_code IS NOT DISTINCT FROM 'nowpayments')");
                    table.CheckConstraint("ck_payments_47", "hosted_invoice_ref IS NULL OR hosted_invoice_started_at IS NOT NULL");
                    table.CheckConstraint("ck_payments_48", "(payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination_ciphertext IS NULL) OR\n           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND (bank_destination_ciphertext IS NOT NULL OR coalesce(legacy_kind='manual_deposit',false)) AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)");
                    table.CheckConstraint("ck_payments_49", "topup_user_id IS NULL OR payment_method='crypto'");
                    table.CheckConstraint("ck_payments_5", "quote_currency IN ('USD','TOMAN')");
                    table.CheckConstraint("ck_payments_50", "status <> 'settled' OR confirmed_at IS NOT NULL");
                    table.CheckConstraint("ck_payments_51", "((ipn_key_id IS NOT NULL) + (ipn_signature IS NOT NULL) + (ipn_payload_sha256 IS NOT NULL) + (ipn_verified_at IS NOT NULL)) IN (0,4)");
                    table.CheckConstraint("ck_payments_52", "ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND (evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL))");
                    table.CheckConstraint("ck_payments_53", "expected_network_amount IS NULL OR payment_method='crypto'");
                    table.CheckConstraint("ck_payments_54", "ipn_verified_at IS NULL OR akanti_utc_time(ipn_verified_at)");
                    table.CheckConstraint("ck_payments_55", "first_payment_observed_at IS NULL OR akanti_utc_time(first_payment_observed_at)");
                    table.CheckConstraint("ck_payments_56", "confirmed_at IS NULL OR akanti_utc_time(confirmed_at)");
                    table.CheckConstraint("ck_payments_57", "reviewed_at IS NULL OR akanti_utc_time(reviewed_at)");
                    table.CheckConstraint("ck_payments_58", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_payments_6", "amount_due IS NULL OR (akanti_decimal(amount_due)=1)");
                    table.CheckConstraint("ck_payments_7", "akanti_decimal_compare(amount_due,'0') > 0");
                    table.CheckConstraint("ck_payments_8", "amount_received IS NULL OR (akanti_decimal(amount_received)=1)");
                    table.CheckConstraint("ck_payments_9", "akanti_decimal_compare(amount_received,'0') >= 0");
                    table.ForeignKey(
                        name: "payments_order_id_fk",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "payments_review_admin_id_fk",
                        column: x => x.review_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_stock_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    triggered_at = table.Column<string>(type: "TEXT", nullable: true),
                    cancelled_at = table.Column<string>(type: "TEXT", nullable: true),
                    closed_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stock_subscriptions", x => x.id);
                    table.CheckConstraint("ck_product_stock_subscriptions_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_product_stock_subscriptions_10", "closed_at IS NULL OR akanti_utc_time(closed_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_2", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_product_stock_subscriptions_3", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_product_stock_subscriptions_4", "triggered_at IS NULL OR triggered_at >= created_at");
                    table.CheckConstraint("ck_product_stock_subscriptions_5", "cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_6", "closed_at IS NULL OR closed_at >= created_at");
                    table.CheckConstraint("ck_product_stock_subscriptions_7", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_8", "triggered_at IS NULL OR akanti_utc_time(triggered_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_9", "cancelled_at IS NULL OR akanti_utc_time(cancelled_at)");
                });

            migrationBuilder.CreateTable(
                name: "product_supplier_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    product_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    provider_code = table.Column<string>(type: "TEXT", nullable: false),
                    external_product_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    external_variant_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "false"),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "1"),
                    observation_revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    observed_source_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    observed_account_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    observed_at = table.Column<string>(type: "TEXT", nullable: true),
                    availability_code = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'unknown'"),
                    available_quantity = table.Column<int>(type: "INTEGER", nullable: true),
                    observed_unit_cost = table.Column<decimal>(type: "TEXT", nullable: true),
                    observed_currency = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    eligibility_code = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'unknown'"),
                    quote_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    observation_error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_supplier_sources", x => x.id);
                    table.UniqueConstraint("AK_product_supplier_sources_product_id_id", x => new { x.product_id, x.id });
                    table.CheckConstraint("ck_product_supplier_sources_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_product_supplier_sources_10", "observed_account_ref IS NULL OR (length(observed_account_ref) BETWEEN 1 AND 160 AND trim(observed_account_ref)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_11", "availability_code IN ('unknown','available','out_of_stock','ineligible','error')");
                    table.CheckConstraint("ck_product_supplier_sources_12", "available_quantity >= 0");
                    table.CheckConstraint("ck_product_supplier_sources_13", "observed_unit_cost IS NULL OR (akanti_decimal(observed_unit_cost)=1)");
                    table.CheckConstraint("ck_product_supplier_sources_14", "akanti_decimal_compare(observed_unit_cost,'0') > 0");
                    table.CheckConstraint("ck_product_supplier_sources_15", "observed_currency IS NULL OR (length(observed_currency) BETWEEN 1 AND 160 AND trim(observed_currency)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_16", "eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')");
                    table.CheckConstraint("ck_product_supplier_sources_17", "quote_ref IS NULL OR (length(quote_ref) BETWEEN 1 AND 160 AND trim(quote_ref)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_18", "observation_error_code IS NULL OR (length(observation_error_code) BETWEEN 1 AND 160 AND trim(observation_error_code)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_19", "((observed_unit_cost IS NOT NULL) + (observed_currency IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_product_supplier_sources_2", "product_id IS NULL OR akanti_uuid(product_id)");
                    table.CheckConstraint("ck_product_supplier_sources_20", "availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)");
                    table.CheckConstraint("ck_product_supplier_sources_21", "availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')");
                    table.CheckConstraint("ck_product_supplier_sources_22", "availability_code <> 'out_of_stock' OR available_quantity=0");
                    table.CheckConstraint("ck_product_supplier_sources_23", "observed_at IS NULL OR akanti_utc_time(observed_at)");
                    table.CheckConstraint("ck_product_supplier_sources_24", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_product_supplier_sources_25", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_product_supplier_sources_3", "provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')");
                    table.CheckConstraint("ck_product_supplier_sources_4", "external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_5", "external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')");
                    table.CheckConstraint("ck_product_supplier_sources_6", "enabled IS NULL OR (enabled IN (0,1))");
                    table.CheckConstraint("ck_product_supplier_sources_7", "revision > 0");
                    table.CheckConstraint("ck_product_supplier_sources_8", "observation_revision >= 0");
                    table.CheckConstraint("ck_product_supplier_sources_9", "observed_source_revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    sku = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    fulfillment_kind = table.Column<string>(type: "TEXT", nullable: false),
                    local_first = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    availability_policy = table.Column<string>(type: "TEXT", nullable: false),
                    active_supplier_source_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    icon_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    guidance = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValueSql: "true"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                    table.CheckConstraint("ck_products_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_products_10", "icon_ref IS NULL OR (length(icon_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_products_11", "guidance IS NULL OR (length(guidance)<=16384)");
                    table.CheckConstraint("ck_products_12", "active IS NULL OR (active IN (0,1))");
                    table.CheckConstraint("ck_products_13", "availability_policy <> 'local_preorder' OR fulfillment_kind='local'");
                    table.CheckConstraint("ck_products_14", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_products_15", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_products_2", "sku IS NULL OR (length(sku) BETWEEN 1 AND 160 AND trim(sku)<>'')");
                    table.CheckConstraint("ck_products_3", "fulfillment_kind IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_products_4", "local_first IS NULL OR (local_first IN (0,1))");
                    table.CheckConstraint("ck_products_5", "availability_policy IN ('immediate','local_preorder')");
                    table.CheckConstraint("ck_products_6", "active_supplier_source_id IS NULL OR akanti_uuid(active_supplier_source_id)");
                    table.CheckConstraint("ck_products_7", "revision >= 0");
                    table.CheckConstraint("ck_products_8", "title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')");
                    table.CheckConstraint("ck_products_9", "description IS NULL OR (length(description)<=16384)");
                    table.ForeignKey(
                        name: "products_id_active_supplier_source_id_fk",
                        columns: x => new { x.id, x.active_supplier_source_id },
                        principalTable: "product_supplier_sources",
                        principalColumns: new[] { "product_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    requested_quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    product_supplier_source_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    source_revision = table.Column<long>(type: "INTEGER", nullable: true),
                    provider_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    account_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    external_product_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    external_variant_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    local_intent_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    remote_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    request_sha256 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'blocked'"),
                    revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    review_code = table.Column<string>(type: "TEXT", nullable: true),
                    next_check_at = table.Column<string>(type: "TEXT", nullable: true),
                    evidence_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    cost_baseline_amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    cost_currency = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    cost_quote_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    cost_observed_at = table.Column<string>(type: "TEXT", nullable: true),
                    approved_cost_ceiling = table.Column<decimal>(type: "TEXT", nullable: true),
                    approval_audit_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_requests", x => x.id);
                    table.CheckConstraint("ck_supplier_requests_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_supplier_requests_10", "local_intent_key IS NULL OR (length(local_intent_key) BETWEEN 1 AND 160 AND trim(local_intent_key)<>'')");
                    table.CheckConstraint("ck_supplier_requests_11", "remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')");
                    table.CheckConstraint("ck_supplier_requests_12", "length(request_sha256)=32");
                    table.CheckConstraint("ck_supplier_requests_13", "status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')");
                    table.CheckConstraint("ck_supplier_requests_14", "revision >= 0");
                    table.CheckConstraint("ck_supplier_requests_15", "review_code IS NULL OR (review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'))");
                    table.CheckConstraint("ck_supplier_requests_16", "evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_supplier_requests_17", "cost_baseline_amount IS NULL OR (akanti_decimal(cost_baseline_amount)=1)");
                    table.CheckConstraint("ck_supplier_requests_18", "akanti_decimal_compare(cost_baseline_amount,'0') > 0");
                    table.CheckConstraint("ck_supplier_requests_19", "cost_currency IS NULL OR (length(cost_currency) BETWEEN 1 AND 160 AND trim(cost_currency)<>'')");
                    table.CheckConstraint("ck_supplier_requests_2", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_supplier_requests_20", "cost_quote_ref IS NULL OR (length(cost_quote_ref) BETWEEN 1 AND 160 AND trim(cost_quote_ref)<>'')");
                    table.CheckConstraint("ck_supplier_requests_21", "approved_cost_ceiling IS NULL OR (akanti_decimal(approved_cost_ceiling)=1)");
                    table.CheckConstraint("ck_supplier_requests_22", "akanti_decimal_compare(approved_cost_ceiling,'0') > 0");
                    table.CheckConstraint("ck_supplier_requests_23", "approval_audit_id IS NULL OR akanti_uuid(approval_audit_id)");
                    table.CheckConstraint("ck_supplier_requests_24", "((cost_baseline_amount IS NOT NULL) + (cost_currency IS NOT NULL) + (cost_quote_ref IS NOT NULL) + (cost_observed_at IS NOT NULL)) IN (0,4)");
                    table.CheckConstraint("ck_supplier_requests_25", "((approved_cost_ceiling IS NOT NULL) + (approval_audit_id IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_supplier_requests_26", "status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))");
                    table.CheckConstraint("ck_supplier_requests_27", "status <> 'blocked' OR review_code IS NOT NULL");
                    table.CheckConstraint("ck_supplier_requests_28", "next_check_at IS NULL OR akanti_utc_time(next_check_at)");
                    table.CheckConstraint("ck_supplier_requests_29", "cost_observed_at IS NULL OR akanti_utc_time(cost_observed_at)");
                    table.CheckConstraint("ck_supplier_requests_3", "requested_quantity > 0");
                    table.CheckConstraint("ck_supplier_requests_30", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_supplier_requests_31", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_supplier_requests_4", "product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)");
                    table.CheckConstraint("ck_supplier_requests_5", "source_revision > 0");
                    table.CheckConstraint("ck_supplier_requests_6", "provider_code IS NULL OR (length(provider_code) BETWEEN 1 AND 160 AND trim(provider_code)<>'')");
                    table.CheckConstraint("ck_supplier_requests_7", "account_ref IS NULL OR (length(account_ref) BETWEEN 1 AND 160 AND trim(account_ref)<>'')");
                    table.CheckConstraint("ck_supplier_requests_8", "external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')");
                    table.CheckConstraint("ck_supplier_requests_9", "external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')");
                    table.ForeignKey(
                        name: "supplier_requests_approval_audit_id_fk",
                        column: x => x.approval_audit_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_requests_order_id_fk",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_requests_product_supplier_source_id_fk",
                        column: x => x.product_supplier_source_id,
                        principalTable: "product_supplier_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    telegram_id = table.Column<long>(type: "INTEGER", nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    first_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    last_name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    language = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    blocked_at = table.Column<string>(type: "TEXT", nullable: true),
                    banned_at = table.Column<string>(type: "TEXT", nullable: true),
                    marketing_opt_out_at = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    flow_state = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'idle'"),
                    flow_revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    flow_order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    flow_product_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    flow_anchor_message_id = table.Column<long>(type: "INTEGER", nullable: true),
                    flow_expires_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_users_10", "flow_order_id IS NULL OR akanti_uuid(flow_order_id)");
                    table.CheckConstraint("ck_users_11", "flow_product_id IS NULL OR akanti_uuid(flow_product_id)");
                    table.CheckConstraint("ck_users_12", "flow_anchor_message_id > 0");
                    table.CheckConstraint("ck_users_13", "blocked_at IS NULL OR akanti_utc_time(blocked_at)");
                    table.CheckConstraint("ck_users_14", "banned_at IS NULL OR akanti_utc_time(banned_at)");
                    table.CheckConstraint("ck_users_15", "marketing_opt_out_at IS NULL OR akanti_utc_time(marketing_opt_out_at)");
                    table.CheckConstraint("ck_users_16", "first_seen_at IS NULL OR akanti_utc_time(first_seen_at)");
                    table.CheckConstraint("ck_users_17", "last_seen_at IS NULL OR akanti_utc_time(last_seen_at)");
                    table.CheckConstraint("ck_users_18", "flow_expires_at IS NULL OR akanti_utc_time(flow_expires_at)");
                    table.CheckConstraint("ck_users_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_users_3", "telegram_id > 0");
                    table.CheckConstraint("ck_users_4", "username IS NULL OR (length(username) BETWEEN 1 AND 160 AND trim(username)<>'')");
                    table.CheckConstraint("ck_users_5", "first_name IS NULL OR (length(first_name) BETWEEN 1 AND 160 AND trim(first_name)<>'')");
                    table.CheckConstraint("ck_users_6", "last_name IS NULL OR (length(last_name) BETWEEN 1 AND 160 AND trim(last_name)<>'')");
                    table.CheckConstraint("ck_users_7", "language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')");
                    table.CheckConstraint("ck_users_8", "flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')");
                    table.CheckConstraint("ck_users_9", "flow_revision >= 0");
                    table.ForeignKey(
                        name: "users_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "users_flow_order_id_fk",
                        column: x => x.flow_order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "users_flow_product_id_fk",
                        column: x => x.flow_product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_request_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    attempt_no = table.Column<long>(type: "INTEGER", nullable: false),
                    operation = table.Column<string>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'in_flight'"),
                    parent_submit_attempt_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    admin_authorization_audit_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    purchase_idempotency_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    max_charge_amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    http_status = table.Column<int>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    remote_ref = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    evidence_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    started_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    finished_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_request_attempts", x => x.id);
                    table.CheckConstraint("ck_supplier_request_attempts_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_supplier_request_attempts_10", "akanti_decimal_compare(max_charge_amount,'0') > 0");
                    table.CheckConstraint("ck_supplier_request_attempts_11", "http_status BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_supplier_request_attempts_12", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 160 AND trim(error_code)<>'')");
                    table.CheckConstraint("ck_supplier_request_attempts_13", "remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')");
                    table.CheckConstraint("ck_supplier_request_attempts_14", "evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_supplier_request_attempts_15", "(outcome='in_flight')=(finished_at IS NULL)");
                    table.CheckConstraint("ck_supplier_request_attempts_16", "finished_at IS NULL OR finished_at >= started_at");
                    table.CheckConstraint("ck_supplier_request_attempts_17", "(operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR\n           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)");
                    table.CheckConstraint("ck_supplier_request_attempts_18", "started_at IS NULL OR akanti_utc_time(started_at)");
                    table.CheckConstraint("ck_supplier_request_attempts_19", "finished_at IS NULL OR akanti_utc_time(finished_at)");
                    table.CheckConstraint("ck_supplier_request_attempts_2", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_supplier_request_attempts_3", "attempt_no > 0");
                    table.CheckConstraint("ck_supplier_request_attempts_4", "operation IN ('submit','status_lookup','export')");
                    table.CheckConstraint("ck_supplier_request_attempts_5", "outcome IN ('in_flight','succeeded','failed','timeout','unknown')");
                    table.CheckConstraint("ck_supplier_request_attempts_6", "parent_submit_attempt_id IS NULL OR akanti_uuid(parent_submit_attempt_id)");
                    table.CheckConstraint("ck_supplier_request_attempts_7", "admin_authorization_audit_id IS NULL OR akanti_uuid(admin_authorization_audit_id)");
                    table.CheckConstraint("ck_supplier_request_attempts_8", "purchase_idempotency_key IS NULL OR (length(purchase_idempotency_key) BETWEEN 1 AND 160 AND trim(purchase_idempotency_key)<>'')");
                    table.CheckConstraint("ck_supplier_request_attempts_9", "max_charge_amount IS NULL OR (akanti_decimal(max_charge_amount)=1)");
                    table.ForeignKey(
                        name: "supplier_request_attempts_admin_authorization_audit_id_fk",
                        column: x => x.admin_authorization_audit_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_request_attempts_parent_submit_attempt_id_fk",
                        column: x => x.parent_submit_attempt_id,
                        principalTable: "supplier_request_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_request_attempts_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    category = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'open'"),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    closed_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_tickets", x => x.id);
                    table.CheckConstraint("ck_support_tickets_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_support_tickets_10", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_support_tickets_11", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.CheckConstraint("ck_support_tickets_12", "closed_at IS NULL OR akanti_utc_time(closed_at)");
                    table.CheckConstraint("ck_support_tickets_2", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_support_tickets_3", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_support_tickets_4", "category IN ('general','order','payment','preorder_cancellation')");
                    table.CheckConstraint("ck_support_tickets_5", "status IN ('open','assigned','waiting_customer','closed')");
                    table.CheckConstraint("ck_support_tickets_6", "source IN ('customer','admin','system')");
                    table.CheckConstraint("ck_support_tickets_7", "assigned_admin_id IS NULL OR akanti_uuid(assigned_admin_id)");
                    table.CheckConstraint("ck_support_tickets_8", "(status='closed')=(closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_support_tickets_9", "category <> 'preorder_cancellation' OR order_id IS NOT NULL");
                    table.ForeignKey(
                        name: "support_tickets_assigned_admin_id_fk",
                        column: x => x.assigned_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_order_id_fk",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_user_id_fk",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    balance = table.Column<decimal>(type: "TEXT", nullable: false, defaultValueSql: "0"),
                    version = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.user_id);
                    table.CheckConstraint("ck_wallets_1", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_wallets_2", "balance IS NULL OR (akanti_decimal(balance)=1)");
                    table.CheckConstraint("ck_wallets_3", "akanti_decimal_compare(balance,'0') >= 0");
                    table.CheckConstraint("ck_wallets_4", "version >= 0");
                    table.CheckConstraint("ck_wallets_5", "updated_at IS NULL OR akanti_utc_time(updated_at)");
                    table.ForeignKey(
                        name: "wallets_user_id_fk",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ticket_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    sender_kind = table.Column<string>(type: "TEXT", nullable: false),
                    sender_admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    sender_telegram_id = table.Column<long>(type: "INTEGER", nullable: true),
                    body_ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    body_key_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    asset_ref = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    telegram_message_id = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_messages", x => x.id);
                    table.CheckConstraint("ck_support_messages_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_support_messages_10", "((body_ciphertext IS NOT NULL) + (body_key_id IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_support_messages_11", "((body_ciphertext IS NOT NULL) + (asset_ref IS NOT NULL))>0");
                    table.CheckConstraint("ck_support_messages_12", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_support_messages_2", "ticket_id IS NULL OR akanti_uuid(ticket_id)");
                    table.CheckConstraint("ck_support_messages_3", "sender_kind IN ('customer','admin','system')");
                    table.CheckConstraint("ck_support_messages_4", "sender_admin_id IS NULL OR akanti_uuid(sender_admin_id)");
                    table.CheckConstraint("ck_support_messages_5", "sender_telegram_id > 0");
                    table.CheckConstraint("ck_support_messages_6", "body_key_id IS NULL OR (length(body_key_id) BETWEEN 1 AND 160 AND trim(body_key_id)<>'')");
                    table.CheckConstraint("ck_support_messages_7", "asset_ref IS NULL OR (length(asset_ref) BETWEEN 1 AND 1024)");
                    table.CheckConstraint("ck_support_messages_8", "telegram_message_id > 0");
                    table.CheckConstraint("ck_support_messages_9", "(sender_kind='admin')=(sender_admin_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "support_messages_sender_admin_id_fk",
                        column: x => x.sender_admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_messages_ticket_id_fk",
                        column: x => x.ticket_id,
                        principalTable: "support_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    signed_amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    balance_after = table.Column<decimal>(type: "TEXT", nullable: true),
                    posting_version = table.Column<long>(type: "INTEGER", nullable: true),
                    movement_kind = table.Column<string>(type: "TEXT", nullable: false),
                    is_posting = table.Column<bool>(type: "INTEGER", nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    payment_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    legacy_import_batch = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    admin_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    audit_event_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    reason = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_entries", x => x.id);
                    table.CheckConstraint("ck_wallet_entries_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_wallet_entries_10", "idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')");
                    table.CheckConstraint("ck_wallet_entries_11", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_wallet_entries_12", "payment_id IS NULL OR akanti_uuid(payment_id)");
                    table.CheckConstraint("ck_wallet_entries_13", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_wallet_entries_14", "legacy_import_batch IS NULL OR akanti_uuid(legacy_import_batch)");
                    table.CheckConstraint("ck_wallet_entries_15", "admin_id IS NULL OR akanti_uuid(admin_id)");
                    table.CheckConstraint("ck_wallet_entries_16", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                    table.CheckConstraint("ck_wallet_entries_17", "reason IS NULL OR (length(reason)<=16384)");
                    table.CheckConstraint("ck_wallet_entries_18", "(is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR\n           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)");
                    table.CheckConstraint("ck_wallet_entries_19", "movement_kind <> 'purchase_debit' OR akanti_decimal_compare(signed_amount,'0') < 0");
                    table.CheckConstraint("ck_wallet_entries_2", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_wallet_entries_20", "movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR akanti_decimal_compare(signed_amount,'0') > 0");
                    table.CheckConstraint("ck_wallet_entries_21", "(movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='legacy_history' AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND ((order_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND ((payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND ((supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR\n           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((payment_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0) OR\n           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0)");
                    table.CheckConstraint("ck_wallet_entries_22", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_wallet_entries_3", "signed_amount IS NULL OR (akanti_decimal(signed_amount)=1)");
                    table.CheckConstraint("ck_wallet_entries_4", "akanti_decimal_compare(signed_amount,'0') <> 0");
                    table.CheckConstraint("ck_wallet_entries_5", "balance_after IS NULL OR (akanti_decimal(balance_after)=1)");
                    table.CheckConstraint("ck_wallet_entries_6", "akanti_decimal_compare(balance_after,'0') >= 0");
                    table.CheckConstraint("ck_wallet_entries_7", "posting_version > 0");
                    table.CheckConstraint("ck_wallet_entries_8", "movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')");
                    table.CheckConstraint("ck_wallet_entries_9", "is_posting IS NULL OR (is_posting IN (0,1))");
                    table.ForeignKey(
                        name: "wallet_entries_admin_id_fk",
                        column: x => x.admin_id,
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_order_id_fk",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_payment_id_fk",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_user_id_fk",
                        column: x => x.user_id,
                        principalTable: "wallets",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telegram_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    brand_id = table.Column<Guid>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    recipient_kind = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    staff_chat_id = table.Column<long>(type: "INTEGER", nullable: true),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    target_message_id = table.Column<long>(type: "INTEGER", nullable: true),
                    part_no = table.Column<int>(type: "INTEGER", nullable: false, defaultValueSql: "1"),
                    delivery_generation = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    recipient_language = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    eligibility_checked_at = table.Column<string>(type: "TEXT", nullable: true),
                    dedupe_key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'pending'"),
                    attempts = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                    next_attempt_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    last_attempt_at = table.Column<string>(type: "TEXT", nullable: true),
                    telegram_message_id = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    sent_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "(strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')"),
                    campaign_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    campaign_batch_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    discount_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    order_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    fulfillment_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    support_message_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    product_stock_subscription_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    supplier_request_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    audit_event_id = table.Column<Guid>(type: "TEXT", nullable: true, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_deliveries", x => x.id);
                    table.CheckConstraint("ck_telegram_deliveries_1", "id IS NULL OR akanti_uuid(id)");
                    table.CheckConstraint("ck_telegram_deliveries_10", "delivery_generation IS NULL OR akanti_uuid(delivery_generation)");
                    table.CheckConstraint("ck_telegram_deliveries_11", "recipient_language IS NULL OR (length(recipient_language) BETWEEN 1 AND 160 AND trim(recipient_language)<>'')");
                    table.CheckConstraint("ck_telegram_deliveries_12", "dedupe_key IS NULL OR (length(dedupe_key) BETWEEN 1 AND 160 AND trim(dedupe_key)<>'')");
                    table.CheckConstraint("ck_telegram_deliveries_13", "status IN ('pending','leased','succeeded','retry','failed','cancelled','uncertain')");
                    table.CheckConstraint("ck_telegram_deliveries_14", "attempts >= 0");
                    table.CheckConstraint("ck_telegram_deliveries_15", "telegram_message_id > 0");
                    table.CheckConstraint("ck_telegram_deliveries_16", "last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 160 AND trim(last_error_code)<>'')");
                    table.CheckConstraint("ck_telegram_deliveries_17", "campaign_id IS NULL OR akanti_uuid(campaign_id)");
                    table.CheckConstraint("ck_telegram_deliveries_18", "campaign_batch_id IS NULL OR akanti_uuid(campaign_batch_id)");
                    table.CheckConstraint("ck_telegram_deliveries_19", "discount_id IS NULL OR akanti_uuid(discount_id)");
                    table.CheckConstraint("ck_telegram_deliveries_2", "brand_id IS NULL OR akanti_uuid(brand_id)");
                    table.CheckConstraint("ck_telegram_deliveries_20", "order_id IS NULL OR akanti_uuid(order_id)");
                    table.CheckConstraint("ck_telegram_deliveries_21", "fulfillment_id IS NULL OR akanti_uuid(fulfillment_id)");
                    table.CheckConstraint("ck_telegram_deliveries_22", "support_message_id IS NULL OR akanti_uuid(support_message_id)");
                    table.CheckConstraint("ck_telegram_deliveries_23", "product_stock_subscription_id IS NULL OR akanti_uuid(product_stock_subscription_id)");
                    table.CheckConstraint("ck_telegram_deliveries_24", "supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)");
                    table.CheckConstraint("ck_telegram_deliveries_25", "audit_event_id IS NULL OR akanti_uuid(audit_event_id)");
                    table.CheckConstraint("ck_telegram_deliveries_26", "(recipient_kind='customer_dm' AND user_id IS NOT NULL AND staff_chat_id IS NULL AND purpose NOT IN ('staff_supplier_case','staff_supplier_bonus')) OR\n           (recipient_kind='staff_chat' AND user_id IS NULL AND staff_chat_id IS NOT NULL AND purpose IN ('staff_supplier_case','staff_supplier_bonus'))");
                    table.CheckConstraint("ck_telegram_deliveries_27", "action <> 'edit' OR target_message_id IS NOT NULL");
                    table.CheckConstraint("ck_telegram_deliveries_28", "status <> 'succeeded' OR sent_at IS NOT NULL");
                    table.CheckConstraint("ck_telegram_deliveries_29", "((campaign_id IS NOT NULL) + (campaign_batch_id IS NOT NULL)) IN (0,2)");
                    table.CheckConstraint("ck_telegram_deliveries_3", "recipient_kind IN ('customer_dm','staff_chat')");
                    table.CheckConstraint("ck_telegram_deliveries_30", "eligibility_checked_at IS NULL OR akanti_utc_time(eligibility_checked_at)");
                    table.CheckConstraint("ck_telegram_deliveries_31", "next_attempt_at IS NULL OR akanti_utc_time(next_attempt_at)");
                    table.CheckConstraint("ck_telegram_deliveries_32", "last_attempt_at IS NULL OR akanti_utc_time(last_attempt_at)");
                    table.CheckConstraint("ck_telegram_deliveries_33", "sent_at IS NULL OR akanti_utc_time(sent_at)");
                    table.CheckConstraint("ck_telegram_deliveries_34", "created_at IS NULL OR akanti_utc_time(created_at)");
                    table.CheckConstraint("ck_telegram_deliveries_4", "user_id IS NULL OR akanti_uuid(user_id)");
                    table.CheckConstraint("ck_telegram_deliveries_5", "staff_chat_id < 0");
                    table.CheckConstraint("ck_telegram_deliveries_6", "purpose IN ('order_credentials','order_status','campaign','discount_notice','support_notice','restock_notice','staff_supplier_case','staff_supplier_bonus')");
                    table.CheckConstraint("ck_telegram_deliveries_7", "action IN ('send','edit')");
                    table.CheckConstraint("ck_telegram_deliveries_8", "target_message_id > 0");
                    table.CheckConstraint("ck_telegram_deliveries_9", "part_no > 0");
                    table.ForeignKey(
                        name: "telegram_deliveries_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_brand_id_fk",
                        column: x => x.brand_id,
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_campaign_id_campaign_batch_id_brand_id_fk",
                        columns: x => new { x.campaign_id, x.campaign_batch_id, x.brand_id },
                        principalTable: "campaigns",
                        principalColumns: new[] { "id", "batch_id", "brand_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_discount_id_fk",
                        column: x => x.discount_id,
                        principalTable: "discounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_fulfillment_id_fk",
                        column: x => x.fulfillment_id,
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_order_id_fk",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_product_stock_subscription_id_fk",
                        column: x => x.product_stock_subscription_id,
                        principalTable: "product_stock_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_support_message_id_fk",
                        column: x => x.support_message_id,
                        principalTable: "support_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_user_id_fk",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_admin_id",
                table: "admin_sessions",
                column: "admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_token_hash",
                table: "admin_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_login",
                table: "admins",
                column: "login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_user_id",
                table: "admins",
                column: "telegram_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_verification_audit_id",
                table: "admins",
                column: "telegram_verification_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_verified_by_admin_id",
                table: "admins",
                column: "telegram_verified_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_actor_admin_id",
                table: "audit_events",
                column: "actor_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_actor_user_id",
                table: "audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_brand_id",
                table: "audit_events",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "activity_member_time",
                table: "bot_activity_events",
                columns: new[] { "user_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "activity_retention",
                table: "bot_activity_events",
                columns: new[] { "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_order_id",
                table: "bot_activity_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_product_id",
                table: "bot_activity_events",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_support_ticket_id",
                table: "bot_activity_events",
                column: "support_ticket_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_user_id_idempotency_key",
                table: "bot_activity_events",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brand_products_product_id",
                table: "brand_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "brands_distinct_iran_card",
                table: "brands",
                column: "bank_card_fingerprint",
                unique: true,
                filter: "market='iran' AND allow_bank");

            migrationBuilder.CreateIndex(
                name: "IX_brands_bot_id",
                table: "brands",
                column: "bot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brands_code",
                table: "brands",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brands_customer_support_admin_id",
                table: "brands",
                column: "customer_support_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_batch_id_brand_id_language",
                table: "campaigns",
                columns: new[] { "batch_id", "brand_id", "language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_brand_id",
                table: "campaigns",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_created_by_admin_id",
                table: "campaigns",
                column: "created_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_id_batch_id_brand_id",
                table: "campaigns",
                columns: new[] { "id", "batch_id", "brand_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_audit_event_id",
                table: "discount_revisions",
                column: "audit_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_discount_id_revision_no",
                table: "discount_revisions",
                columns: new[] { "discount_id", "revision_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_eligible_user_id",
                table: "discount_revisions",
                column: "eligible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_product_id",
                table: "discount_revisions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_uses_discount_revision_id",
                table: "discount_uses",
                column: "discount_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_uses_order_id",
                table: "discount_uses",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "discount_prepaid_source_once",
                table: "discounts",
                column: "source_order_id",
                unique: true,
                filter: "kind='prepaid_entitlement'");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_brand_id_code_digest",
                table: "discounts",
                columns: new[] { "brand_id", "code_digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discounts_created_by_admin_id",
                table: "discounts",
                column: "created_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_eligible_user_id",
                table: "discounts",
                column: "eligible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_product_id",
                table: "discounts",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_order_id_unit_no",
                table: "fulfillments",
                columns: new[] { "order_id", "unit_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_resolution_admin_id",
                table: "fulfillments",
                column: "resolution_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_supplier_item_key",
                table: "fulfillments",
                column: "supplier_item_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_supplier_request_id",
                table: "fulfillments",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "inventory_available_product",
                table: "inventory_items",
                columns: new[] { "product_id", "created_at", "id" },
                filter: "state='available'");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_assigned_fulfillment_id",
                table: "inventory_items",
                column: "assigned_fulfillment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_supplier_item_key",
                table: "inventory_items",
                column: "supplier_item_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_supplier_request_id",
                table: "inventory_items",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_audit_event_id",
                table: "jobs",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_brand_id",
                table: "jobs",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_campaign_id",
                table: "jobs",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_dedupe_key",
                table: "jobs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_discount_id",
                table: "jobs",
                column: "discount_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_fulfillment_id",
                table: "jobs",
                column: "fulfillment_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_order_id",
                table: "jobs",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_payment_id",
                table: "jobs",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_id",
                table: "jobs",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_stock_subscription_id",
                table: "jobs",
                column: "product_stock_subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_supplier_source_id",
                table: "jobs",
                column: "product_supplier_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_supplier_request_id",
                table: "jobs",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_support_message_id",
                table: "jobs",
                column: "support_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_telegram_delivery_id",
                table: "jobs",
                column: "telegram_delivery_id");

            migrationBuilder.CreateIndex(
                name: "jobs_claimable",
                table: "jobs",
                columns: new[] { "available_at", "id" },
                filter: "status IN ('pending','retry')");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_id_map_target_brand_id",
                table: "legacy_id_map",
                column: "target_brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_hold_admin_id",
                table: "orders",
                column: "hold_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_id_delivery_generation",
                table: "orders",
                columns: new[] { "id", "delivery_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_admin_id",
                table: "orders",
                column: "resolution_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_audit_event_id",
                table: "orders",
                column: "resolution_audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_wallet_entry_id",
                table: "orders",
                column: "resolution_wallet_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "orders_one_awaiting",
                table: "orders",
                column: "user_id",
                unique: true,
                filter: "payment_status='awaiting_payment'");

            migrationBuilder.CreateIndex(
                name: "orders_paid_preorder_fifo",
                table: "orders",
                columns: new[] { "product_id", "payment_confirmed_at", "id" },
                filter: "is_preorder AND payment_status IN ('paid','not_required') AND fulfillment_status IN ('waiting_stock','requires_admin')");

            migrationBuilder.CreateIndex(
                name: "IX_payments_compensating_wallet_entry_id",
                table: "payments",
                column: "compensating_wallet_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_hosted_invoice_ref",
                table: "payments",
                column: "hosted_invoice_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_idempotency_ref",
                table: "payments",
                column: "idempotency_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_order_id",
                table: "payments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_review_admin_id",
                table: "payments",
                column: "review_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_topup_user_id",
                table: "payments",
                column: "topup_user_id");

            migrationBuilder.CreateIndex(
                name: "payments_provider_identity",
                table: "payments",
                columns: new[] { "provider_code", "provider_payment_ref" },
                unique: true,
                filter: "provider_payment_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_stock_subscriptions_product_id",
                table: "product_stock_subscriptions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "subscription_active_once",
                table: "product_stock_subscriptions",
                columns: new[] { "user_id", "product_id" },
                unique: true,
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "subscription_closed_retention",
                table: "product_stock_subscriptions",
                columns: new[] { "closed_at", "id" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_supplier_sources_product_id_id",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "source_with_variant",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "provider_code", "external_product_id", "external_variant_id" },
                unique: true,
                filter: "external_variant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "source_without_variant",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "provider_code", "external_product_id" },
                unique: true,
                filter: "external_variant_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_products_id_active_supplier_source_id",
                table: "products",
                columns: new[] { "id", "active_supplier_source_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_sku",
                table: "products",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_admin_authorization_audit_id",
                table: "supplier_request_attempts",
                column: "admin_authorization_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_parent_submit_attempt_id",
                table: "supplier_request_attempts",
                column: "parent_submit_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_supplier_request_id_attempt_no",
                table: "supplier_request_attempts",
                columns: new[] { "supplier_request_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_approval_audit_id",
                table: "supplier_requests",
                column: "approval_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_local_intent_key",
                table: "supplier_requests",
                column: "local_intent_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_product_supplier_source_id",
                table: "supplier_requests",
                column: "product_supplier_source_id");

            migrationBuilder.CreateIndex(
                name: "supplier_order_unresolved",
                table: "supplier_requests",
                column: "order_id",
                unique: true,
                filter: "status NOT IN ('failed','resolved','succeeded')");

            migrationBuilder.CreateIndex(
                name: "IX_support_messages_sender_admin_id",
                table: "support_messages",
                column: "sender_admin_id");

            migrationBuilder.CreateIndex(
                name: "support_external_message_once",
                table: "support_messages",
                columns: new[] { "ticket_id", "telegram_message_id" },
                unique: true,
                filter: "telegram_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_assigned_admin_id",
                table: "support_tickets",
                column: "assigned_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_order_id",
                table: "support_tickets",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_user_id",
                table: "support_tickets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "delivery_campaign_recipient_once",
                table: "telegram_deliveries",
                columns: new[] { "campaign_batch_id", "user_id" },
                unique: true,
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "delivery_manifest_part_once",
                table: "telegram_deliveries",
                columns: new[] { "order_id", "delivery_generation", "part_no", "purpose" },
                unique: true,
                filter: "purpose='order_credentials'");

            migrationBuilder.CreateIndex(
                name: "delivery_restock_once",
                table: "telegram_deliveries",
                column: "product_stock_subscription_id",
                unique: true,
                filter: "product_stock_subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "delivery_staff_case_once",
                table: "telegram_deliveries",
                columns: new[] { "supplier_request_id", "audit_event_id", "delivery_generation", "purpose" },
                unique: true,
                filter: "recipient_kind='staff_chat'");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_audit_event_id",
                table: "telegram_deliveries",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_brand_id",
                table: "telegram_deliveries",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_campaign_id_campaign_batch_id_brand_id",
                table: "telegram_deliveries",
                columns: new[] { "campaign_id", "campaign_batch_id", "brand_id" });

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_dedupe_key",
                table: "telegram_deliveries",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_discount_id",
                table: "telegram_deliveries",
                column: "discount_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_fulfillment_id",
                table: "telegram_deliveries",
                column: "fulfillment_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_support_message_id",
                table: "telegram_deliveries",
                column: "support_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_user_id",
                table: "telegram_deliveries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_brand_id_telegram_id",
                table: "users",
                columns: new[] { "brand_id", "telegram_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_flow_order_id",
                table: "users",
                column: "flow_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_flow_product_id",
                table: "users",
                column: "flow_product_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_id_brand_id",
                table: "users",
                columns: new[] { "id", "brand_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_admin_id",
                table: "wallet_entries",
                column: "admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_audit_event_id",
                table: "wallet_entries",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_supplier_request_id",
                table: "wallet_entries",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_user_id_idempotency_key",
                table: "wallet_entries",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "wallet_opening_once",
                table: "wallet_entries",
                column: "user_id",
                unique: true,
                filter: "movement_kind='opening_balance' AND is_posting");

            migrationBuilder.CreateIndex(
                name: "wallet_payment_credit_once",
                table: "wallet_entries",
                column: "payment_id",
                unique: true,
                filter: "movement_kind IN ('wallet_topup','immediate_crypto_shortage_credit','late_crypto_credit')");

            migrationBuilder.CreateIndex(
                name: "wallet_posting_version",
                table: "wallet_entries",
                columns: new[] { "user_id", "posting_version" },
                unique: true,
                filter: "is_posting");

            migrationBuilder.CreateIndex(
                name: "wallet_supplier_resolution_once",
                table: "wallet_entries",
                column: "order_id",
                unique: true,
                filter: "movement_kind='supplier_wallet_credit'");

            migrationBuilder.AddForeignKey(
                name: "admin_sessions_admin_id_fk",
                table: "admin_sessions",
                column: "admin_id",
                principalTable: "admins",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "admins_telegram_verification_audit_id_fk",
                table: "admins",
                column: "telegram_verification_audit_id",
                principalTable: "audit_events",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "audit_events_actor_user_id_fk",
                table: "audit_events",
                column: "actor_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_order_id_fk",
                table: "bot_activity_events",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_product_id_fk",
                table: "bot_activity_events",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_support_ticket_id_fk",
                table: "bot_activity_events",
                column: "support_ticket_id",
                principalTable: "support_tickets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_user_id_fk",
                table: "bot_activity_events",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "brand_products_product_id_fk",
                table: "brand_products",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_discount_id_fk",
                table: "discount_revisions",
                column: "discount_id",
                principalTable: "discounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_eligible_user_id_fk",
                table: "discount_revisions",
                column: "eligible_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_product_id_fk",
                table: "discount_revisions",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_uses_discount_id_fk",
                table: "discount_uses",
                column: "discount_id",
                principalTable: "discounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_uses_order_id_fk",
                table: "discount_uses",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_eligible_user_id_fk",
                table: "discounts",
                column: "eligible_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_product_id_fk",
                table: "discounts",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_source_order_id_fk",
                table: "discounts",
                column: "source_order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fulfillments_order_id_fk",
                table: "fulfillments",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fulfillments_supplier_request_id_fk",
                table: "fulfillments",
                column: "supplier_request_id",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "inventory_items_product_id_fk",
                table: "inventory_items",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "inventory_items_supplier_request_id_fk",
                table: "inventory_items",
                column: "supplier_request_id",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_order_id_fk",
                table: "jobs",
                column: "order_id",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_payment_id_fk",
                table: "jobs",
                column: "payment_id",
                principalTable: "payments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_id_fk",
                table: "jobs",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_stock_subscription_id_fk",
                table: "jobs",
                column: "product_stock_subscription_id",
                principalTable: "product_stock_subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_supplier_source_id_fk",
                table: "jobs",
                column: "product_supplier_source_id",
                principalTable: "product_supplier_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_supplier_request_id_fk",
                table: "jobs",
                column: "supplier_request_id",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_support_message_id_fk",
                table: "jobs",
                column: "support_message_id",
                principalTable: "support_messages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_telegram_delivery_id_fk",
                table: "jobs",
                column: "telegram_delivery_id",
                principalTable: "telegram_deliveries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_product_id_fk",
                table: "orders",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_resolution_wallet_entry_id_fk",
                table: "orders",
                column: "resolution_wallet_entry_id",
                principalTable: "wallet_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_user_id_fk",
                table: "orders",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "payments_compensating_wallet_entry_id_fk",
                table: "payments",
                column: "compensating_wallet_entry_id",
                principalTable: "wallet_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "payments_topup_user_id_fk",
                table: "payments",
                column: "topup_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_stock_subscriptions_product_id_fk",
                table: "product_stock_subscriptions",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_stock_subscriptions_user_id_fk",
                table: "product_stock_subscriptions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_supplier_sources_product_id_fk",
                table: "product_supplier_sources",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(EfMigrationGuards.SqliteBaseline);
            migrationBuilder.Sql("PRAGMA user_version=1;");
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The baseline cannot be downgraded; use explicit verified recovery.");
    }
}
