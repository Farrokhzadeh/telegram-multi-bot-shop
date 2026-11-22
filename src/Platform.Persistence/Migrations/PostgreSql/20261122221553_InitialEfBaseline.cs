using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Persistence.Migrations.PostgreSql
{
    public partial class InitialEfBaseline : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shop");

            migrationBuilder.CreateTable(
                name: "admin_sessions",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    auth_version_at_issue = table.Column<long>(type: "bigint", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sessions", x => x.id);
                    table.CheckConstraint("ck_admin_sessions_1", "octet_length(token_hash)=32");
                    table.CheckConstraint("ck_admin_sessions_2", "auth_version_at_issue >= 0");
                    table.CheckConstraint("ck_admin_sessions_3", "expires_at > issued_at");
                    table.CheckConstraint("ck_admin_sessions_4", "revoked_at IS NULL OR revoked_at >= issued_at");
                    table.CheckConstraint("ck_admin_sessions_5", "isfinite(issued_at)");
                    table.CheckConstraint("ck_admin_sessions_6", "isfinite(expires_at)");
                    table.CheckConstraint("ck_admin_sessions_7", "isfinite(revoked_at)");
                    table.CheckConstraint("ck_admin_sessions_8", "isfinite(last_used_at)");
                });

            migrationBuilder.CreateTable(
                name: "admins",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    login = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    authentication_mode = table.Column<string>(type: "text", nullable: false),
                    password_verifier = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    external_subject = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    disabled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    auth_version = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_login_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: true),
                    public_contact_handle = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    telegram_verified_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    telegram_verified_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    telegram_verification_audit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    binding_challenge_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    binding_candidate_telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    binding_expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admins", x => x.id);
                    table.CheckConstraint("ck_admins_1", "authentication_mode IN ('password','external')");
                    table.CheckConstraint("ck_admins_10", "isfinite(disabled_at)");
                    table.CheckConstraint("ck_admins_11", "isfinite(created_at)");
                    table.CheckConstraint("ck_admins_12", "isfinite(last_login_at)");
                    table.CheckConstraint("ck_admins_13", "isfinite(telegram_verified_at)");
                    table.CheckConstraint("ck_admins_14", "isfinite(binding_expires_at)");
                    table.CheckConstraint("ck_admins_15", "length(login) BETWEEN 1 AND 160 AND btrim(login)<>''");
                    table.CheckConstraint("ck_admins_16", "length(password_verifier) BETWEEN 1 AND 160 AND btrim(password_verifier)<>''");
                    table.CheckConstraint("ck_admins_17", "length(external_subject) BETWEEN 1 AND 160 AND btrim(external_subject)<>''");
                    table.CheckConstraint("ck_admins_18", "length(public_contact_handle) BETWEEN 1 AND 160 AND btrim(public_contact_handle)<>''");
                    table.CheckConstraint("ck_admins_2", "auth_version >= 0");
                    table.CheckConstraint("ck_admins_3", "telegram_user_id > 0");
                    table.CheckConstraint("ck_admins_4", "octet_length(binding_challenge_hash)=32");
                    table.CheckConstraint("ck_admins_5", "binding_candidate_telegram_id > 0");
                    table.CheckConstraint("ck_admins_6", "(authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR\n           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)");
                    table.CheckConstraint("ck_admins_7", "num_nonnulls(telegram_user_id,telegram_verified_by_admin_id,telegram_verified_at,telegram_verification_audit_id) IN (0,4)");
                    table.CheckConstraint("ck_admins_8", "public_contact_handle IS NULL OR telegram_user_id IS NOT NULL");
                    table.CheckConstraint("ck_admins_9", "num_nonnulls(binding_challenge_hash,binding_candidate_telegram_id,binding_expires_at) IN (0,3)");
                    table.ForeignKey(
                        name: "admins_telegram_verified_by_admin_id_fk",
                        column: x => x.telegram_verified_by_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "brands",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    internal_name = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    public_name = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    market = table.Column<string>(type: "text", nullable: false),
                    default_language = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    logo_asset_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    bot_username = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_token_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    bot_token_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    allow_wallet = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    allow_crypto = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    allow_bank = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    allow_wallet_topup = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    zero_balance_topup = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    require_channel_join = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    support_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    fx_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    customer_support_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_number_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    bank_holder_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    bank_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    bank_card_fingerprint = table.Column<byte[]>(type: "bytea", nullable: true),
                    bank_changed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    current_fx = table.Column<decimal>(type: "numeric", nullable: true),
                    fx_source = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    fx_fetched_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    fx_error_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    pricing_rounding_step = table.Column<decimal>(type: "numeric", nullable: false, defaultValueSql: "5000"),
                    max_exchange_rate_age_seconds = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "3600"),
                    telegram_update_highwater = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "-1"),
                    telegram_update_window = table.Column<byte[]>(type: "bytea", nullable: false, defaultValueSql: "decode(repeat('00',128),'hex')"),
                    policy_version = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brands", x => x.id);
                    table.CheckConstraint("ck_brands_1", "market IN ('iran','international')");
                    table.CheckConstraint("ck_brands_10", "num_nonnulls(bank_number_ciphertext,bank_holder_ciphertext,bank_key_id,bank_card_fingerprint,bank_changed_at) IN (0,5)");
                    table.CheckConstraint("ck_brands_11", "bank_card_fingerprint IS NULL OR octet_length(bank_card_fingerprint)=32");
                    table.CheckConstraint("ck_brands_12", "NOT allow_bank OR (market='iran' AND bank_card_fingerprint IS NOT NULL)");
                    table.CheckConstraint("ck_brands_13", "num_nonnulls(current_fx,fx_source,fx_fetched_at) IN (0,3)");
                    table.CheckConstraint("ck_brands_14", "NOT (active AND support_enabled) OR customer_support_admin_id IS NOT NULL");
                    table.CheckConstraint("ck_brands_15", "isfinite(bank_changed_at)");
                    table.CheckConstraint("ck_brands_16", "isfinite(fx_fetched_at)");
                    table.CheckConstraint("ck_brands_17", "isfinite(created_at)");
                    table.CheckConstraint("ck_brands_18", "isfinite(updated_at)");
                    table.CheckConstraint("ck_brands_19", "length(code) BETWEEN 1 AND 160 AND btrim(code)<>''");
                    table.CheckConstraint("ck_brands_2", "bot_id > 0");
                    table.CheckConstraint("ck_brands_20", "length(internal_name) BETWEEN 1 AND 160 AND btrim(internal_name)<>''");
                    table.CheckConstraint("ck_brands_21", "length(public_name) BETWEEN 1 AND 160 AND btrim(public_name)<>''");
                    table.CheckConstraint("ck_brands_22", "length(default_language) BETWEEN 1 AND 160 AND btrim(default_language)<>''");
                    table.CheckConstraint("ck_brands_23", "length(logo_asset_ref) BETWEEN 1 AND 1024 AND btrim(logo_asset_ref)<>''");
                    table.CheckConstraint("ck_brands_24", "length(bot_username) BETWEEN 1 AND 160 AND btrim(bot_username)<>''");
                    table.CheckConstraint("ck_brands_25", "length(bot_token_key_id) BETWEEN 1 AND 160 AND btrim(bot_token_key_id)<>''");
                    table.CheckConstraint("ck_brands_26", "length(bank_key_id) BETWEEN 1 AND 160 AND btrim(bank_key_id)<>''");
                    table.CheckConstraint("ck_brands_27", "current_fx NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_brands_28", "length(fx_source) BETWEEN 1 AND 160 AND btrim(fx_source)<>''");
                    table.CheckConstraint("ck_brands_29", "length(fx_error_code) BETWEEN 1 AND 160 AND btrim(fx_error_code)<>''");
                    table.CheckConstraint("ck_brands_3", "octet_length(bot_token_ciphertext) > 0");
                    table.CheckConstraint("ck_brands_30", "pricing_rounding_step NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_brands_4", "current_fx > 0");
                    table.CheckConstraint("ck_brands_5", "pricing_rounding_step > 0");
                    table.CheckConstraint("ck_brands_6", "max_exchange_rate_age_seconds > 0");
                    table.CheckConstraint("ck_brands_7", "telegram_update_highwater >= -1");
                    table.CheckConstraint("ck_brands_8", "octet_length(telegram_update_window)=128");
                    table.CheckConstraint("ck_brands_9", "policy_version >= 0");
                    table.ForeignKey(
                        name: "brands_customer_support_admin_id_fk",
                        column: x => x.customer_support_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "brand_channels",
                schema: "shop",
                columns: table => new
                {
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    invite_link = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand_channels", x => new { x.brand_id, x.chat_id });
                    table.CheckConstraint("ck_brand_channels_1", "chat_id < 0");
                    table.CheckConstraint("ck_brand_channels_2", "isfinite(created_at)");
                    table.CheckConstraint("ck_brand_channels_3", "isfinite(updated_at)");
                    table.CheckConstraint("ck_brand_channels_4", "length(title) BETWEEN 1 AND 160 AND btrim(title)<>''");
                    table.CheckConstraint("ck_brand_channels_5", "length(invite_link) BETWEEN 1 AND 1024 AND btrim(invite_link)<>''");
                    table.ForeignKey(
                        name: "brand_channels_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    audience_kind = table.Column<string>(type: "text", nullable: false),
                    active_since = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    audience_language = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    message_text = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    media_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'draft'"),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.id);
                    table.UniqueConstraint("AK_campaigns_id_batch_id_brand_id", x => new { x.id, x.batch_id, x.brand_id });
                    table.CheckConstraint("ck_campaigns_1", "audience_kind IN ('all','active_since','language')");
                    table.CheckConstraint("ck_campaigns_10", "isfinite(started_at)");
                    table.CheckConstraint("ck_campaigns_11", "isfinite(completed_at)");
                    table.CheckConstraint("ck_campaigns_12", "length(language) BETWEEN 1 AND 160 AND btrim(language)<>''");
                    table.CheckConstraint("ck_campaigns_13", "length(audience_language) BETWEEN 1 AND 160 AND btrim(audience_language)<>''");
                    table.CheckConstraint("ck_campaigns_14", "length(message_text)<=16384");
                    table.CheckConstraint("ck_campaigns_15", "length(media_ref) BETWEEN 1 AND 1024 AND btrim(media_ref)<>''");
                    table.CheckConstraint("ck_campaigns_2", "status IN ('draft','approved','running','completed','cancelled')");
                    table.CheckConstraint("ck_campaigns_3", "num_nonnulls(message_text,media_ref)>0");
                    table.CheckConstraint("ck_campaigns_4", "(audience_kind='active_since')=(active_since IS NOT NULL)");
                    table.CheckConstraint("ck_campaigns_5", "(audience_kind='language')=(audience_language IS NOT NULL)");
                    table.CheckConstraint("ck_campaigns_6", "status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL");
                    table.CheckConstraint("ck_campaigns_7", "isfinite(active_since)");
                    table.CheckConstraint("ck_campaigns_8", "isfinite(approved_at)");
                    table.CheckConstraint("ck_campaigns_9", "isfinite(created_at)");
                    table.ForeignKey(
                        name: "campaigns_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "campaigns_created_by_admin_id_fk",
                        column: x => x.created_by_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "legacy_id_map",
                schema: "shop",
                columns: table => new
                {
                    source_brand = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    source_table = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    source_key_kind = table.Column<string>(type: "text", nullable: false),
                    legacy_id = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    import_batch = table.Column<Guid>(type: "uuid", nullable: false),
                    import_manifest_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: false),
                    migrated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_id_map", x => new { x.source_brand, x.source_table, x.source_key_kind, x.legacy_id });
                    table.CheckConstraint("ck_legacy_id_map_1", "source_key_kind IN ('primary','public_order_reference')");
                    table.CheckConstraint("ck_legacy_id_map_2", "target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')");
                    table.CheckConstraint("ck_legacy_id_map_3", "octet_length(source_fingerprint)=32");
                    table.CheckConstraint("ck_legacy_id_map_4", "isfinite(migrated_at)");
                    table.CheckConstraint("ck_legacy_id_map_5", "length(source_brand) BETWEEN 1 AND 160 AND btrim(source_brand)<>''");
                    table.CheckConstraint("ck_legacy_id_map_6", "length(source_table) BETWEEN 1 AND 160 AND btrim(source_table)<>''");
                    table.CheckConstraint("ck_legacy_id_map_7", "length(legacy_id) BETWEEN 1 AND 160 AND btrim(legacy_id)<>''");
                    table.CheckConstraint("ck_legacy_id_map_8", "length(import_manifest_ref) BETWEEN 1 AND 1024 AND btrim(import_manifest_ref)<>''");
                    table.ForeignKey(
                        name: "legacy_id_map_target_brand_id_fk",
                        column: x => x.target_brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", maxLength: 16384, nullable: false),
                    previous_revision = table.Column<long>(type: "bigint", nullable: true),
                    new_revision = table.Column<long>(type: "bigint", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    before_channel_enforcement = table.Column<bool>(type: "boolean", nullable: true),
                    after_channel_enforcement = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_1", "actor_kind IN ('admin','customer','system')");
                    table.CheckConstraint("ck_audit_events_10", "isfinite(created_at)");
                    table.CheckConstraint("ck_audit_events_11", "length(reason)<=16384");
                    table.CheckConstraint("ck_audit_events_2", "action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')");
                    table.CheckConstraint("ck_audit_events_3", "entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')");
                    table.CheckConstraint("ck_audit_events_4", "previous_revision >= 0");
                    table.CheckConstraint("ck_audit_events_5", "new_revision >= 0");
                    table.CheckConstraint("ck_audit_events_6", "octet_length(command_sha256)=32");
                    table.CheckConstraint("ck_audit_events_7", "(actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR\n           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR\n           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)");
                    table.CheckConstraint("ck_audit_events_8", "(entity_kind='platform')=(entity_id IS NULL)");
                    table.CheckConstraint("ck_audit_events_9", "(action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR\n           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)");
                    table.ForeignKey(
                        name: "audit_events_actor_admin_id_fk",
                        column: x => x.actor_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "audit_events_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bot_activity_events",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    action_code = table.Column<string>(type: "text", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    support_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity_selected = table.Column<int>(type: "integer", nullable: true),
                    flow_revision = table.Column<long>(type: "bigint", nullable: true),
                    telegram_update_id = table.Column<long>(type: "bigint", nullable: true),
                    outcome_code = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_activity_events", x => x.id);
                    table.CheckConstraint("ck_bot_activity_events_1", "event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow','payment')");
                    table.CheckConstraint("ck_bot_activity_events_10", "action_code NOT IN ('quantity_enter','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR quantity_selected IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_11", "action_code <> 'support_reply' OR support_ticket_id IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_12", "action_code NOT IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') OR order_id IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_13", "action_code NOT IN ('flow_expired','flow_abandoned','quantity_rejected') OR flow_revision IS NOT NULL");
                    table.CheckConstraint("ck_bot_activity_events_14", "action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)");
                    table.CheckConstraint("ck_bot_activity_events_15", "action_code <> 'checkout_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND order_id IS NULL AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL)");
                    table.CheckConstraint("ck_bot_activity_events_16", "action_code NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR (source_kind='system' AND\n        outcome_code=CASE WHEN action_code='checkout_funded' THEN 'accepted' ELSE 'cancelled' END)");
                    table.CheckConstraint("ck_bot_activity_events_17", "event_type<>'payment' OR (source_kind='system' AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL\n        AND outcome_code=CASE action_code WHEN 'payment_denied' THEN 'rejected' WHEN 'payment_review' THEN 'blocked' ELSE 'accepted' END)");
                    table.CheckConstraint("ck_bot_activity_events_18", "isfinite(occurred_at)");
                    table.CheckConstraint("ck_bot_activity_events_19", "length(idempotency_key) BETWEEN 1 AND 160 AND btrim(idempotency_key)<>''");
                    table.CheckConstraint("ck_bot_activity_events_2", "source_kind IN ('command','callback','flow','system')");
                    table.CheckConstraint("ck_bot_activity_events_3", "action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected','topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled','flow_abandoned')");
                    table.CheckConstraint("ck_bot_activity_events_4", "quantity_selected > 0");
                    table.CheckConstraint("ck_bot_activity_events_5", "flow_revision >= 0");
                    table.CheckConstraint("ck_bot_activity_events_6", "telegram_update_id >= 0");
                    table.CheckConstraint("ck_bot_activity_events_7", "outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')");
                    table.CheckConstraint("ck_bot_activity_events_8", "(event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR\n           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR\n           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected')) OR\n           (event_type='support' AND action_code IN ('support_open','support_reply')) OR\n           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR\n           (event_type='discount' AND action_code='discounts_open') OR\n           (event_type='policy' AND action_code='policy_check') OR\n           (event_type='flow' AND action_code IN ('flow_expired','flow_abandoned')) OR\n           (event_type='payment' AND action_code IN ('topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled'))");
                    table.CheckConstraint("ck_bot_activity_events_9", "action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR product_id IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "brand_products",
                schema: "shop",
                columns: table => new
                {
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visible = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    title_mode = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'inherit'"),
                    title_override = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    description_mode = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'inherit'"),
                    description_override = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    icon_mode = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'inherit'"),
                    icon_override = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    guidance_mode = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'inherit'"),
                    guidance_override = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    price_usdt = table.Column<decimal>(type: "numeric", nullable: false),
                    price_toman = table.Column<decimal>(type: "numeric", nullable: true),
                    auto_price_toman = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    allow_wallet = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    allow_crypto = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    allow_bank = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    min_quantity = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "1"),
                    max_quantity = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "100"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand_products", x => new { x.brand_id, x.product_id });
                    table.CheckConstraint("ck_brand_products_1", "title_mode IN ('inherit','override')");
                    table.CheckConstraint("ck_brand_products_10", "(title_mode='override')=(title_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_11", "(description_mode='override')=(description_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_12", "(icon_mode='override')=(icon_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_13", "(guidance_mode='override')=(guidance_override IS NOT NULL)");
                    table.CheckConstraint("ck_brand_products_14", "NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL");
                    table.CheckConstraint("ck_brand_products_15", "isfinite(updated_at)");
                    table.CheckConstraint("ck_brand_products_16", "length(title_override) BETWEEN 1 AND 160 AND btrim(title_override)<>''");
                    table.CheckConstraint("ck_brand_products_17", "length(description_override)<=16384");
                    table.CheckConstraint("ck_brand_products_18", "length(icon_override) BETWEEN 1 AND 1024 AND btrim(icon_override)<>''");
                    table.CheckConstraint("ck_brand_products_19", "length(guidance_override)<=16384");
                    table.CheckConstraint("ck_brand_products_2", "description_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_20", "price_usdt NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_brand_products_21", "price_toman NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_brand_products_3", "icon_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_4", "guidance_mode IN ('inherit','override','clear')");
                    table.CheckConstraint("ck_brand_products_5", "price_usdt > 0");
                    table.CheckConstraint("ck_brand_products_6", "price_toman > 0");
                    table.CheckConstraint("ck_brand_products_7", "min_quantity > 0");
                    table.CheckConstraint("ck_brand_products_8", "max_quantity >= min_quantity");
                    table.CheckConstraint("ck_brand_products_9", "revision >= 0");
                    table.ForeignKey(
                        name: "brand_products_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discount_revisions",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_no = table.Column<long>(type: "bigint", nullable: false),
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    percent_off = table.Column<decimal>(type: "numeric", nullable: false),
                    max_total_uses = table.Column<long>(type: "bigint", nullable: true),
                    max_uses_per_user = table.Column<long>(type: "bigint", nullable: true),
                    usage_adjustment_units = table.Column<long>(type: "bigint", nullable: false),
                    redeemed_count_at_revision = table.Column<long>(type: "bigint", nullable: false),
                    held_units_at_revision = table.Column<long>(type: "bigint", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ends_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    eligible_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_revisions", x => x.id);
                    table.CheckConstraint("ck_discount_revisions_1", "revision_no >= 0");
                    table.CheckConstraint("ck_discount_revisions_10", "percent_off NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_discount_revisions_2", "percent_off BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_discount_revisions_3", "max_total_uses >= 0");
                    table.CheckConstraint("ck_discount_revisions_4", "max_uses_per_user >= 0");
                    table.CheckConstraint("ck_discount_revisions_5", "redeemed_count_at_revision >= 0");
                    table.CheckConstraint("ck_discount_revisions_6", "held_units_at_revision >= 0");
                    table.CheckConstraint("ck_discount_revisions_7", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                    table.CheckConstraint("ck_discount_revisions_8", "isfinite(starts_at)");
                    table.CheckConstraint("ck_discount_revisions_9", "isfinite(ends_at)");
                    table.ForeignKey(
                        name: "discount_revisions_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discount_uses",
                schema: "shop",
                columns: table => new
                {
                    discount_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    units_covered = table.Column<int>(type: "integer", nullable: false),
                    saved_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    saved_currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    transitioned_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_uses", x => new { x.discount_id, x.order_id });
                    table.CheckConstraint("ck_discount_uses_1", "units_covered > 0");
                    table.CheckConstraint("ck_discount_uses_2", "saved_amount >= 0");
                    table.CheckConstraint("ck_discount_uses_3", "saved_currency IN ('USD','TOMAN')");
                    table.CheckConstraint("ck_discount_uses_4", "status IN ('held','redeemed','released')");
                    table.CheckConstraint("ck_discount_uses_5", "isfinite(created_at)");
                    table.CheckConstraint("ck_discount_uses_6", "isfinite(transitioned_at)");
                    table.CheckConstraint("ck_discount_uses_7", "saved_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.ForeignKey(
                        name: "discount_uses_discount_revision_id_fk",
                        column: x => x.discount_revision_id,
                        principalSchema: "shop",
                        principalTable: "discount_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discounts",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    code_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    code_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    display_prefix = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    issuance_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    percent_off = table.Column<decimal>(type: "numeric", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    eligible_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    max_total_uses = table.Column<long>(type: "bigint", nullable: true),
                    max_uses_per_user = table.Column<long>(type: "bigint", nullable: true),
                    redeemed_count = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    usage_adjustment_units = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    starts_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ends_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discounts", x => x.id);
                    table.CheckConstraint("ck_discounts_1", "octet_length(code_digest)=32");
                    table.CheckConstraint("ck_discounts_10", "(kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR\n           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)");
                    table.CheckConstraint("ck_discounts_11", "isfinite(starts_at)");
                    table.CheckConstraint("ck_discounts_12", "isfinite(ends_at)");
                    table.CheckConstraint("ck_discounts_13", "isfinite(created_at)");
                    table.CheckConstraint("ck_discounts_14", "length(code_key_id) BETWEEN 1 AND 160 AND btrim(code_key_id)<>''");
                    table.CheckConstraint("ck_discounts_15", "length(display_prefix) BETWEEN 1 AND 160 AND btrim(display_prefix)<>''");
                    table.CheckConstraint("ck_discounts_16", "percent_off NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_discounts_2", "octet_length(code_ciphertext)>0");
                    table.CheckConstraint("ck_discounts_3", "kind IN ('prepaid_entitlement','goodwill','promotion')");
                    table.CheckConstraint("ck_discounts_4", "percent_off BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_discounts_5", "max_total_uses >= 0");
                    table.CheckConstraint("ck_discounts_6", "max_uses_per_user >= 0");
                    table.CheckConstraint("ck_discounts_7", "redeemed_count >= 0");
                    table.CheckConstraint("ck_discounts_8", "revision >= 0");
                    table.CheckConstraint("ck_discounts_9", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                    table.ForeignKey(
                        name: "discounts_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "discounts_created_by_admin_id_fk",
                        column: x => x.created_by_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fulfillments",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_no = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unfilled'"),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remote_item_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    supplier_item_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    delivered_content = table.Column<byte[]>(type: "bytea", nullable: true),
                    delivered_content_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    content_kind = table.Column<string>(type: "text", nullable: true),
                    resolution_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_note = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    manifest_generation = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fulfillments", x => x.id);
                    table.CheckConstraint("ck_fulfillments_1", "unit_no > 0");
                    table.CheckConstraint("ck_fulfillments_10", "status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_fulfillments_11", "status <> 'retained_admin' OR resolution_admin_id IS NOT NULL");
                    table.CheckConstraint("ck_fulfillments_12", "isfinite(created_at)");
                    table.CheckConstraint("ck_fulfillments_13", "isfinite(completed_at)");
                    table.CheckConstraint("ck_fulfillments_14", "length(remote_item_ref) BETWEEN 1 AND 160 AND btrim(remote_item_ref)<>''");
                    table.CheckConstraint("ck_fulfillments_15", "length(delivered_content_key_id) BETWEEN 1 AND 160 AND btrim(delivered_content_key_id)<>''");
                    table.CheckConstraint("ck_fulfillments_16", "length(resolution_note)<=16384");
                    table.CheckConstraint("ck_fulfillments_2", "kind IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_fulfillments_3", "status IN ('unfilled','acquired','ready','released','retained_admin')");
                    table.CheckConstraint("ck_fulfillments_4", "octet_length(supplier_item_key)=32");
                    table.CheckConstraint("ck_fulfillments_5", "content_kind IN ('account','key','code','text','file')");
                    table.CheckConstraint("ck_fulfillments_6", "kind IS NOT NULL OR status='unfilled'");
                    table.CheckConstraint("ck_fulfillments_7", "(kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)");
                    table.CheckConstraint("ck_fulfillments_8", "num_nonnulls(delivered_content,delivered_content_key_id,content_kind) IN (0,3)");
                    table.CheckConstraint("ck_fulfillments_9", "status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL");
                    table.ForeignKey(
                        name: "fulfillments_resolution_admin_id_fk",
                        column: x => x.resolution_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_items",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    credential_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    source_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    origin_kind = table.Column<string>(type: "text", nullable: false),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remote_item_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    supplier_item_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'available'"),
                    assigned_fulfillment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allocated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_items", x => x.id);
                    table.CheckConstraint("ck_inventory_items_1", "octet_length(credential_ciphertext)>0");
                    table.CheckConstraint("ck_inventory_items_10", "isfinite(created_at)");
                    table.CheckConstraint("ck_inventory_items_11", "length(credential_key_id) BETWEEN 1 AND 160 AND btrim(credential_key_id)<>''");
                    table.CheckConstraint("ck_inventory_items_12", "length(remote_item_ref) BETWEEN 1 AND 160 AND btrim(remote_item_ref)<>''");
                    table.CheckConstraint("ck_inventory_items_2", "octet_length(source_fingerprint)=32");
                    table.CheckConstraint("ck_inventory_items_3", "origin_kind IN ('local_import','supplier_bonus')");
                    table.CheckConstraint("ck_inventory_items_4", "octet_length(supplier_item_key)=32");
                    table.CheckConstraint("ck_inventory_items_5", "state IN ('available','allocated','retired','admin_held')");
                    table.CheckConstraint("ck_inventory_items_6", "(state='allocated')=(assigned_fulfillment_id IS NOT NULL)");
                    table.CheckConstraint("ck_inventory_items_7", "(assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)");
                    table.CheckConstraint("ck_inventory_items_8", "(origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR\n           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')");
                    table.CheckConstraint("ck_inventory_items_9", "isfinite(allocated_at)");
                    table.ForeignKey(
                        name: "inventory_items_assigned_fulfillment_id_fk",
                        column: x => x.assigned_fulfillment_id,
                        principalSchema: "shop",
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_kind = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfillment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discount_id = table.Column<Guid>(type: "uuid", nullable: true),
                    support_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    telegram_delivery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_stock_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_supplier_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin_revision = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'"),
                    attempts = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    available_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    locked_by = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    lease_until = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    last_error_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_1", "job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')");
                    table.CheckConstraint("ck_jobs_10", "isfinite(created_at)");
                    table.CheckConstraint("ck_jobs_11", "isfinite(completed_at)");
                    table.CheckConstraint("ck_jobs_12", "length(locked_by) BETWEEN 1 AND 160 AND btrim(locked_by)<>''");
                    table.CheckConstraint("ck_jobs_13", "length(dedupe_key) BETWEEN 1 AND 160 AND btrim(dedupe_key)<>''");
                    table.CheckConstraint("ck_jobs_14", "length(last_error_code) BETWEEN 1 AND 160 AND btrim(last_error_code)<>''");
                    table.CheckConstraint("ck_jobs_2", "origin_revision >= 0");
                    table.CheckConstraint("ck_jobs_3", "status IN ('pending','leased','succeeded','retry','failed','cancelled')");
                    table.CheckConstraint("ck_jobs_4", "attempts >= 0");
                    table.CheckConstraint("ck_jobs_5", "(status='leased')=(locked_by IS NOT NULL)");
                    table.CheckConstraint("ck_jobs_6", "(locked_by IS NULL)=(lease_until IS NULL)");
                    table.CheckConstraint("ck_jobs_7", "status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL");
                    table.CheckConstraint("ck_jobs_8", "isfinite(available_at)");
                    table.CheckConstraint("ck_jobs_9", "isfinite(lease_until)");
                    table.ForeignKey(
                        name: "jobs_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_campaign_id_fk",
                        column: x => x.campaign_id,
                        principalSchema: "shop",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_discount_id_fk",
                        column: x => x.discount_id,
                        principalSchema: "shop",
                        principalTable: "discounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "jobs_fulfillment_id_fk",
                        column: x => x.fulfillment_id,
                        principalSchema: "shop",
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    order_kind = table.Column<string>(type: "text", nullable: false),
                    payment_status = table.Column<string>(type: "text", nullable: false),
                    fulfillment_status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unfulfilled'"),
                    currency = table.Column<string>(type: "text", nullable: false),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    unit_price_usdt = table.Column<decimal>(type: "numeric", nullable: false),
                    unit_price_toman = table.Column<decimal>(type: "numeric", nullable: true),
                    fx_rate_snapshot = table.Column<decimal>(type: "numeric", nullable: true),
                    fx_source_snapshot = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    fx_fetched_at_snapshot = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    auto_price_toman_snapshot = table.Column<bool>(type: "boolean", nullable: false),
                    pricing_rounding_step_snapshot = table.Column<decimal>(type: "numeric", nullable: false),
                    price_calculation_version = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    discount_pct_snapshot = table.Column<decimal>(type: "numeric", nullable: true),
                    discount_units_snapshot = table.Column<int>(type: "integer", nullable: true),
                    payment_due_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    payment_confirmed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    brand_public_name_snapshot = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    product_title_snapshot = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    product_description_snapshot = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    product_icon_snapshot = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    product_guidance_snapshot = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    payment_terms_snapshot = table.Column<string>(type: "text", maxLength: 16384, nullable: false),
                    fulfillment_kind_snapshot = table.Column<string>(type: "text", nullable: false),
                    local_first_snapshot = table.Column<bool>(type: "boolean", nullable: false),
                    is_preorder = table.Column<bool>(type: "boolean", nullable: false),
                    review_code = table.Column<string>(type: "text", nullable: true),
                    hold_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    hold_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_reason = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    resolution_kind = table.Column<string>(type: "text", nullable: true),
                    resolution_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolution_audit_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_wallet_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_generation = table.Column<Guid>(type: "uuid", nullable: true),
                    manifest_asset_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    manifest_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    manifest_prepared_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    release_authorized_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_1", "quantity > 0");
                    table.CheckConstraint("ck_orders_10", "fx_rate_snapshot > 0");
                    table.CheckConstraint("ck_orders_11", "pricing_rounding_step_snapshot > 0");
                    table.CheckConstraint("ck_orders_12", "discount_pct_snapshot BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_orders_13", "discount_units_snapshot > 0");
                    table.CheckConstraint("ck_orders_14", "fulfillment_kind_snapshot IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_orders_15", "resolution_kind IN ('replaced','unpaid_expiry','local_preorder_cancellation','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','manual_fulfilled')");
                    table.CheckConstraint("ck_orders_16", "octet_length(manifest_digest)=32");
                    table.CheckConstraint("ck_orders_17", "num_nonnulls(hold_at,hold_admin_id,hold_reason) IN (0,3)");
                    table.CheckConstraint("ck_orders_18", "num_nonnulls(manifest_asset_ref,manifest_digest,manifest_prepared_at,delivery_generation) IN (0,4)");
                    table.CheckConstraint("ck_orders_19", "release_authorized_at IS NULL OR (manifest_prepared_at IS NOT NULL AND release_authorized_at >= manifest_prepared_at)");
                    table.CheckConstraint("ck_orders_2", "revision >= 0");
                    table.CheckConstraint("ck_orders_20", "num_nonnulls(fx_rate_snapshot,fx_source_snapshot,fx_fetched_at_snapshot) IN (0,3)");
                    table.CheckConstraint("ck_orders_21", "num_nonnulls(discount_pct_snapshot,discount_units_snapshot) IN (0,2)");
                    table.CheckConstraint("ck_orders_22", "discount_units_snapshot IS NULL OR discount_units_snapshot=quantity");
                    table.CheckConstraint("ck_orders_23", "(currency='TOMAN')=(payment_method='bank')");
                    table.CheckConstraint("ck_orders_24", "currency <> 'TOMAN' OR unit_price_toman IS NOT NULL");
                    table.CheckConstraint("ck_orders_25", "payment_method <> 'no_payment_required' OR (payment_status='not_required' AND discount_pct_snapshot IS NOT NULL AND discount_pct_snapshot=100)");
                    table.CheckConstraint("ck_orders_26", "payment_status <> 'not_required' OR payment_method='no_payment_required'");
                    table.CheckConstraint("ck_orders_27", "payment_status NOT IN ('paid','not_required') OR payment_confirmed_at IS NOT NULL");
                    table.CheckConstraint("ck_orders_28", "payment_status NOT IN ('awaiting_payment','under_review','confirmation_pending') OR (payment_due_at IS NOT NULL AND payment_method IN ('crypto','bank'))");
                    table.CheckConstraint("ck_orders_29", "payment_due_at IS NULL OR payment_due_at=created_at + INTERVAL '30 minutes'");
                    table.CheckConstraint("ck_orders_3", "order_kind IN ('product_purchase','prepaid_units')");
                    table.CheckConstraint("ck_orders_30", "NOT is_preorder OR (order_kind='product_purchase' AND fulfillment_kind_snapshot='local')");
                    table.CheckConstraint("ck_orders_31", "(resolution_kind IS NULL AND num_nonnulls(resolution_admin_id,resolution_at,resolution_audit_event_id,resolution_wallet_entry_id)=0) OR\n           (resolution_kind IS NOT NULL AND resolution_at IS NOT NULL)");
                    table.CheckConstraint("ck_orders_32", "resolution_kind NOT IN ('local_preorder_cancellation','supplier_wallet_credit','manual_fulfilled') OR\n           (resolution_admin_id IS NOT NULL AND resolution_audit_event_id IS NOT NULL)");
                    table.CheckConstraint("ck_orders_33", "resolution_kind NOT IN ('immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit') OR resolution_wallet_entry_id IS NOT NULL");
                    table.CheckConstraint("ck_orders_34", "resolution_kind <> 'local_preorder_cancellation' OR (is_preorder AND resolution_wallet_entry_id IS NULL AND fulfillment_status='cancelled')");
                    table.CheckConstraint("ck_orders_35", "resolution_kind NOT IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') OR fulfillment_status='compensated'");
                    table.CheckConstraint("ck_orders_36", "isfinite(fx_fetched_at_snapshot)");
                    table.CheckConstraint("ck_orders_37", "isfinite(payment_due_at)");
                    table.CheckConstraint("ck_orders_38", "isfinite(payment_confirmed_at)");
                    table.CheckConstraint("ck_orders_39", "isfinite(hold_at)");
                    table.CheckConstraint("ck_orders_4", "payment_status IN ('awaiting_payment','under_review','confirmation_pending','paid','not_required','expired','cancelled')");
                    table.CheckConstraint("ck_orders_40", "isfinite(resolution_at)");
                    table.CheckConstraint("ck_orders_41", "isfinite(manifest_prepared_at)");
                    table.CheckConstraint("ck_orders_42", "isfinite(release_authorized_at)");
                    table.CheckConstraint("ck_orders_43", "isfinite(created_at)");
                    table.CheckConstraint("ck_orders_44", "isfinite(updated_at)");
                    table.CheckConstraint("ck_orders_45", "unit_price_usdt NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_orders_46", "unit_price_toman NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_orders_47", "fx_rate_snapshot NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_orders_48", "length(fx_source_snapshot) BETWEEN 1 AND 160 AND btrim(fx_source_snapshot)<>''");
                    table.CheckConstraint("ck_orders_49", "pricing_rounding_step_snapshot NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_orders_5", "fulfillment_status IN ('unfulfilled','waiting_stock','acquiring','requires_admin','ready','delivering','completed','cancelled','compensated')");
                    table.CheckConstraint("ck_orders_50", "length(price_calculation_version) BETWEEN 1 AND 160 AND btrim(price_calculation_version)<>''");
                    table.CheckConstraint("ck_orders_51", "discount_pct_snapshot NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_orders_52", "length(brand_public_name_snapshot) BETWEEN 1 AND 160 AND btrim(brand_public_name_snapshot)<>''");
                    table.CheckConstraint("ck_orders_53", "length(product_title_snapshot) BETWEEN 1 AND 160 AND btrim(product_title_snapshot)<>''");
                    table.CheckConstraint("ck_orders_54", "length(product_description_snapshot)<=16384");
                    table.CheckConstraint("ck_orders_55", "length(product_icon_snapshot) BETWEEN 1 AND 1024 AND btrim(product_icon_snapshot)<>''");
                    table.CheckConstraint("ck_orders_56", "length(product_guidance_snapshot)<=16384");
                    table.CheckConstraint("ck_orders_57", "length(payment_terms_snapshot)<=16384");
                    table.CheckConstraint("ck_orders_58", "review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery')");
                    table.CheckConstraint("ck_orders_59", "length(hold_reason)<=16384");
                    table.CheckConstraint("ck_orders_6", "currency IN ('USDT','TOMAN')");
                    table.CheckConstraint("ck_orders_60", "length(manifest_asset_ref) BETWEEN 1 AND 1024 AND btrim(manifest_asset_ref)<>''");
                    table.CheckConstraint("ck_orders_7", "payment_method IN ('wallet','crypto','bank','no_payment_required')");
                    table.CheckConstraint("ck_orders_8", "unit_price_usdt > 0");
                    table.CheckConstraint("ck_orders_9", "unit_price_toman > 0");
                    table.ForeignKey(
                        name: "orders_hold_admin_id_fk",
                        column: x => x.hold_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "orders_resolution_admin_id_fk",
                        column: x => x.resolution_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "orders_resolution_audit_event_id_fk",
                        column: x => x.resolution_audit_event_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    topup_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    quote_currency = table.Column<string>(type: "text", nullable: false),
                    amount_due = table.Column<decimal>(type: "numeric", nullable: false),
                    amount_received = table.Column<decimal>(type: "numeric", nullable: true),
                    provider_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    provider_payment_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    hosted_invoice_started_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    hosted_invoice_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    hosted_invoice_url = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    idempotency_ref = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    network_asset = table.Column<string>(type: "text", nullable: true),
                    expected_network_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    ipn_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    ipn_signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    ipn_payload_sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    ipn_verified_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    network_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    provider_confirmations = table.Column<long>(type: "bigint", nullable: true),
                    first_payment_observed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'created'"),
                    provider_status_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    bank_destination_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    bank_destination_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    receipt_asset_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    review_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    review_reason = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    compensating_wallet_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evidence_asset_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    pending_evidence_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    pending_evidence_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    evidence_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    legacy_kind = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.id);
                    table.CheckConstraint("ck_payments_1", "payment_method IN ('crypto','bank')");
                    table.CheckConstraint("ck_payments_10", "network_amount >= 0");
                    table.CheckConstraint("ck_payments_11", "provider_confirmations >= 0");
                    table.CheckConstraint("ck_payments_12", "status IN ('created','awaiting','observed','confirming','settled','rejected','expired','review')");
                    table.CheckConstraint("ck_payments_13", "octet_length(pending_evidence_ciphertext) BETWEEN 1 AND 66000");
                    table.CheckConstraint("ck_payments_14", "octet_length(evidence_digest)=32");
                    table.CheckConstraint("ck_payments_15", "num_nonnulls(order_id,topup_user_id)=1");
                    table.CheckConstraint("ck_payments_16", "num_nonnulls(bank_destination_ciphertext,bank_destination_key_id) IN (0,2)");
                    table.CheckConstraint("ck_payments_17", "num_nonnulls(pending_evidence_ciphertext,pending_evidence_key_id) IN (0,2)");
                    table.CheckConstraint("ck_payments_18", "(evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL) = (evidence_digest IS NOT NULL)");
                    table.CheckConstraint("ck_payments_19", "pending_evidence_ciphertext IS NULL OR (evidence_asset_ref IS NULL AND order_id IS NOT NULL AND ipn_key_id IS NOT NULL AND first_payment_observed_at IS NOT NULL AND status IN ('confirming','review'))");
                    table.CheckConstraint("ck_payments_2", "quote_currency IN ('USD','TOMAN')");
                    table.CheckConstraint("ck_payments_20", "num_nonnulls(review_admin_id,reviewed_at,review_reason) IN (0,3)");
                    table.CheckConstraint("ck_payments_21", "provider_payment_ref IS NULL OR provider_code IS NOT NULL");
                    table.CheckConstraint("ck_payments_22", "isfinite(hosted_invoice_started_at)");
                    table.CheckConstraint("ck_payments_23", "num_nonnulls(hosted_invoice_ref,hosted_invoice_url) IN (0,2)");
                    table.CheckConstraint("ck_payments_24", "hosted_invoice_started_at IS NULL OR (payment_method='crypto' AND provider_code IS NOT DISTINCT FROM 'nowpayments')");
                    table.CheckConstraint("ck_payments_25", "hosted_invoice_ref IS NULL OR hosted_invoice_started_at IS NOT NULL");
                    table.CheckConstraint("ck_payments_26", "(payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination_ciphertext IS NULL) OR\n           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND (bank_destination_ciphertext IS NOT NULL OR coalesce(legacy_kind='manual_deposit',false)) AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)");
                    table.CheckConstraint("ck_payments_27", "topup_user_id IS NULL OR payment_method='crypto'");
                    table.CheckConstraint("ck_payments_28", "status <> 'settled' OR confirmed_at IS NOT NULL");
                    table.CheckConstraint("ck_payments_29", "num_nonnulls(ipn_key_id,ipn_signature,ipn_payload_sha256,ipn_verified_at) IN (0,4)");
                    table.CheckConstraint("ck_payments_3", "amount_due > 0");
                    table.CheckConstraint("ck_payments_30", "ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND (evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL))");
                    table.CheckConstraint("ck_payments_31", "expected_network_amount IS NULL OR payment_method='crypto'");
                    table.CheckConstraint("ck_payments_32", "isfinite(ipn_verified_at)");
                    table.CheckConstraint("ck_payments_33", "isfinite(first_payment_observed_at)");
                    table.CheckConstraint("ck_payments_34", "isfinite(confirmed_at)");
                    table.CheckConstraint("ck_payments_35", "isfinite(reviewed_at)");
                    table.CheckConstraint("ck_payments_36", "isfinite(created_at)");
                    table.CheckConstraint("ck_payments_37", "amount_due NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_payments_38", "amount_received NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_payments_39", "length(provider_code) BETWEEN 1 AND 160 AND btrim(provider_code)<>''");
                    table.CheckConstraint("ck_payments_4", "amount_received >= 0");
                    table.CheckConstraint("ck_payments_40", "length(provider_payment_ref) BETWEEN 1 AND 160 AND btrim(provider_payment_ref)<>''");
                    table.CheckConstraint("ck_payments_41", "length(hosted_invoice_ref) BETWEEN 1 AND 160 AND btrim(hosted_invoice_ref)<>''");
                    table.CheckConstraint("ck_payments_42", "length(hosted_invoice_url) BETWEEN 1 AND 1024 AND btrim(hosted_invoice_url)<>''");
                    table.CheckConstraint("ck_payments_43", "length(idempotency_ref) BETWEEN 1 AND 160 AND btrim(idempotency_ref)<>''");
                    table.CheckConstraint("ck_payments_44", "expected_network_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_payments_45", "length(ipn_key_id) BETWEEN 1 AND 160 AND btrim(ipn_key_id)<>''");
                    table.CheckConstraint("ck_payments_46", "network_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_payments_47", "length(provider_status_code) BETWEEN 1 AND 160 AND btrim(provider_status_code)<>''");
                    table.CheckConstraint("ck_payments_48", "length(bank_destination_key_id) BETWEEN 1 AND 160 AND btrim(bank_destination_key_id)<>''");
                    table.CheckConstraint("ck_payments_49", "length(receipt_asset_ref) BETWEEN 1 AND 1024 AND btrim(receipt_asset_ref)<>''");
                    table.CheckConstraint("ck_payments_5", "hosted_invoice_ref ~ '^[0-9]{1,64}$'");
                    table.CheckConstraint("ck_payments_50", "length(review_reason)<=16384");
                    table.CheckConstraint("ck_payments_51", "length(evidence_asset_ref) BETWEEN 1 AND 1024 AND btrim(evidence_asset_ref)<>''");
                    table.CheckConstraint("ck_payments_52", "length(pending_evidence_key_id) BETWEEN 1 AND 160 AND btrim(pending_evidence_key_id)<>''");
                    table.CheckConstraint("ck_payments_53", "length(legacy_kind) BETWEEN 1 AND 160 AND btrim(legacy_kind)<>''");
                    table.CheckConstraint("ck_payments_6", "network_asset IN ('USDTTRC20','USDTBSC')");
                    table.CheckConstraint("ck_payments_7", "expected_network_amount > 0");
                    table.CheckConstraint("ck_payments_8", "octet_length(ipn_signature)=64");
                    table.CheckConstraint("ck_payments_9", "octet_length(ipn_payload_sha256)=32");
                    table.ForeignKey(
                        name: "payments_order_id_fk",
                        column: x => x.order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "payments_review_admin_id_fk",
                        column: x => x.review_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_stock_subscriptions",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    triggered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stock_subscriptions", x => x.id);
                    table.CheckConstraint("ck_product_stock_subscriptions_1", "triggered_at IS NULL OR triggered_at >= created_at");
                    table.CheckConstraint("ck_product_stock_subscriptions_2", "cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_3", "closed_at IS NULL OR closed_at >= created_at");
                    table.CheckConstraint("ck_product_stock_subscriptions_4", "isfinite(created_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_5", "isfinite(triggered_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_6", "isfinite(cancelled_at)");
                    table.CheckConstraint("ck_product_stock_subscriptions_7", "isfinite(closed_at)");
                });

            migrationBuilder.CreateTable(
                name: "product_supplier_sources",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    external_product_id = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    external_variant_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "1"),
                    observation_revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    observed_source_revision = table.Column<long>(type: "bigint", nullable: true),
                    observed_account_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    observed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    availability_code = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unknown'"),
                    available_quantity = table.Column<int>(type: "integer", nullable: true),
                    observed_unit_cost = table.Column<decimal>(type: "numeric", nullable: true),
                    observed_currency = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    eligibility_code = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unknown'"),
                    quote_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    observation_error_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_supplier_sources", x => x.id);
                    table.UniqueConstraint("AK_product_supplier_sources_product_id_id", x => new { x.product_id, x.id });
                    table.CheckConstraint("ck_product_supplier_sources_1", "provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')");
                    table.CheckConstraint("ck_product_supplier_sources_10", "availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)");
                    table.CheckConstraint("ck_product_supplier_sources_11", "availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')");
                    table.CheckConstraint("ck_product_supplier_sources_12", "availability_code <> 'out_of_stock' OR available_quantity=0");
                    table.CheckConstraint("ck_product_supplier_sources_13", "isfinite(observed_at)");
                    table.CheckConstraint("ck_product_supplier_sources_14", "isfinite(created_at)");
                    table.CheckConstraint("ck_product_supplier_sources_15", "isfinite(updated_at)");
                    table.CheckConstraint("ck_product_supplier_sources_16", "length(external_product_id) BETWEEN 1 AND 160 AND btrim(external_product_id)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_17", "length(external_variant_id) BETWEEN 1 AND 160 AND btrim(external_variant_id)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_18", "length(observed_account_ref) BETWEEN 1 AND 160 AND btrim(observed_account_ref)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_19", "observed_unit_cost NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_product_supplier_sources_2", "revision > 0");
                    table.CheckConstraint("ck_product_supplier_sources_20", "length(observed_currency) BETWEEN 1 AND 160 AND btrim(observed_currency)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_21", "length(quote_ref) BETWEEN 1 AND 160 AND btrim(quote_ref)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_22", "length(observation_error_code) BETWEEN 1 AND 160 AND btrim(observation_error_code)<>''");
                    table.CheckConstraint("ck_product_supplier_sources_3", "observation_revision >= 0");
                    table.CheckConstraint("ck_product_supplier_sources_4", "observed_source_revision > 0");
                    table.CheckConstraint("ck_product_supplier_sources_5", "availability_code IN ('unknown','available','out_of_stock','ineligible','error')");
                    table.CheckConstraint("ck_product_supplier_sources_6", "available_quantity >= 0");
                    table.CheckConstraint("ck_product_supplier_sources_7", "observed_unit_cost > 0");
                    table.CheckConstraint("ck_product_supplier_sources_8", "eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')");
                    table.CheckConstraint("ck_product_supplier_sources_9", "num_nonnulls(observed_unit_cost,observed_currency) IN (0,2)");
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    fulfillment_kind = table.Column<string>(type: "text", nullable: false),
                    local_first = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    availability_policy = table.Column<string>(type: "text", nullable: false),
                    active_supplier_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    title = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    icon_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    guidance = table.Column<string>(type: "text", maxLength: 16384, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                    table.CheckConstraint("ck_products_1", "fulfillment_kind IN ('local','supplier','manual')");
                    table.CheckConstraint("ck_products_10", "length(icon_ref) BETWEEN 1 AND 1024 AND btrim(icon_ref)<>''");
                    table.CheckConstraint("ck_products_11", "length(guidance)<=16384");
                    table.CheckConstraint("ck_products_2", "availability_policy IN ('immediate','local_preorder')");
                    table.CheckConstraint("ck_products_3", "revision >= 0");
                    table.CheckConstraint("ck_products_4", "availability_policy <> 'local_preorder' OR fulfillment_kind='local'");
                    table.CheckConstraint("ck_products_5", "isfinite(created_at)");
                    table.CheckConstraint("ck_products_6", "isfinite(updated_at)");
                    table.CheckConstraint("ck_products_7", "length(sku) BETWEEN 1 AND 160 AND btrim(sku)<>''");
                    table.CheckConstraint("ck_products_8", "length(title) BETWEEN 1 AND 160 AND btrim(title)<>''");
                    table.CheckConstraint("ck_products_9", "length(description)<=16384");
                    table.ForeignKey(
                        name: "products_id_active_supplier_source_id_fk",
                        columns: x => new { x.id, x.active_supplier_source_id },
                        principalSchema: "shop",
                        principalTable: "product_supplier_sources",
                        principalColumns: new[] { "product_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_requests",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_quantity = table.Column<int>(type: "integer", nullable: false),
                    product_supplier_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_revision = table.Column<long>(type: "bigint", nullable: true),
                    provider_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    account_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    external_product_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    external_variant_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    local_intent_key = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    remote_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    request_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'blocked'"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    review_code = table.Column<string>(type: "text", nullable: true),
                    next_check_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    cost_baseline_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    cost_currency = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    cost_quote_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    cost_observed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_cost_ceiling = table.Column<decimal>(type: "numeric", nullable: true),
                    approval_audit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_requests", x => x.id);
                    table.CheckConstraint("ck_supplier_requests_1", "requested_quantity > 0");
                    table.CheckConstraint("ck_supplier_requests_10", "status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))");
                    table.CheckConstraint("ck_supplier_requests_11", "status <> 'blocked' OR review_code IS NOT NULL");
                    table.CheckConstraint("ck_supplier_requests_12", "isfinite(next_check_at)");
                    table.CheckConstraint("ck_supplier_requests_13", "isfinite(cost_observed_at)");
                    table.CheckConstraint("ck_supplier_requests_14", "isfinite(created_at)");
                    table.CheckConstraint("ck_supplier_requests_15", "isfinite(updated_at)");
                    table.CheckConstraint("ck_supplier_requests_16", "length(provider_code) BETWEEN 1 AND 160 AND btrim(provider_code)<>''");
                    table.CheckConstraint("ck_supplier_requests_17", "length(account_ref) BETWEEN 1 AND 160 AND btrim(account_ref)<>''");
                    table.CheckConstraint("ck_supplier_requests_18", "length(external_product_id) BETWEEN 1 AND 160 AND btrim(external_product_id)<>''");
                    table.CheckConstraint("ck_supplier_requests_19", "length(external_variant_id) BETWEEN 1 AND 160 AND btrim(external_variant_id)<>''");
                    table.CheckConstraint("ck_supplier_requests_2", "source_revision > 0");
                    table.CheckConstraint("ck_supplier_requests_20", "length(local_intent_key) BETWEEN 1 AND 160 AND btrim(local_intent_key)<>''");
                    table.CheckConstraint("ck_supplier_requests_21", "length(remote_ref) BETWEEN 1 AND 160 AND btrim(remote_ref)<>''");
                    table.CheckConstraint("ck_supplier_requests_22", "review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery')");
                    table.CheckConstraint("ck_supplier_requests_23", "length(evidence_ref) BETWEEN 1 AND 1024 AND btrim(evidence_ref)<>''");
                    table.CheckConstraint("ck_supplier_requests_24", "cost_baseline_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_supplier_requests_25", "length(cost_currency) BETWEEN 1 AND 160 AND btrim(cost_currency)<>''");
                    table.CheckConstraint("ck_supplier_requests_26", "length(cost_quote_ref) BETWEEN 1 AND 160 AND btrim(cost_quote_ref)<>''");
                    table.CheckConstraint("ck_supplier_requests_27", "approved_cost_ceiling NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_supplier_requests_3", "octet_length(request_sha256)=32");
                    table.CheckConstraint("ck_supplier_requests_4", "status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')");
                    table.CheckConstraint("ck_supplier_requests_5", "revision >= 0");
                    table.CheckConstraint("ck_supplier_requests_6", "cost_baseline_amount > 0");
                    table.CheckConstraint("ck_supplier_requests_7", "approved_cost_ceiling > 0");
                    table.CheckConstraint("ck_supplier_requests_8", "num_nonnulls(cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at) IN (0,4)");
                    table.CheckConstraint("ck_supplier_requests_9", "num_nonnulls(approved_cost_ceiling,approval_audit_id) IN (0,2)");
                    table.ForeignKey(
                        name: "supplier_requests_approval_audit_id_fk",
                        column: x => x.approval_audit_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_requests_order_id_fk",
                        column: x => x.order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_requests_product_supplier_source_id_fk",
                        column: x => x.product_supplier_source_id,
                        principalSchema: "shop",
                        principalTable: "product_supplier_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    first_name = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    last_name = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    language = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    blocked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    banned_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    marketing_opt_out_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    first_seen_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_seen_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    flow_state = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'idle'"),
                    flow_revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    flow_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    flow_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    flow_anchor_message_id = table.Column<long>(type: "bigint", nullable: true),
                    flow_expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_1", "telegram_id > 0");
                    table.CheckConstraint("ck_users_10", "isfinite(flow_expires_at)");
                    table.CheckConstraint("ck_users_11", "length(username) BETWEEN 1 AND 160 AND btrim(username)<>''");
                    table.CheckConstraint("ck_users_12", "length(first_name) BETWEEN 1 AND 160 AND btrim(first_name)<>''");
                    table.CheckConstraint("ck_users_13", "length(last_name) BETWEEN 1 AND 160 AND btrim(last_name)<>''");
                    table.CheckConstraint("ck_users_14", "length(language) BETWEEN 1 AND 160 AND btrim(language)<>''");
                    table.CheckConstraint("ck_users_2", "flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')");
                    table.CheckConstraint("ck_users_3", "flow_revision >= 0");
                    table.CheckConstraint("ck_users_4", "flow_anchor_message_id > 0");
                    table.CheckConstraint("ck_users_5", "isfinite(blocked_at)");
                    table.CheckConstraint("ck_users_6", "isfinite(banned_at)");
                    table.CheckConstraint("ck_users_7", "isfinite(marketing_opt_out_at)");
                    table.CheckConstraint("ck_users_8", "isfinite(first_seen_at)");
                    table.CheckConstraint("ck_users_9", "isfinite(last_seen_at)");
                    table.ForeignKey(
                        name: "users_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "users_flow_order_id_fk",
                        column: x => x.flow_order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "users_flow_product_id_fk",
                        column: x => x.flow_product_id,
                        principalSchema: "shop",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_request_attempts",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'in_flight'"),
                    parent_submit_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    admin_authorization_audit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purchase_idempotency_key = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    max_charge_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    remote_ref = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    evidence_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    finished_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_request_attempts", x => x.id);
                    table.CheckConstraint("ck_supplier_request_attempts_1", "attempt_no > 0");
                    table.CheckConstraint("ck_supplier_request_attempts_10", "isfinite(finished_at)");
                    table.CheckConstraint("ck_supplier_request_attempts_11", "length(purchase_idempotency_key) BETWEEN 1 AND 160 AND btrim(purchase_idempotency_key)<>''");
                    table.CheckConstraint("ck_supplier_request_attempts_12", "max_charge_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_supplier_request_attempts_13", "length(error_code) BETWEEN 1 AND 160 AND btrim(error_code)<>''");
                    table.CheckConstraint("ck_supplier_request_attempts_14", "length(remote_ref) BETWEEN 1 AND 160 AND btrim(remote_ref)<>''");
                    table.CheckConstraint("ck_supplier_request_attempts_15", "length(evidence_ref) BETWEEN 1 AND 1024 AND btrim(evidence_ref)<>''");
                    table.CheckConstraint("ck_supplier_request_attempts_2", "operation IN ('submit','status_lookup','export')");
                    table.CheckConstraint("ck_supplier_request_attempts_3", "outcome IN ('in_flight','succeeded','failed','timeout','unknown')");
                    table.CheckConstraint("ck_supplier_request_attempts_4", "max_charge_amount > 0");
                    table.CheckConstraint("ck_supplier_request_attempts_5", "http_status BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_supplier_request_attempts_6", "(outcome='in_flight')=(finished_at IS NULL)");
                    table.CheckConstraint("ck_supplier_request_attempts_7", "finished_at IS NULL OR finished_at >= started_at");
                    table.CheckConstraint("ck_supplier_request_attempts_8", "(operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR\n           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)");
                    table.CheckConstraint("ck_supplier_request_attempts_9", "isfinite(started_at)");
                    table.ForeignKey(
                        name: "supplier_request_attempts_admin_authorization_audit_id_fk",
                        column: x => x.admin_authorization_audit_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_request_attempts_parent_submit_attempt_id_fk",
                        column: x => x.parent_submit_attempt_id,
                        principalSchema: "shop",
                        principalTable: "supplier_request_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "supplier_request_attempts_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalSchema: "shop",
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_tickets",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'open'"),
                    source = table.Column<string>(type: "text", nullable: false),
                    assigned_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_tickets", x => x.id);
                    table.CheckConstraint("ck_support_tickets_1", "category IN ('general','order','payment','preorder_cancellation')");
                    table.CheckConstraint("ck_support_tickets_2", "status IN ('open','assigned','waiting_customer','closed')");
                    table.CheckConstraint("ck_support_tickets_3", "source IN ('customer','admin','system')");
                    table.CheckConstraint("ck_support_tickets_4", "(status='closed')=(closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_support_tickets_5", "category <> 'preorder_cancellation' OR order_id IS NOT NULL");
                    table.CheckConstraint("ck_support_tickets_6", "isfinite(created_at)");
                    table.CheckConstraint("ck_support_tickets_7", "isfinite(updated_at)");
                    table.CheckConstraint("ck_support_tickets_8", "isfinite(closed_at)");
                    table.ForeignKey(
                        name: "support_tickets_assigned_admin_id_fk",
                        column: x => x.assigned_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_order_id_fk",
                        column: x => x.order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_user_id_fk",
                        column: x => x.user_id,
                        principalSchema: "shop",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "shop",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<decimal>(type: "numeric", nullable: false, defaultValueSql: "0"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.user_id);
                    table.CheckConstraint("ck_wallets_1", "balance >= 0");
                    table.CheckConstraint("ck_wallets_2", "version >= 0");
                    table.CheckConstraint("ck_wallets_3", "isfinite(updated_at)");
                    table.CheckConstraint("ck_wallets_4", "balance NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.ForeignKey(
                        name: "wallets_user_id_fk",
                        column: x => x.user_id,
                        principalSchema: "shop",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_messages",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_kind = table.Column<string>(type: "text", nullable: false),
                    sender_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    body_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    body_key_id = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    asset_ref = table.Column<string>(type: "text", maxLength: 1024, nullable: true),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_messages", x => x.id);
                    table.CheckConstraint("ck_support_messages_1", "sender_kind IN ('customer','admin','system')");
                    table.CheckConstraint("ck_support_messages_2", "sender_telegram_id > 0");
                    table.CheckConstraint("ck_support_messages_3", "telegram_message_id > 0");
                    table.CheckConstraint("ck_support_messages_4", "(sender_kind='admin')=(sender_admin_id IS NOT NULL)");
                    table.CheckConstraint("ck_support_messages_5", "num_nonnulls(body_ciphertext,body_key_id) IN (0,2)");
                    table.CheckConstraint("ck_support_messages_6", "num_nonnulls(body_ciphertext,asset_ref)>0");
                    table.CheckConstraint("ck_support_messages_7", "isfinite(created_at)");
                    table.CheckConstraint("ck_support_messages_8", "length(body_key_id) BETWEEN 1 AND 160 AND btrim(body_key_id)<>''");
                    table.CheckConstraint("ck_support_messages_9", "length(asset_ref) BETWEEN 1 AND 1024 AND btrim(asset_ref)<>''");
                    table.ForeignKey(
                        name: "support_messages_sender_admin_id_fk",
                        column: x => x.sender_admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_messages_ticket_id_fk",
                        column: x => x.ticket_id,
                        principalSchema: "shop",
                        principalTable: "support_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signed_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric", nullable: true),
                    posting_version = table.Column<long>(type: "bigint", nullable: true),
                    movement_kind = table.Column<string>(type: "text", nullable: false),
                    is_posting = table.Column<bool>(type: "boolean", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_import_batch = table.Column<Guid>(type: "uuid", nullable: true),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", maxLength: 16384, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_entries", x => x.id);
                    table.CheckConstraint("ck_wallet_entries_1", "signed_amount <> 0");
                    table.CheckConstraint("ck_wallet_entries_10", "signed_amount NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_wallet_entries_11", "balance_after NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric)");
                    table.CheckConstraint("ck_wallet_entries_12", "length(idempotency_key) BETWEEN 1 AND 160 AND btrim(idempotency_key)<>''");
                    table.CheckConstraint("ck_wallet_entries_13", "length(reason)<=16384");
                    table.CheckConstraint("ck_wallet_entries_2", "balance_after >= 0");
                    table.CheckConstraint("ck_wallet_entries_3", "posting_version > 0");
                    table.CheckConstraint("ck_wallet_entries_4", "movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')");
                    table.CheckConstraint("ck_wallet_entries_5", "(is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR\n           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)");
                    table.CheckConstraint("ck_wallet_entries_6", "movement_kind <> 'purchase_debit' OR signed_amount < 0");
                    table.CheckConstraint("ck_wallet_entries_7", "movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR signed_amount > 0");
                    table.CheckConstraint("ck_wallet_entries_8", "(movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR\n           (movement_kind='legacy_history' AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR\n           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND num_nonnulls(order_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND num_nonnulls(payment_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND num_nonnulls(supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR\n           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(payment_id,legacy_import_batch)=0) OR\n           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,legacy_import_batch)=0)");
                    table.CheckConstraint("ck_wallet_entries_9", "isfinite(created_at)");
                    table.ForeignKey(
                        name: "wallet_entries_admin_id_fk",
                        column: x => x.admin_id,
                        principalSchema: "shop",
                        principalTable: "admins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_order_id_fk",
                        column: x => x.order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_payment_id_fk",
                        column: x => x.payment_id,
                        principalSchema: "shop",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalSchema: "shop",
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "wallet_entries_user_id_fk",
                        column: x => x.user_id,
                        principalSchema: "shop",
                        principalTable: "wallets",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telegram_deliveries",
                schema: "shop",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_kind = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    staff_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_message_id = table.Column<long>(type: "bigint", nullable: true),
                    part_no = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "1"),
                    delivery_generation = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_language = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    eligibility_checked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'"),
                    attempts = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    next_attempt_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_attempt_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: true),
                    last_error_code = table.Column<string>(type: "text", maxLength: 160, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discount_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfillment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    support_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_stock_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_deliveries", x => x.id);
                    table.CheckConstraint("ck_telegram_deliveries_1", "recipient_kind IN ('customer_dm','staff_chat')");
                    table.CheckConstraint("ck_telegram_deliveries_10", "(recipient_kind='customer_dm' AND user_id IS NOT NULL AND staff_chat_id IS NULL AND purpose NOT IN ('staff_supplier_case','staff_supplier_bonus')) OR\n           (recipient_kind='staff_chat' AND user_id IS NULL AND staff_chat_id IS NOT NULL AND purpose IN ('staff_supplier_case','staff_supplier_bonus'))");
                    table.CheckConstraint("ck_telegram_deliveries_11", "action <> 'edit' OR target_message_id IS NOT NULL");
                    table.CheckConstraint("ck_telegram_deliveries_12", "status <> 'succeeded' OR sent_at IS NOT NULL");
                    table.CheckConstraint("ck_telegram_deliveries_13", "num_nonnulls(campaign_id,campaign_batch_id) IN (0,2)");
                    table.CheckConstraint("ck_telegram_deliveries_14", "isfinite(eligibility_checked_at)");
                    table.CheckConstraint("ck_telegram_deliveries_15", "isfinite(next_attempt_at)");
                    table.CheckConstraint("ck_telegram_deliveries_16", "isfinite(last_attempt_at)");
                    table.CheckConstraint("ck_telegram_deliveries_17", "isfinite(sent_at)");
                    table.CheckConstraint("ck_telegram_deliveries_18", "isfinite(created_at)");
                    table.CheckConstraint("ck_telegram_deliveries_19", "length(recipient_language) BETWEEN 1 AND 160 AND btrim(recipient_language)<>''");
                    table.CheckConstraint("ck_telegram_deliveries_2", "staff_chat_id < 0");
                    table.CheckConstraint("ck_telegram_deliveries_20", "length(dedupe_key) BETWEEN 1 AND 160 AND btrim(dedupe_key)<>''");
                    table.CheckConstraint("ck_telegram_deliveries_21", "length(last_error_code) BETWEEN 1 AND 160 AND btrim(last_error_code)<>''");
                    table.CheckConstraint("ck_telegram_deliveries_3", "purpose IN ('order_credentials','order_status','campaign','discount_notice','support_notice','restock_notice','staff_supplier_case','staff_supplier_bonus')");
                    table.CheckConstraint("ck_telegram_deliveries_4", "action IN ('send','edit')");
                    table.CheckConstraint("ck_telegram_deliveries_5", "target_message_id > 0");
                    table.CheckConstraint("ck_telegram_deliveries_6", "part_no > 0");
                    table.CheckConstraint("ck_telegram_deliveries_7", "status IN ('pending','leased','succeeded','retry','failed','cancelled','uncertain')");
                    table.CheckConstraint("ck_telegram_deliveries_8", "attempts >= 0");
                    table.CheckConstraint("ck_telegram_deliveries_9", "telegram_message_id > 0");
                    table.ForeignKey(
                        name: "telegram_deliveries_audit_event_id_fk",
                        column: x => x.audit_event_id,
                        principalSchema: "shop",
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_brand_id_fk",
                        column: x => x.brand_id,
                        principalSchema: "shop",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_campaign_id_campaign_batch_id_brand_id_fk",
                        columns: x => new { x.campaign_id, x.campaign_batch_id, x.brand_id },
                        principalSchema: "shop",
                        principalTable: "campaigns",
                        principalColumns: new[] { "id", "batch_id", "brand_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_discount_id_fk",
                        column: x => x.discount_id,
                        principalSchema: "shop",
                        principalTable: "discounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_fulfillment_id_fk",
                        column: x => x.fulfillment_id,
                        principalSchema: "shop",
                        principalTable: "fulfillments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_order_id_fk",
                        column: x => x.order_id,
                        principalSchema: "shop",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_product_stock_subscription_id_fk",
                        column: x => x.product_stock_subscription_id,
                        principalSchema: "shop",
                        principalTable: "product_stock_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_supplier_request_id_fk",
                        column: x => x.supplier_request_id,
                        principalSchema: "shop",
                        principalTable: "supplier_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_support_message_id_fk",
                        column: x => x.support_message_id,
                        principalSchema: "shop",
                        principalTable: "support_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "telegram_deliveries_user_id_fk",
                        column: x => x.user_id,
                        principalSchema: "shop",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_admin_id",
                schema: "shop",
                table: "admin_sessions",
                column: "admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_sessions_token_hash",
                schema: "shop",
                table: "admin_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_login",
                schema: "shop",
                table: "admins",
                column: "login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_user_id",
                schema: "shop",
                table: "admins",
                column: "telegram_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_verification_audit_id",
                schema: "shop",
                table: "admins",
                column: "telegram_verification_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_admins_telegram_verified_by_admin_id",
                schema: "shop",
                table: "admins",
                column: "telegram_verified_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_actor_admin_id",
                schema: "shop",
                table: "audit_events",
                column: "actor_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_actor_user_id",
                schema: "shop",
                table: "audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_brand_id",
                schema: "shop",
                table: "audit_events",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "activity_member_time",
                schema: "shop",
                table: "bot_activity_events",
                columns: new[] { "user_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "activity_retention",
                schema: "shop",
                table: "bot_activity_events",
                columns: new[] { "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_order_id",
                schema: "shop",
                table: "bot_activity_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_product_id",
                schema: "shop",
                table: "bot_activity_events",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_support_ticket_id",
                schema: "shop",
                table: "bot_activity_events",
                column: "support_ticket_id");

            migrationBuilder.CreateIndex(
                name: "IX_bot_activity_events_user_id_idempotency_key",
                schema: "shop",
                table: "bot_activity_events",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brand_products_product_id",
                schema: "shop",
                table: "brand_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "brands_distinct_iran_card",
                schema: "shop",
                table: "brands",
                column: "bank_card_fingerprint",
                unique: true,
                filter: "market='iran' AND allow_bank");

            migrationBuilder.CreateIndex(
                name: "IX_brands_bot_id",
                schema: "shop",
                table: "brands",
                column: "bot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brands_code",
                schema: "shop",
                table: "brands",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brands_customer_support_admin_id",
                schema: "shop",
                table: "brands",
                column: "customer_support_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_batch_id_brand_id_language",
                schema: "shop",
                table: "campaigns",
                columns: new[] { "batch_id", "brand_id", "language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_brand_id",
                schema: "shop",
                table: "campaigns",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_created_by_admin_id",
                schema: "shop",
                table: "campaigns",
                column: "created_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_id_batch_id_brand_id",
                schema: "shop",
                table: "campaigns",
                columns: new[] { "id", "batch_id", "brand_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_audit_event_id",
                schema: "shop",
                table: "discount_revisions",
                column: "audit_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_discount_id_revision_no",
                schema: "shop",
                table: "discount_revisions",
                columns: new[] { "discount_id", "revision_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_eligible_user_id",
                schema: "shop",
                table: "discount_revisions",
                column: "eligible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_revisions_product_id",
                schema: "shop",
                table: "discount_revisions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_uses_discount_revision_id",
                schema: "shop",
                table: "discount_uses",
                column: "discount_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_uses_order_id",
                schema: "shop",
                table: "discount_uses",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "discount_prepaid_source_once",
                schema: "shop",
                table: "discounts",
                column: "source_order_id",
                unique: true,
                filter: "kind='prepaid_entitlement'");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_brand_id_code_digest",
                schema: "shop",
                table: "discounts",
                columns: new[] { "brand_id", "code_digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discounts_created_by_admin_id",
                schema: "shop",
                table: "discounts",
                column: "created_by_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_eligible_user_id",
                schema: "shop",
                table: "discounts",
                column: "eligible_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_discounts_product_id",
                schema: "shop",
                table: "discounts",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_order_id_unit_no",
                schema: "shop",
                table: "fulfillments",
                columns: new[] { "order_id", "unit_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_resolution_admin_id",
                schema: "shop",
                table: "fulfillments",
                column: "resolution_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_supplier_item_key",
                schema: "shop",
                table: "fulfillments",
                column: "supplier_item_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillments_supplier_request_id",
                schema: "shop",
                table: "fulfillments",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "inventory_available_product",
                schema: "shop",
                table: "inventory_items",
                columns: new[] { "product_id", "created_at", "id" },
                filter: "state='available'");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_assigned_fulfillment_id",
                schema: "shop",
                table: "inventory_items",
                column: "assigned_fulfillment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_supplier_item_key",
                schema: "shop",
                table: "inventory_items",
                column: "supplier_item_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_supplier_request_id",
                schema: "shop",
                table: "inventory_items",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_audit_event_id",
                schema: "shop",
                table: "jobs",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_brand_id",
                schema: "shop",
                table: "jobs",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_campaign_id",
                schema: "shop",
                table: "jobs",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_dedupe_key",
                schema: "shop",
                table: "jobs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_discount_id",
                schema: "shop",
                table: "jobs",
                column: "discount_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_fulfillment_id",
                schema: "shop",
                table: "jobs",
                column: "fulfillment_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_order_id",
                schema: "shop",
                table: "jobs",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_payment_id",
                schema: "shop",
                table: "jobs",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_id",
                schema: "shop",
                table: "jobs",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_stock_subscription_id",
                schema: "shop",
                table: "jobs",
                column: "product_stock_subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_product_supplier_source_id",
                schema: "shop",
                table: "jobs",
                column: "product_supplier_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_supplier_request_id",
                schema: "shop",
                table: "jobs",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_support_message_id",
                schema: "shop",
                table: "jobs",
                column: "support_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_telegram_delivery_id",
                schema: "shop",
                table: "jobs",
                column: "telegram_delivery_id");

            migrationBuilder.CreateIndex(
                name: "jobs_claimable",
                schema: "shop",
                table: "jobs",
                columns: new[] { "available_at", "id" },
                filter: "status IN ('pending','retry')");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_id_map_target_brand_id",
                schema: "shop",
                table: "legacy_id_map",
                column: "target_brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_hold_admin_id",
                schema: "shop",
                table: "orders",
                column: "hold_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_id_delivery_generation",
                schema: "shop",
                table: "orders",
                columns: new[] { "id", "delivery_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_admin_id",
                schema: "shop",
                table: "orders",
                column: "resolution_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_audit_event_id",
                schema: "shop",
                table: "orders",
                column: "resolution_audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_resolution_wallet_entry_id",
                schema: "shop",
                table: "orders",
                column: "resolution_wallet_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "orders_one_awaiting",
                schema: "shop",
                table: "orders",
                column: "user_id",
                unique: true,
                filter: "payment_status='awaiting_payment'");

            migrationBuilder.CreateIndex(
                name: "orders_paid_preorder_fifo",
                schema: "shop",
                table: "orders",
                columns: new[] { "product_id", "payment_confirmed_at", "id" },
                filter: "is_preorder AND payment_status IN ('paid','not_required') AND fulfillment_status IN ('waiting_stock','requires_admin')");

            migrationBuilder.CreateIndex(
                name: "IX_payments_compensating_wallet_entry_id",
                schema: "shop",
                table: "payments",
                column: "compensating_wallet_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_hosted_invoice_ref",
                schema: "shop",
                table: "payments",
                column: "hosted_invoice_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_idempotency_ref",
                schema: "shop",
                table: "payments",
                column: "idempotency_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_order_id",
                schema: "shop",
                table: "payments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_review_admin_id",
                schema: "shop",
                table: "payments",
                column: "review_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_topup_user_id",
                schema: "shop",
                table: "payments",
                column: "topup_user_id");

            migrationBuilder.CreateIndex(
                name: "payments_provider_identity",
                schema: "shop",
                table: "payments",
                columns: new[] { "provider_code", "provider_payment_ref" },
                unique: true,
                filter: "provider_payment_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_stock_subscriptions_product_id",
                schema: "shop",
                table: "product_stock_subscriptions",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "subscription_active_once",
                schema: "shop",
                table: "product_stock_subscriptions",
                columns: new[] { "user_id", "product_id" },
                unique: true,
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "subscription_closed_retention",
                schema: "shop",
                table: "product_stock_subscriptions",
                columns: new[] { "closed_at", "id" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_supplier_sources_product_id_id",
                schema: "shop",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "source_with_variant",
                schema: "shop",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "provider_code", "external_product_id", "external_variant_id" },
                unique: true,
                filter: "external_variant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "source_without_variant",
                schema: "shop",
                table: "product_supplier_sources",
                columns: new[] { "product_id", "provider_code", "external_product_id" },
                unique: true,
                filter: "external_variant_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_products_id_active_supplier_source_id",
                schema: "shop",
                table: "products",
                columns: new[] { "id", "active_supplier_source_id" });

            migrationBuilder.CreateIndex(
                name: "IX_products_sku",
                schema: "shop",
                table: "products",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_admin_authorization_audit_id",
                schema: "shop",
                table: "supplier_request_attempts",
                column: "admin_authorization_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_parent_submit_attempt_id",
                schema: "shop",
                table: "supplier_request_attempts",
                column: "parent_submit_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_request_attempts_supplier_request_id_attempt_no",
                schema: "shop",
                table: "supplier_request_attempts",
                columns: new[] { "supplier_request_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_approval_audit_id",
                schema: "shop",
                table: "supplier_requests",
                column: "approval_audit_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_local_intent_key",
                schema: "shop",
                table: "supplier_requests",
                column: "local_intent_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_requests_product_supplier_source_id",
                schema: "shop",
                table: "supplier_requests",
                column: "product_supplier_source_id");

            migrationBuilder.CreateIndex(
                name: "supplier_order_unresolved",
                schema: "shop",
                table: "supplier_requests",
                column: "order_id",
                unique: true,
                filter: "status NOT IN ('failed','resolved','succeeded')");

            migrationBuilder.CreateIndex(
                name: "IX_support_messages_sender_admin_id",
                schema: "shop",
                table: "support_messages",
                column: "sender_admin_id");

            migrationBuilder.CreateIndex(
                name: "support_external_message_once",
                schema: "shop",
                table: "support_messages",
                columns: new[] { "ticket_id", "telegram_message_id" },
                unique: true,
                filter: "telegram_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_assigned_admin_id",
                schema: "shop",
                table: "support_tickets",
                column: "assigned_admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_order_id",
                schema: "shop",
                table: "support_tickets",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_support_tickets_user_id",
                schema: "shop",
                table: "support_tickets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "delivery_campaign_recipient_once",
                schema: "shop",
                table: "telegram_deliveries",
                columns: new[] { "campaign_batch_id", "user_id" },
                unique: true,
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "delivery_manifest_part_once",
                schema: "shop",
                table: "telegram_deliveries",
                columns: new[] { "order_id", "delivery_generation", "part_no", "purpose" },
                unique: true,
                filter: "purpose='order_credentials'");

            migrationBuilder.CreateIndex(
                name: "delivery_restock_once",
                schema: "shop",
                table: "telegram_deliveries",
                column: "product_stock_subscription_id",
                unique: true,
                filter: "product_stock_subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "delivery_staff_case_once",
                schema: "shop",
                table: "telegram_deliveries",
                columns: new[] { "supplier_request_id", "audit_event_id", "delivery_generation", "purpose" },
                unique: true,
                filter: "recipient_kind='staff_chat'");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_audit_event_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_brand_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_campaign_id_campaign_batch_id_brand_id",
                schema: "shop",
                table: "telegram_deliveries",
                columns: new[] { "campaign_id", "campaign_batch_id", "brand_id" });

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_dedupe_key",
                schema: "shop",
                table: "telegram_deliveries",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_discount_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "discount_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_fulfillment_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "fulfillment_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_support_message_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "support_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_deliveries_user_id",
                schema: "shop",
                table: "telegram_deliveries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_brand_id_telegram_id",
                schema: "shop",
                table: "users",
                columns: new[] { "brand_id", "telegram_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_flow_order_id",
                schema: "shop",
                table: "users",
                column: "flow_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_flow_product_id",
                schema: "shop",
                table: "users",
                column: "flow_product_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_id_brand_id",
                schema: "shop",
                table: "users",
                columns: new[] { "id", "brand_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_admin_id",
                schema: "shop",
                table: "wallet_entries",
                column: "admin_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_audit_event_id",
                schema: "shop",
                table: "wallet_entries",
                column: "audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_supplier_request_id",
                schema: "shop",
                table: "wallet_entries",
                column: "supplier_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_user_id_idempotency_key",
                schema: "shop",
                table: "wallet_entries",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "wallet_opening_once",
                schema: "shop",
                table: "wallet_entries",
                column: "user_id",
                unique: true,
                filter: "movement_kind='opening_balance' AND is_posting");

            migrationBuilder.CreateIndex(
                name: "wallet_payment_credit_once",
                schema: "shop",
                table: "wallet_entries",
                column: "payment_id",
                unique: true,
                filter: "movement_kind IN ('wallet_topup','immediate_crypto_shortage_credit','late_crypto_credit')");

            migrationBuilder.CreateIndex(
                name: "wallet_posting_version",
                schema: "shop",
                table: "wallet_entries",
                columns: new[] { "user_id", "posting_version" },
                unique: true,
                filter: "is_posting");

            migrationBuilder.CreateIndex(
                name: "wallet_supplier_resolution_once",
                schema: "shop",
                table: "wallet_entries",
                column: "order_id",
                unique: true,
                filter: "movement_kind='supplier_wallet_credit'");

            migrationBuilder.AddForeignKey(
                name: "admin_sessions_admin_id_fk",
                schema: "shop",
                table: "admin_sessions",
                column: "admin_id",
                principalSchema: "shop",
                principalTable: "admins",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "admins_telegram_verification_audit_id_fk",
                schema: "shop",
                table: "admins",
                column: "telegram_verification_audit_id",
                principalSchema: "shop",
                principalTable: "audit_events",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "audit_events_actor_user_id_fk",
                schema: "shop",
                table: "audit_events",
                column: "actor_user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_order_id_fk",
                schema: "shop",
                table: "bot_activity_events",
                column: "order_id",
                principalSchema: "shop",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_product_id_fk",
                schema: "shop",
                table: "bot_activity_events",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_support_ticket_id_fk",
                schema: "shop",
                table: "bot_activity_events",
                column: "support_ticket_id",
                principalSchema: "shop",
                principalTable: "support_tickets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "bot_activity_events_user_id_fk",
                schema: "shop",
                table: "bot_activity_events",
                column: "user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "brand_products_product_id_fk",
                schema: "shop",
                table: "brand_products",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_discount_id_fk",
                schema: "shop",
                table: "discount_revisions",
                column: "discount_id",
                principalSchema: "shop",
                principalTable: "discounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_eligible_user_id_fk",
                schema: "shop",
                table: "discount_revisions",
                column: "eligible_user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_revisions_product_id_fk",
                schema: "shop",
                table: "discount_revisions",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_uses_discount_id_fk",
                schema: "shop",
                table: "discount_uses",
                column: "discount_id",
                principalSchema: "shop",
                principalTable: "discounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discount_uses_order_id_fk",
                schema: "shop",
                table: "discount_uses",
                column: "order_id",
                principalSchema: "shop",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_eligible_user_id_fk",
                schema: "shop",
                table: "discounts",
                column: "eligible_user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_product_id_fk",
                schema: "shop",
                table: "discounts",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "discounts_source_order_id_fk",
                schema: "shop",
                table: "discounts",
                column: "source_order_id",
                principalSchema: "shop",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fulfillments_order_id_fk",
                schema: "shop",
                table: "fulfillments",
                column: "order_id",
                principalSchema: "shop",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fulfillments_supplier_request_id_fk",
                schema: "shop",
                table: "fulfillments",
                column: "supplier_request_id",
                principalSchema: "shop",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "inventory_items_product_id_fk",
                schema: "shop",
                table: "inventory_items",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "inventory_items_supplier_request_id_fk",
                schema: "shop",
                table: "inventory_items",
                column: "supplier_request_id",
                principalSchema: "shop",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_order_id_fk",
                schema: "shop",
                table: "jobs",
                column: "order_id",
                principalSchema: "shop",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_payment_id_fk",
                schema: "shop",
                table: "jobs",
                column: "payment_id",
                principalSchema: "shop",
                principalTable: "payments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_id_fk",
                schema: "shop",
                table: "jobs",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_stock_subscription_id_fk",
                schema: "shop",
                table: "jobs",
                column: "product_stock_subscription_id",
                principalSchema: "shop",
                principalTable: "product_stock_subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_product_supplier_source_id_fk",
                schema: "shop",
                table: "jobs",
                column: "product_supplier_source_id",
                principalSchema: "shop",
                principalTable: "product_supplier_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_supplier_request_id_fk",
                schema: "shop",
                table: "jobs",
                column: "supplier_request_id",
                principalSchema: "shop",
                principalTable: "supplier_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_support_message_id_fk",
                schema: "shop",
                table: "jobs",
                column: "support_message_id",
                principalSchema: "shop",
                principalTable: "support_messages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "jobs_telegram_delivery_id_fk",
                schema: "shop",
                table: "jobs",
                column: "telegram_delivery_id",
                principalSchema: "shop",
                principalTable: "telegram_deliveries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_product_id_fk",
                schema: "shop",
                table: "orders",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_resolution_wallet_entry_id_fk",
                schema: "shop",
                table: "orders",
                column: "resolution_wallet_entry_id",
                principalSchema: "shop",
                principalTable: "wallet_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "orders_user_id_fk",
                schema: "shop",
                table: "orders",
                column: "user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "payments_compensating_wallet_entry_id_fk",
                schema: "shop",
                table: "payments",
                column: "compensating_wallet_entry_id",
                principalSchema: "shop",
                principalTable: "wallet_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "payments_topup_user_id_fk",
                schema: "shop",
                table: "payments",
                column: "topup_user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_stock_subscriptions_product_id_fk",
                schema: "shop",
                table: "product_stock_subscriptions",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_stock_subscriptions_user_id_fk",
                schema: "shop",
                table: "product_stock_subscriptions",
                column: "user_id",
                principalSchema: "shop",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "product_supplier_sources_product_id_fk",
                schema: "shop",
                table: "product_supplier_sources",
                column: "product_id",
                principalSchema: "shop",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(EfMigrationGuards.PostgresBaseline);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The baseline cannot be downgraded; use explicit verified recovery.");
    }
}
