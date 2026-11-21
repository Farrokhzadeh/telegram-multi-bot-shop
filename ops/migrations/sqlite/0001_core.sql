CREATE TABLE brands (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    code TEXT CHECK (code IS NULL OR (length(code) BETWEEN 1 AND 160 AND trim(code)<>'')) NOT NULL UNIQUE,
    internal_name TEXT CHECK (internal_name IS NULL OR (length(internal_name) BETWEEN 1 AND 160 AND trim(internal_name)<>'')) NOT NULL,
    public_name TEXT CHECK (public_name IS NULL OR (length(public_name) BETWEEN 1 AND 160 AND trim(public_name)<>'')) NOT NULL,
    market TEXT NOT NULL CHECK (market IN ('iran','international')),
    default_language TEXT CHECK (default_language IS NULL OR (length(default_language) BETWEEN 1 AND 160 AND trim(default_language)<>'')) NOT NULL,
    logo_asset_ref TEXT CHECK (logo_asset_ref IS NULL OR (length(logo_asset_ref) BETWEEN 1 AND 1024)),
    bot_username TEXT CHECK (bot_username IS NULL OR (length(bot_username) BETWEEN 1 AND 160 AND trim(bot_username)<>'')) NOT NULL,
    bot_id INTEGER NOT NULL UNIQUE CHECK (bot_id > 0),
    bot_token_ciphertext BLOB NOT NULL CHECK (length(bot_token_ciphertext) > 0),
    bot_token_key_id TEXT CHECK (bot_token_key_id IS NULL OR (length(bot_token_key_id) BETWEEN 1 AND 160 AND trim(bot_token_key_id)<>'')) NOT NULL,
    active INTEGER CHECK (active IS NULL OR (active IN (0,1))) NOT NULL DEFAULT false,
    allow_wallet INTEGER CHECK (allow_wallet IS NULL OR (allow_wallet IN (0,1))) NOT NULL DEFAULT true,
    allow_crypto INTEGER CHECK (allow_crypto IS NULL OR (allow_crypto IN (0,1))) NOT NULL DEFAULT false,
    allow_bank INTEGER CHECK (allow_bank IS NULL OR (allow_bank IN (0,1))) NOT NULL DEFAULT false,
    allow_wallet_topup INTEGER CHECK (allow_wallet_topup IS NULL OR (allow_wallet_topup IN (0,1))) NOT NULL DEFAULT false,
    zero_balance_topup INTEGER CHECK (zero_balance_topup IS NULL OR (zero_balance_topup IN (0,1))) NOT NULL DEFAULT false,
    require_channel_join INTEGER CHECK (require_channel_join IS NULL OR (require_channel_join IN (0,1))) NOT NULL DEFAULT false,
    support_enabled INTEGER CHECK (support_enabled IS NULL OR (support_enabled IN (0,1))) NOT NULL DEFAULT false,
    fx_enabled INTEGER CHECK (fx_enabled IS NULL OR (fx_enabled IN (0,1))) NOT NULL DEFAULT false,
    customer_support_admin_id TEXT COLLATE NOCASE CHECK (customer_support_admin_id IS NULL OR akanti_uuid(customer_support_admin_id)),
    bank_number_ciphertext BLOB,
    bank_holder_ciphertext BLOB,
    bank_key_id TEXT CHECK (bank_key_id IS NULL OR (length(bank_key_id) BETWEEN 1 AND 160 AND trim(bank_key_id)<>'')),
    bank_card_fingerprint BLOB,
    bank_changed_at TEXT,
    current_fx TEXT CHECK (current_fx IS NULL OR (akanti_decimal(current_fx)=1)) CHECK (akanti_decimal_compare(current_fx,'0') > 0),
    fx_source TEXT CHECK (fx_source IS NULL OR (length(fx_source) BETWEEN 1 AND 160 AND trim(fx_source)<>'')),
    fx_fetched_at TEXT,
    fx_error_code TEXT CHECK (fx_error_code IS NULL OR (length(fx_error_code) BETWEEN 1 AND 160 AND trim(fx_error_code)<>'')),
    pricing_rounding_step TEXT CHECK (pricing_rounding_step IS NULL OR (akanti_decimal(pricing_rounding_step)=1)) NOT NULL DEFAULT 5000 CHECK (akanti_decimal_compare(pricing_rounding_step,'0') > 0),
    max_exchange_rate_age_seconds INTEGER NOT NULL DEFAULT 3600 CHECK (max_exchange_rate_age_seconds > 0),
    telegram_update_highwater INTEGER NOT NULL DEFAULT -1 CHECK (telegram_update_highwater >= -1),
    telegram_update_window BLOB NOT NULL DEFAULT (zeroblob(128)) CHECK (length(telegram_update_window)=128),
    policy_version INTEGER NOT NULL DEFAULT 0 CHECK (policy_version >= 0),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK (((bank_number_ciphertext IS NOT NULL) + (bank_holder_ciphertext IS NOT NULL) + (bank_key_id IS NOT NULL) + (bank_card_fingerprint IS NOT NULL) + (bank_changed_at IS NOT NULL)) IN (0,5)),
    CHECK (bank_card_fingerprint IS NULL OR length(bank_card_fingerprint)=32),
    CHECK (NOT allow_bank OR (market='iran' AND bank_card_fingerprint IS NOT NULL)),
    CHECK (((current_fx IS NOT NULL) + (fx_source IS NOT NULL) + (fx_fetched_at IS NOT NULL)) IN (0,3)),
    CHECK (NOT (active AND support_enabled) OR customer_support_admin_id IS NOT NULL),
    CHECK (bank_changed_at IS NULL OR akanti_utc_time(bank_changed_at)),
    CHECK (fx_fetched_at IS NULL OR akanti_utc_time(fx_fetched_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT brands_customer_support_admin_id_fk FOREIGN KEY (customer_support_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE brand_channels (
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    chat_id INTEGER NOT NULL CHECK (chat_id < 0),
    title TEXT CHECK (title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')),
    invite_link TEXT CHECK (invite_link IS NULL OR (length(invite_link) BETWEEN 1 AND 1024)),
    required INTEGER CHECK (required IS NULL OR (required IN (0,1))) NOT NULL DEFAULT true,
    active INTEGER CHECK (active IS NULL OR (active IN (0,1))) NOT NULL DEFAULT true,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    PRIMARY KEY (brand_id,chat_id),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT brand_channels_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE users (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    telegram_id INTEGER NOT NULL CHECK (telegram_id > 0),
    username TEXT CHECK (username IS NULL OR (length(username) BETWEEN 1 AND 160 AND trim(username)<>'')),
    first_name TEXT CHECK (first_name IS NULL OR (length(first_name) BETWEEN 1 AND 160 AND trim(first_name)<>'')),
    last_name TEXT CHECK (last_name IS NULL OR (length(last_name) BETWEEN 1 AND 160 AND trim(last_name)<>'')),
    language TEXT CHECK (language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')) NOT NULL,
    blocked_at TEXT,
    banned_at TEXT,
    marketing_opt_out_at TEXT,
    first_seen_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    last_seen_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    flow_state TEXT NOT NULL DEFAULT 'idle' CHECK (flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')),
    flow_revision INTEGER NOT NULL DEFAULT 0 CHECK (flow_revision >= 0),
    flow_order_id TEXT COLLATE NOCASE CHECK (flow_order_id IS NULL OR akanti_uuid(flow_order_id)),
    flow_product_id TEXT COLLATE NOCASE CHECK (flow_product_id IS NULL OR akanti_uuid(flow_product_id)),
    flow_anchor_message_id INTEGER CHECK (flow_anchor_message_id > 0),
    flow_expires_at TEXT,
    UNIQUE (brand_id,telegram_id),
    UNIQUE (id,brand_id),
    CHECK (blocked_at IS NULL OR akanti_utc_time(blocked_at)),
    CHECK (banned_at IS NULL OR akanti_utc_time(banned_at)),
    CHECK (marketing_opt_out_at IS NULL OR akanti_utc_time(marketing_opt_out_at)),
    CHECK (first_seen_at IS NULL OR akanti_utc_time(first_seen_at)),
    CHECK (last_seen_at IS NULL OR akanti_utc_time(last_seen_at)),
    CHECK (flow_expires_at IS NULL OR akanti_utc_time(flow_expires_at)),
    CONSTRAINT users_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT users_flow_order_id_fk FOREIGN KEY (flow_order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT users_flow_product_id_fk FOREIGN KEY (flow_product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE admins (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    login TEXT CHECK (login IS NULL OR (length(login) BETWEEN 1 AND 160 AND trim(login)<>'')) NOT NULL UNIQUE,
    authentication_mode TEXT NOT NULL CHECK (authentication_mode IN ('password','external')),
    password_verifier TEXT CHECK (password_verifier IS NULL OR (length(password_verifier) BETWEEN 1 AND 160 AND trim(password_verifier)<>'')),
    external_subject TEXT CHECK (external_subject IS NULL OR (length(external_subject) BETWEEN 1 AND 160 AND trim(external_subject)<>'')),
    disabled_at TEXT,
    auth_version INTEGER NOT NULL DEFAULT 0 CHECK (auth_version >= 0),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    last_login_at TEXT,
    telegram_user_id INTEGER UNIQUE CHECK (telegram_user_id > 0),
    public_contact_handle TEXT CHECK (public_contact_handle IS NULL OR (length(public_contact_handle) BETWEEN 1 AND 160 AND trim(public_contact_handle)<>'')),
    telegram_verified_by_admin_id TEXT COLLATE NOCASE CHECK (telegram_verified_by_admin_id IS NULL OR akanti_uuid(telegram_verified_by_admin_id)),
    telegram_verified_at TEXT,
    telegram_verification_audit_id TEXT COLLATE NOCASE CHECK (telegram_verification_audit_id IS NULL OR akanti_uuid(telegram_verification_audit_id)),
    binding_challenge_hash BLOB CHECK (length(binding_challenge_hash)=32),
    binding_candidate_telegram_id INTEGER CHECK (binding_candidate_telegram_id > 0),
    binding_expires_at TEXT,
    CHECK ((authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR
           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)),
    CHECK (((telegram_user_id IS NOT NULL) + (telegram_verified_by_admin_id IS NOT NULL) + (telegram_verified_at IS NOT NULL) + (telegram_verification_audit_id IS NOT NULL)) IN (0,4)),
    CHECK (public_contact_handle IS NULL OR telegram_user_id IS NOT NULL),
    CHECK (((binding_challenge_hash IS NOT NULL) + (binding_candidate_telegram_id IS NOT NULL) + (binding_expires_at IS NOT NULL)) IN (0,3)),
    CHECK (disabled_at IS NULL OR akanti_utc_time(disabled_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (last_login_at IS NULL OR akanti_utc_time(last_login_at)),
    CHECK (telegram_verified_at IS NULL OR akanti_utc_time(telegram_verified_at)),
    CHECK (binding_expires_at IS NULL OR akanti_utc_time(binding_expires_at)),
    CONSTRAINT admins_telegram_verified_by_admin_id_fk FOREIGN KEY (telegram_verified_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT admins_telegram_verification_audit_id_fk FOREIGN KEY (telegram_verification_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE admin_sessions (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    admin_id TEXT COLLATE NOCASE CHECK (admin_id IS NULL OR akanti_uuid(admin_id)) NOT NULL,
    token_hash BLOB NOT NULL UNIQUE CHECK (length(token_hash)=32),
    auth_version_at_issue INTEGER NOT NULL CHECK (auth_version_at_issue >= 0),
    issued_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    expires_at TEXT NOT NULL,
    revoked_at TEXT,
    last_used_at TEXT,
    CHECK (expires_at > issued_at),
    CHECK (revoked_at IS NULL OR revoked_at >= issued_at),
    CHECK (issued_at IS NULL OR akanti_utc_time(issued_at)),
    CHECK (expires_at IS NULL OR akanti_utc_time(expires_at)),
    CHECK (revoked_at IS NULL OR akanti_utc_time(revoked_at)),
    CHECK (last_used_at IS NULL OR akanti_utc_time(last_used_at)),
    CONSTRAINT admin_sessions_admin_id_fk FOREIGN KEY (admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE products (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    sku TEXT CHECK (sku IS NULL OR (length(sku) BETWEEN 1 AND 160 AND trim(sku)<>'')) UNIQUE,
    fulfillment_kind TEXT NOT NULL CHECK (fulfillment_kind IN ('local','supplier','manual')),
    local_first INTEGER CHECK (local_first IS NULL OR (local_first IN (0,1))) NOT NULL DEFAULT true,
    availability_policy TEXT NOT NULL CHECK (availability_policy IN ('immediate','local_preorder')),
    active_supplier_source_id TEXT COLLATE NOCASE CHECK (active_supplier_source_id IS NULL OR akanti_uuid(active_supplier_source_id)),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    title TEXT CHECK (title IS NULL OR (length(title) BETWEEN 1 AND 160 AND trim(title)<>'')) NOT NULL,
    description TEXT CHECK (description IS NULL OR (length(description)<=16384)),
    icon_ref TEXT CHECK (icon_ref IS NULL OR (length(icon_ref) BETWEEN 1 AND 1024)),
    guidance TEXT CHECK (guidance IS NULL OR (length(guidance)<=16384)),
    active INTEGER CHECK (active IS NULL OR (active IN (0,1))) NOT NULL DEFAULT true,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK (availability_policy <> 'local_preorder' OR fulfillment_kind='local'),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT products_id_active_supplier_source_id_fk FOREIGN KEY (id,active_supplier_source_id) REFERENCES product_supplier_sources (product_id,id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE brand_products (
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)) NOT NULL,
    visible INTEGER CHECK (visible IS NULL OR (visible IN (0,1))) NOT NULL DEFAULT false,
    title_mode TEXT NOT NULL DEFAULT 'inherit' CHECK (title_mode IN ('inherit','override')),
    title_override TEXT CHECK (title_override IS NULL OR (length(title_override) BETWEEN 1 AND 160 AND trim(title_override)<>'')),
    description_mode TEXT NOT NULL DEFAULT 'inherit' CHECK (description_mode IN ('inherit','override','clear')),
    description_override TEXT CHECK (description_override IS NULL OR (length(description_override)<=16384)),
    icon_mode TEXT NOT NULL DEFAULT 'inherit' CHECK (icon_mode IN ('inherit','override','clear')),
    icon_override TEXT CHECK (icon_override IS NULL OR (length(icon_override) BETWEEN 1 AND 1024)),
    guidance_mode TEXT NOT NULL DEFAULT 'inherit' CHECK (guidance_mode IN ('inherit','override','clear')),
    guidance_override TEXT CHECK (guidance_override IS NULL OR (length(guidance_override)<=16384)),
    price_usdt TEXT CHECK (price_usdt IS NULL OR (akanti_decimal(price_usdt)=1)) NOT NULL CHECK (akanti_decimal_compare(price_usdt,'0') > 0),
    price_toman TEXT CHECK (price_toman IS NULL OR (akanti_decimal(price_toman)=1)) CHECK (akanti_decimal_compare(price_toman,'0') > 0),
    auto_price_toman INTEGER CHECK (auto_price_toman IS NULL OR (auto_price_toman IN (0,1))) NOT NULL DEFAULT true,
    allow_wallet INTEGER CHECK (allow_wallet IS NULL OR (allow_wallet IN (0,1))) NOT NULL DEFAULT true,
    allow_crypto INTEGER CHECK (allow_crypto IS NULL OR (allow_crypto IN (0,1))) NOT NULL DEFAULT false,
    allow_bank INTEGER CHECK (allow_bank IS NULL OR (allow_bank IN (0,1))) NOT NULL DEFAULT false,
    min_quantity INTEGER NOT NULL DEFAULT 1 CHECK (min_quantity > 0),
    max_quantity INTEGER NOT NULL DEFAULT 100 CHECK (max_quantity >= min_quantity),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    PRIMARY KEY (brand_id,product_id),
    CHECK ((title_mode='override')=(title_override IS NOT NULL)),
    CHECK ((description_mode='override')=(description_override IS NOT NULL)),
    CHECK ((icon_mode='override')=(icon_override IS NOT NULL)),
    CHECK ((guidance_mode='override')=(guidance_override IS NOT NULL)),
    CHECK (NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT brand_products_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT brand_products_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE inventory_items (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)) NOT NULL,
    credential_ciphertext BLOB NOT NULL CHECK (length(credential_ciphertext)>0),
    credential_key_id TEXT CHECK (credential_key_id IS NULL OR (length(credential_key_id) BETWEEN 1 AND 160 AND trim(credential_key_id)<>'')) NOT NULL,
    source_fingerprint BLOB NOT NULL CHECK (length(source_fingerprint)=32),
    origin_kind TEXT NOT NULL CHECK (origin_kind IN ('local_import','supplier_bonus')),
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)),
    remote_item_ref TEXT CHECK (remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')),
    supplier_item_key BLOB UNIQUE CHECK (length(supplier_item_key)=32),
    state TEXT NOT NULL DEFAULT 'available' CHECK (state IN ('available','allocated','retired','admin_held')),
    assigned_fulfillment_id TEXT COLLATE NOCASE CHECK (assigned_fulfillment_id IS NULL OR akanti_uuid(assigned_fulfillment_id)) UNIQUE,
    allocated_at TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK ((state='allocated')=(assigned_fulfillment_id IS NOT NULL)),
    CHECK ((assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)),
    CHECK ((origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR
           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')),
    CHECK (allocated_at IS NULL OR akanti_utc_time(allocated_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT inventory_items_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT inventory_items_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT inventory_items_assigned_fulfillment_id_fk FOREIGN KEY (assigned_fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE wallets (
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) PRIMARY KEY,
    balance TEXT CHECK (balance IS NULL OR (akanti_decimal(balance)=1)) NOT NULL DEFAULT 0 CHECK (akanti_decimal_compare(balance,'0') >= 0),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT wallets_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE wallet_entries (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) NOT NULL,
    signed_amount TEXT CHECK (signed_amount IS NULL OR (akanti_decimal(signed_amount)=1)) NOT NULL CHECK (akanti_decimal_compare(signed_amount,'0') <> 0),
    balance_after TEXT CHECK (balance_after IS NULL OR (akanti_decimal(balance_after)=1)) CHECK (akanti_decimal_compare(balance_after,'0') >= 0),
    posting_version INTEGER CHECK (posting_version > 0),
    movement_kind TEXT NOT NULL CHECK (movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')),
    is_posting INTEGER CHECK (is_posting IS NULL OR (is_posting IN (0,1))) NOT NULL,
    idempotency_key TEXT CHECK (idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')) NOT NULL,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    payment_id TEXT COLLATE NOCASE CHECK (payment_id IS NULL OR akanti_uuid(payment_id)),
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)),
    legacy_import_batch TEXT COLLATE NOCASE CHECK (legacy_import_batch IS NULL OR akanti_uuid(legacy_import_batch)),
    admin_id TEXT COLLATE NOCASE CHECK (admin_id IS NULL OR akanti_uuid(admin_id)),
    audit_event_id TEXT COLLATE NOCASE CHECK (audit_event_id IS NULL OR akanti_uuid(audit_event_id)),
    reason TEXT CHECK (reason IS NULL OR (length(reason)<=16384)) NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    UNIQUE (user_id,idempotency_key),
    CHECK ((is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR
           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)),
    CHECK (movement_kind <> 'purchase_debit' OR akanti_decimal_compare(signed_amount,'0') < 0),
    CHECK (movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR akanti_decimal_compare(signed_amount,'0') > 0),
    CHECK ((movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR
           (movement_kind='legacy_history' AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR
           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND ((order_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR
           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND ((payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR
           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND ((supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL) + (admin_id IS NOT NULL) + (audit_event_id IS NOT NULL))=0) OR
           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((payment_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0) OR
           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND ((order_id IS NOT NULL) + (payment_id IS NOT NULL) + (supplier_request_id IS NOT NULL) + (legacy_import_batch IS NOT NULL))=0)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT wallet_entries_user_id_fk FOREIGN KEY (user_id) REFERENCES wallets (user_id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT wallet_entries_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT wallet_entries_payment_id_fk FOREIGN KEY (payment_id) REFERENCES payments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT wallet_entries_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT wallet_entries_admin_id_fk FOREIGN KEY (admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT wallet_entries_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE orders (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) NOT NULL,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    order_kind TEXT NOT NULL CHECK (order_kind IN ('product_purchase','prepaid_units')),
    payment_status TEXT NOT NULL CHECK (payment_status IN ('awaiting_payment','under_review','confirmation_pending','paid','not_required','expired','cancelled')),
    fulfillment_status TEXT NOT NULL DEFAULT 'unfulfilled' CHECK (fulfillment_status IN ('unfulfilled','waiting_stock','acquiring','requires_admin','ready','delivering','completed','cancelled','compensated')),
    currency TEXT NOT NULL CHECK (currency IN ('USDT','TOMAN')),
    payment_method TEXT NOT NULL CHECK (payment_method IN ('wallet','crypto','bank','no_payment_required')),
    unit_price_usdt TEXT CHECK (unit_price_usdt IS NULL OR (akanti_decimal(unit_price_usdt)=1)) NOT NULL CHECK (akanti_decimal_compare(unit_price_usdt,'0') > 0),
    unit_price_toman TEXT CHECK (unit_price_toman IS NULL OR (akanti_decimal(unit_price_toman)=1)) CHECK (akanti_decimal_compare(unit_price_toman,'0') > 0),
    fx_rate_snapshot TEXT CHECK (fx_rate_snapshot IS NULL OR (akanti_decimal(fx_rate_snapshot)=1)) CHECK (akanti_decimal_compare(fx_rate_snapshot,'0') > 0),
    fx_source_snapshot TEXT CHECK (fx_source_snapshot IS NULL OR (length(fx_source_snapshot) BETWEEN 1 AND 160 AND trim(fx_source_snapshot)<>'')),
    fx_fetched_at_snapshot TEXT,
    auto_price_toman_snapshot INTEGER CHECK (auto_price_toman_snapshot IS NULL OR (auto_price_toman_snapshot IN (0,1))) NOT NULL,
    pricing_rounding_step_snapshot TEXT CHECK (pricing_rounding_step_snapshot IS NULL OR (akanti_decimal(pricing_rounding_step_snapshot)=1)) NOT NULL CHECK (akanti_decimal_compare(pricing_rounding_step_snapshot,'0') > 0),
    price_calculation_version TEXT CHECK (price_calculation_version IS NULL OR (length(price_calculation_version) BETWEEN 1 AND 160 AND trim(price_calculation_version)<>'')) NOT NULL,
    discount_pct_snapshot TEXT CHECK (discount_pct_snapshot IS NULL OR (akanti_decimal(discount_pct_snapshot)=1)) CHECK ((akanti_decimal_compare(discount_pct_snapshot,'0') >= 0 AND akanti_decimal_compare(discount_pct_snapshot,'100') <= 0)),
    discount_units_snapshot INTEGER CHECK (discount_units_snapshot > 0),
    payment_due_at TEXT,
    payment_confirmed_at TEXT,
    brand_public_name_snapshot TEXT CHECK (brand_public_name_snapshot IS NULL OR (length(brand_public_name_snapshot) BETWEEN 1 AND 160 AND trim(brand_public_name_snapshot)<>'')) NOT NULL,
    product_title_snapshot TEXT CHECK (product_title_snapshot IS NULL OR (length(product_title_snapshot) BETWEEN 1 AND 160 AND trim(product_title_snapshot)<>'')) NOT NULL,
    product_description_snapshot TEXT CHECK (product_description_snapshot IS NULL OR (length(product_description_snapshot)<=16384)),
    product_icon_snapshot TEXT CHECK (product_icon_snapshot IS NULL OR (length(product_icon_snapshot) BETWEEN 1 AND 1024)),
    product_guidance_snapshot TEXT CHECK (product_guidance_snapshot IS NULL OR (length(product_guidance_snapshot)<=16384)),
    payment_terms_snapshot TEXT CHECK (payment_terms_snapshot IS NULL OR (length(payment_terms_snapshot)<=16384)) NOT NULL,
    fulfillment_kind_snapshot TEXT NOT NULL CHECK (fulfillment_kind_snapshot IN ('local','supplier','manual')),
    local_first_snapshot INTEGER CHECK (local_first_snapshot IS NULL OR (local_first_snapshot IN (0,1))) NOT NULL,
    is_preorder INTEGER CHECK (is_preorder IS NULL OR (is_preorder IN (0,1))) NOT NULL,
    review_code TEXT CHECK (review_code IS NULL OR (review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'))),
    hold_at TEXT,
    hold_admin_id TEXT COLLATE NOCASE CHECK (hold_admin_id IS NULL OR akanti_uuid(hold_admin_id)),
    hold_reason TEXT CHECK (hold_reason IS NULL OR (length(hold_reason)<=16384)),
    resolution_kind TEXT CHECK (resolution_kind IN ('replaced','unpaid_expiry','local_preorder_cancellation','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','manual_fulfilled')),
    resolution_admin_id TEXT COLLATE NOCASE CHECK (resolution_admin_id IS NULL OR akanti_uuid(resolution_admin_id)),
    resolution_at TEXT,
    resolution_audit_event_id TEXT COLLATE NOCASE CHECK (resolution_audit_event_id IS NULL OR akanti_uuid(resolution_audit_event_id)),
    resolution_wallet_entry_id TEXT COLLATE NOCASE CHECK (resolution_wallet_entry_id IS NULL OR akanti_uuid(resolution_wallet_entry_id)) UNIQUE,
    delivery_generation TEXT COLLATE NOCASE CHECK (delivery_generation IS NULL OR akanti_uuid(delivery_generation)),
    manifest_asset_ref TEXT CHECK (manifest_asset_ref IS NULL OR (length(manifest_asset_ref) BETWEEN 1 AND 1024)),
    manifest_digest BLOB CHECK (length(manifest_digest)=32),
    manifest_prepared_at TEXT,
    release_authorized_at TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    UNIQUE (id,delivery_generation),
    CHECK (((hold_at IS NOT NULL) + (hold_admin_id IS NOT NULL) + (hold_reason IS NOT NULL)) IN (0,3)),
    CHECK (((manifest_asset_ref IS NOT NULL) + (manifest_digest IS NOT NULL) + (manifest_prepared_at IS NOT NULL) + (delivery_generation IS NOT NULL)) IN (0,4)),
    CHECK (release_authorized_at IS NULL OR (manifest_prepared_at IS NOT NULL AND release_authorized_at >= manifest_prepared_at)),
    CHECK (((fx_rate_snapshot IS NOT NULL) + (fx_source_snapshot IS NOT NULL) + (fx_fetched_at_snapshot IS NOT NULL)) IN (0,3)),
    CHECK (((discount_pct_snapshot IS NOT NULL) + (discount_units_snapshot IS NOT NULL)) IN (0,2)),
    CHECK (discount_units_snapshot IS NULL OR discount_units_snapshot=quantity),
    CHECK ((currency='TOMAN')=(payment_method='bank')),
    CHECK (currency <> 'TOMAN' OR unit_price_toman IS NOT NULL),
    CHECK (payment_method <> 'no_payment_required' OR (payment_status='not_required' AND discount_pct_snapshot IS NOT NULL AND discount_pct_snapshot=100)),
    CHECK (payment_status <> 'not_required' OR payment_method='no_payment_required'),
    CHECK (payment_status NOT IN ('paid','not_required') OR payment_confirmed_at IS NOT NULL),
    CHECK (payment_status NOT IN ('awaiting_payment','under_review','confirmation_pending') OR (payment_due_at IS NOT NULL AND payment_method IN ('crypto','bank'))),
    CHECK (payment_due_at IS NULL OR payment_due_at=akanti_add_minutes(created_at,30)),
    CHECK (NOT is_preorder OR (order_kind='product_purchase' AND fulfillment_kind_snapshot='local')),
    CHECK ((resolution_kind IS NULL AND ((resolution_admin_id IS NOT NULL) + (resolution_at IS NOT NULL) + (resolution_audit_event_id IS NOT NULL) + (resolution_wallet_entry_id IS NOT NULL))=0) OR
           (resolution_kind IS NOT NULL AND resolution_at IS NOT NULL)),
    CHECK (resolution_kind NOT IN ('local_preorder_cancellation','supplier_wallet_credit','manual_fulfilled') OR
           (resolution_admin_id IS NOT NULL AND resolution_audit_event_id IS NOT NULL)),
    CHECK (resolution_kind NOT IN ('immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit') OR resolution_wallet_entry_id IS NOT NULL),
    CHECK (resolution_kind <> 'local_preorder_cancellation' OR (is_preorder AND resolution_wallet_entry_id IS NULL AND fulfillment_status='cancelled')),
    CHECK (resolution_kind NOT IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') OR fulfillment_status='compensated'),
    CHECK (fx_fetched_at_snapshot IS NULL OR akanti_utc_time(fx_fetched_at_snapshot)),
    CHECK (payment_due_at IS NULL OR akanti_utc_time(payment_due_at)),
    CHECK (payment_confirmed_at IS NULL OR akanti_utc_time(payment_confirmed_at)),
    CHECK (hold_at IS NULL OR akanti_utc_time(hold_at)),
    CHECK (resolution_at IS NULL OR akanti_utc_time(resolution_at)),
    CHECK (manifest_prepared_at IS NULL OR akanti_utc_time(manifest_prepared_at)),
    CHECK (release_authorized_at IS NULL OR akanti_utc_time(release_authorized_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT orders_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT orders_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT orders_hold_admin_id_fk FOREIGN KEY (hold_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT orders_resolution_admin_id_fk FOREIGN KEY (resolution_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT orders_resolution_audit_event_id_fk FOREIGN KEY (resolution_audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT orders_resolution_wallet_entry_id_fk FOREIGN KEY (resolution_wallet_entry_id) REFERENCES wallet_entries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE payments (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    topup_user_id TEXT COLLATE NOCASE CHECK (topup_user_id IS NULL OR akanti_uuid(topup_user_id)),
    payment_method TEXT NOT NULL CHECK (payment_method IN ('crypto','bank')),
    quote_currency TEXT NOT NULL CHECK (quote_currency IN ('USD','TOMAN')),
    amount_due TEXT CHECK (amount_due IS NULL OR (akanti_decimal(amount_due)=1)) NOT NULL CHECK (akanti_decimal_compare(amount_due,'0') > 0),
    amount_received TEXT CHECK (amount_received IS NULL OR (akanti_decimal(amount_received)=1)) CHECK (akanti_decimal_compare(amount_received,'0') >= 0),
    provider_code TEXT CHECK (provider_code IS NULL OR (length(provider_code) BETWEEN 1 AND 160 AND trim(provider_code)<>'')),
    provider_payment_ref TEXT CHECK (provider_payment_ref IS NULL OR (length(provider_payment_ref) BETWEEN 1 AND 160 AND trim(provider_payment_ref)<>'')),
    hosted_invoice_started_at TEXT,
    hosted_invoice_ref TEXT CHECK (hosted_invoice_ref IS NULL OR (length(hosted_invoice_ref) BETWEEN 1 AND 160 AND trim(hosted_invoice_ref)<>'')) UNIQUE CHECK (length(hosted_invoice_ref) BETWEEN 1 AND 64 AND hosted_invoice_ref NOT GLOB '*[^0-9]*'),
    hosted_invoice_url TEXT CHECK (hosted_invoice_url IS NULL OR (length(hosted_invoice_url) BETWEEN 1 AND 1024)),
    idempotency_ref TEXT CHECK (idempotency_ref IS NULL OR (length(idempotency_ref) BETWEEN 1 AND 160 AND trim(idempotency_ref)<>'')) NOT NULL UNIQUE,
    network_asset TEXT CHECK (network_asset IN ('USDTTRC20','USDTBSC')),
    expected_network_amount TEXT CHECK (expected_network_amount IS NULL OR (akanti_decimal(expected_network_amount)=1)) CHECK (akanti_decimal_compare(expected_network_amount,'0') > 0),
    ipn_key_id TEXT CHECK (ipn_key_id IS NULL OR (length(ipn_key_id) BETWEEN 1 AND 160 AND trim(ipn_key_id)<>'')),
    ipn_signature BLOB CHECK (length(ipn_signature)=64),
    ipn_payload_sha256 BLOB CHECK (length(ipn_payload_sha256)=32),
    ipn_verified_at TEXT,
    network_amount TEXT CHECK (network_amount IS NULL OR (akanti_decimal(network_amount)=1)) CHECK (akanti_decimal_compare(network_amount,'0') >= 0),
    provider_confirmations INTEGER CHECK (provider_confirmations >= 0),
    first_payment_observed_at TEXT,
    confirmed_at TEXT,
    status TEXT NOT NULL DEFAULT 'created' CHECK (status IN ('created','awaiting','observed','confirming','settled','rejected','expired','review')),
    provider_status_code TEXT CHECK (provider_status_code IS NULL OR (length(provider_status_code) BETWEEN 1 AND 160 AND trim(provider_status_code)<>'')),
    bank_destination_ciphertext BLOB,
    bank_destination_key_id TEXT CHECK (bank_destination_key_id IS NULL OR (length(bank_destination_key_id) BETWEEN 1 AND 160 AND trim(bank_destination_key_id)<>'')),
    receipt_asset_ref TEXT CHECK (receipt_asset_ref IS NULL OR (length(receipt_asset_ref) BETWEEN 1 AND 1024)),
    review_admin_id TEXT COLLATE NOCASE CHECK (review_admin_id IS NULL OR akanti_uuid(review_admin_id)),
    reviewed_at TEXT,
    review_reason TEXT CHECK (review_reason IS NULL OR (length(review_reason)<=16384)),
    compensating_wallet_entry_id TEXT COLLATE NOCASE CHECK (compensating_wallet_entry_id IS NULL OR akanti_uuid(compensating_wallet_entry_id)) UNIQUE,
    evidence_asset_ref TEXT CHECK (evidence_asset_ref IS NULL OR (length(evidence_asset_ref) BETWEEN 1 AND 1024)),
    pending_evidence_ciphertext BLOB CHECK (length(pending_evidence_ciphertext) BETWEEN 1 AND 66000),
    pending_evidence_key_id TEXT CHECK (pending_evidence_key_id IS NULL OR (length(pending_evidence_key_id) BETWEEN 1 AND 160 AND trim(pending_evidence_key_id)<>'')),
    evidence_digest BLOB CHECK (length(evidence_digest)=32),
    legacy_kind TEXT CHECK (legacy_kind IS NULL OR (length(legacy_kind) BETWEEN 1 AND 160 AND trim(legacy_kind)<>'')),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK (((order_id IS NOT NULL) + (topup_user_id IS NOT NULL))=1),
    CHECK (((bank_destination_ciphertext IS NOT NULL) + (bank_destination_key_id IS NOT NULL)) IN (0,2)),
    CHECK (((pending_evidence_ciphertext IS NOT NULL) + (pending_evidence_key_id IS NOT NULL)) IN (0,2)),
    CHECK ((evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL) = (evidence_digest IS NOT NULL)),
    CHECK (pending_evidence_ciphertext IS NULL OR (evidence_asset_ref IS NULL AND order_id IS NOT NULL AND ipn_key_id IS NOT NULL AND first_payment_observed_at IS NOT NULL AND status IN ('confirming','review'))),
    CHECK (((review_admin_id IS NOT NULL) + (reviewed_at IS NOT NULL) + (review_reason IS NOT NULL)) IN (0,3)),
    CHECK (provider_payment_ref IS NULL OR provider_code IS NOT NULL),
    CHECK (hosted_invoice_started_at IS NULL OR akanti_utc_time(hosted_invoice_started_at)),
    CHECK (((hosted_invoice_ref IS NOT NULL) + (hosted_invoice_url IS NOT NULL)) IN (0,2)),
    CHECK (hosted_invoice_started_at IS NULL OR (payment_method='crypto' AND provider_code IS NOT DISTINCT FROM 'nowpayments')),
    CHECK (hosted_invoice_ref IS NULL OR hosted_invoice_started_at IS NOT NULL),
    CHECK ((payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination_ciphertext IS NULL) OR
           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND (bank_destination_ciphertext IS NOT NULL OR coalesce(legacy_kind='manual_deposit',false)) AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)),
    CHECK (topup_user_id IS NULL OR payment_method='crypto'),
    CHECK (status <> 'settled' OR confirmed_at IS NOT NULL),
    CHECK (((ipn_key_id IS NOT NULL) + (ipn_signature IS NOT NULL) + (ipn_payload_sha256 IS NOT NULL) + (ipn_verified_at IS NOT NULL)) IN (0,4)),
    CHECK (ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND (evidence_asset_ref IS NOT NULL OR pending_evidence_ciphertext IS NOT NULL))),
    CHECK (expected_network_amount IS NULL OR payment_method='crypto'),
    CHECK (ipn_verified_at IS NULL OR akanti_utc_time(ipn_verified_at)),
    CHECK (first_payment_observed_at IS NULL OR akanti_utc_time(first_payment_observed_at)),
    CHECK (confirmed_at IS NULL OR akanti_utc_time(confirmed_at)),
    CHECK (reviewed_at IS NULL OR akanti_utc_time(reviewed_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT payments_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT payments_topup_user_id_fk FOREIGN KEY (topup_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT payments_review_admin_id_fk FOREIGN KEY (review_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT payments_compensating_wallet_entry_id_fk FOREIGN KEY (compensating_wallet_entry_id) REFERENCES wallet_entries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE fulfillments (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)) NOT NULL,
    unit_no INTEGER NOT NULL CHECK (unit_no > 0),
    kind TEXT CHECK (kind IN ('local','supplier','manual')),
    status TEXT NOT NULL DEFAULT 'unfilled' CHECK (status IN ('unfilled','acquired','ready','released','retained_admin')),
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)),
    remote_item_ref TEXT CHECK (remote_item_ref IS NULL OR (length(remote_item_ref) BETWEEN 1 AND 160 AND trim(remote_item_ref)<>'')),
    supplier_item_key BLOB UNIQUE CHECK (length(supplier_item_key)=32),
    delivered_content BLOB,
    delivered_content_key_id TEXT CHECK (delivered_content_key_id IS NULL OR (length(delivered_content_key_id) BETWEEN 1 AND 160 AND trim(delivered_content_key_id)<>'')),
    content_kind TEXT CHECK (content_kind IN ('account','key','code','text','file')),
    resolution_admin_id TEXT COLLATE NOCASE CHECK (resolution_admin_id IS NULL OR akanti_uuid(resolution_admin_id)),
    resolution_note TEXT CHECK (resolution_note IS NULL OR (length(resolution_note)<=16384)),
    manifest_generation TEXT COLLATE NOCASE CHECK (manifest_generation IS NULL OR akanti_uuid(manifest_generation)),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    completed_at TEXT,
    UNIQUE (order_id,unit_no),
    CHECK (kind IS NOT NULL OR status='unfilled'),
    CHECK ((kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)),
    CHECK (((delivered_content IS NOT NULL) + (delivered_content_key_id IS NOT NULL) + (content_kind IS NOT NULL)) IN (0,3)),
    CHECK (status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL),
    CHECK (status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)),
    CHECK (status <> 'retained_admin' OR resolution_admin_id IS NOT NULL),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (completed_at IS NULL OR akanti_utc_time(completed_at)),
    CONSTRAINT fulfillments_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT fulfillments_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT fulfillments_resolution_admin_id_fk FOREIGN KEY (resolution_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT fulfillments_order_id_manifest_generation_fk FOREIGN KEY (order_id,manifest_generation) REFERENCES orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE supplier_requests (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)) NOT NULL,
    requested_quantity INTEGER NOT NULL CHECK (requested_quantity > 0),
    product_supplier_source_id TEXT COLLATE NOCASE CHECK (product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)),
    source_revision INTEGER CHECK (source_revision > 0),
    provider_code TEXT CHECK (provider_code IS NULL OR (length(provider_code) BETWEEN 1 AND 160 AND trim(provider_code)<>'')),
    account_ref TEXT CHECK (account_ref IS NULL OR (length(account_ref) BETWEEN 1 AND 160 AND trim(account_ref)<>'')),
    external_product_id TEXT CHECK (external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')),
    external_variant_id TEXT CHECK (external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')),
    local_intent_key TEXT CHECK (local_intent_key IS NULL OR (length(local_intent_key) BETWEEN 1 AND 160 AND trim(local_intent_key)<>'')) NOT NULL UNIQUE,
    remote_ref TEXT CHECK (remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')),
    request_sha256 BLOB NOT NULL CHECK (length(request_sha256)=32),
    status TEXT NOT NULL DEFAULT 'blocked' CHECK (status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    review_code TEXT CHECK (review_code IS NULL OR (review_code IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'))),
    next_check_at TEXT,
    evidence_ref TEXT CHECK (evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)),
    cost_baseline_amount TEXT CHECK (cost_baseline_amount IS NULL OR (akanti_decimal(cost_baseline_amount)=1)) CHECK (akanti_decimal_compare(cost_baseline_amount,'0') > 0),
    cost_currency TEXT CHECK (cost_currency IS NULL OR (length(cost_currency) BETWEEN 1 AND 160 AND trim(cost_currency)<>'')),
    cost_quote_ref TEXT CHECK (cost_quote_ref IS NULL OR (length(cost_quote_ref) BETWEEN 1 AND 160 AND trim(cost_quote_ref)<>'')),
    cost_observed_at TEXT,
    approved_cost_ceiling TEXT CHECK (approved_cost_ceiling IS NULL OR (akanti_decimal(approved_cost_ceiling)=1)) CHECK (akanti_decimal_compare(approved_cost_ceiling,'0') > 0),
    approval_audit_id TEXT COLLATE NOCASE CHECK (approval_audit_id IS NULL OR akanti_uuid(approval_audit_id)),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK (((cost_baseline_amount IS NOT NULL) + (cost_currency IS NOT NULL) + (cost_quote_ref IS NOT NULL) + (cost_observed_at IS NOT NULL)) IN (0,4)),
    CHECK (((approved_cost_ceiling IS NOT NULL) + (approval_audit_id IS NOT NULL)) IN (0,2)),
    CHECK (status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))),
    CHECK (status <> 'blocked' OR review_code IS NOT NULL),
    CHECK (next_check_at IS NULL OR akanti_utc_time(next_check_at)),
    CHECK (cost_observed_at IS NULL OR akanti_utc_time(cost_observed_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT supplier_requests_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT supplier_requests_product_supplier_source_id_fk FOREIGN KEY (product_supplier_source_id) REFERENCES product_supplier_sources (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT supplier_requests_approval_audit_id_fk FOREIGN KEY (approval_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE jobs (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)),
    job_kind TEXT NOT NULL CHECK (job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')),
    payment_id TEXT COLLATE NOCASE CHECK (payment_id IS NULL OR akanti_uuid(payment_id)),
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    fulfillment_id TEXT COLLATE NOCASE CHECK (fulfillment_id IS NULL OR akanti_uuid(fulfillment_id)),
    campaign_id TEXT COLLATE NOCASE CHECK (campaign_id IS NULL OR akanti_uuid(campaign_id)),
    discount_id TEXT COLLATE NOCASE CHECK (discount_id IS NULL OR akanti_uuid(discount_id)),
    support_message_id TEXT COLLATE NOCASE CHECK (support_message_id IS NULL OR akanti_uuid(support_message_id)),
    telegram_delivery_id TEXT COLLATE NOCASE CHECK (telegram_delivery_id IS NULL OR akanti_uuid(telegram_delivery_id)),
    product_stock_subscription_id TEXT COLLATE NOCASE CHECK (product_stock_subscription_id IS NULL OR akanti_uuid(product_stock_subscription_id)),
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)),
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)),
    product_supplier_source_id TEXT COLLATE NOCASE CHECK (product_supplier_source_id IS NULL OR akanti_uuid(product_supplier_source_id)),
    audit_event_id TEXT COLLATE NOCASE CHECK (audit_event_id IS NULL OR akanti_uuid(audit_event_id)),
    origin_revision INTEGER CHECK (origin_revision >= 0),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','leased','succeeded','retry','failed','cancelled')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    available_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    locked_by TEXT CHECK (locked_by IS NULL OR (length(locked_by) BETWEEN 1 AND 160 AND trim(locked_by)<>'')),
    lease_until TEXT,
    dedupe_key TEXT CHECK (dedupe_key IS NULL OR (length(dedupe_key) BETWEEN 1 AND 160 AND trim(dedupe_key)<>'')) NOT NULL UNIQUE,
    last_error_code TEXT CHECK (last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 160 AND trim(last_error_code)<>'')),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    completed_at TEXT,
    CHECK ((status='leased')=(locked_by IS NOT NULL)),
    CHECK ((locked_by IS NULL)=(lease_until IS NULL)),
    CHECK (status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL),
    CHECK (available_at IS NULL OR akanti_utc_time(available_at)),
    CHECK (lease_until IS NULL OR akanti_utc_time(lease_until)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (completed_at IS NULL OR akanti_utc_time(completed_at)),
    CONSTRAINT jobs_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_payment_id_fk FOREIGN KEY (payment_id) REFERENCES payments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_fulfillment_id_fk FOREIGN KEY (fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_campaign_id_fk FOREIGN KEY (campaign_id) REFERENCES campaigns (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_support_message_id_fk FOREIGN KEY (support_message_id) REFERENCES support_messages (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_telegram_delivery_id_fk FOREIGN KEY (telegram_delivery_id) REFERENCES telegram_deliveries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_product_stock_subscription_id_fk FOREIGN KEY (product_stock_subscription_id) REFERENCES product_stock_subscriptions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_product_supplier_source_id_fk FOREIGN KEY (product_supplier_source_id) REFERENCES product_supplier_sources (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT jobs_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE campaigns (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    batch_id TEXT COLLATE NOCASE CHECK (batch_id IS NULL OR akanti_uuid(batch_id)) NOT NULL,
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    language TEXT CHECK (language IS NULL OR (length(language) BETWEEN 1 AND 160 AND trim(language)<>'')) NOT NULL,
    audience_kind TEXT NOT NULL CHECK (audience_kind IN ('all','active_since','language')),
    active_since TEXT,
    audience_language TEXT CHECK (audience_language IS NULL OR (length(audience_language) BETWEEN 1 AND 160 AND trim(audience_language)<>'')),
    message_text TEXT CHECK (message_text IS NULL OR (length(message_text)<=16384)),
    media_ref TEXT CHECK (media_ref IS NULL OR (length(media_ref) BETWEEN 1 AND 1024)),
    status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','approved','running','completed','cancelled')),
    created_by_admin_id TEXT COLLATE NOCASE CHECK (created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)) NOT NULL,
    approved_at TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    started_at TEXT,
    completed_at TEXT,
    UNIQUE (batch_id,brand_id,language),
    UNIQUE (id,batch_id,brand_id),
    CHECK (((message_text IS NOT NULL) + (media_ref IS NOT NULL))>0),
    CHECK ((audience_kind='active_since')=(active_since IS NOT NULL)),
    CHECK ((audience_kind='language')=(audience_language IS NOT NULL)),
    CHECK (status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL),
    CHECK (active_since IS NULL OR akanti_utc_time(active_since)),
    CHECK (approved_at IS NULL OR akanti_utc_time(approved_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (started_at IS NULL OR akanti_utc_time(started_at)),
    CHECK (completed_at IS NULL OR akanti_utc_time(completed_at)),
    CONSTRAINT campaigns_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT campaigns_created_by_admin_id_fk FOREIGN KEY (created_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE telegram_deliveries (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    recipient_kind TEXT NOT NULL CHECK (recipient_kind IN ('customer_dm','staff_chat')),
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)),
    staff_chat_id INTEGER CHECK (staff_chat_id < 0),
    purpose TEXT NOT NULL CHECK (purpose IN ('order_credentials','order_status','campaign','discount_notice','support_notice','restock_notice','staff_supplier_case','staff_supplier_bonus')),
    action TEXT NOT NULL CHECK (action IN ('send','edit')),
    target_message_id INTEGER CHECK (target_message_id > 0),
    part_no INTEGER NOT NULL DEFAULT 1 CHECK (part_no > 0),
    delivery_generation TEXT COLLATE NOCASE CHECK (delivery_generation IS NULL OR akanti_uuid(delivery_generation)),
    recipient_language TEXT CHECK (recipient_language IS NULL OR (length(recipient_language) BETWEEN 1 AND 160 AND trim(recipient_language)<>'')),
    eligibility_checked_at TEXT,
    dedupe_key TEXT CHECK (dedupe_key IS NULL OR (length(dedupe_key) BETWEEN 1 AND 160 AND trim(dedupe_key)<>'')) NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','leased','succeeded','retry','failed','cancelled','uncertain')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    last_attempt_at TEXT,
    telegram_message_id INTEGER CHECK (telegram_message_id > 0),
    last_error_code TEXT CHECK (last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 160 AND trim(last_error_code)<>'')),
    sent_at TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    campaign_id TEXT COLLATE NOCASE CHECK (campaign_id IS NULL OR akanti_uuid(campaign_id)),
    campaign_batch_id TEXT COLLATE NOCASE CHECK (campaign_batch_id IS NULL OR akanti_uuid(campaign_batch_id)),
    discount_id TEXT COLLATE NOCASE CHECK (discount_id IS NULL OR akanti_uuid(discount_id)),
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    fulfillment_id TEXT COLLATE NOCASE CHECK (fulfillment_id IS NULL OR akanti_uuid(fulfillment_id)),
    support_message_id TEXT COLLATE NOCASE CHECK (support_message_id IS NULL OR akanti_uuid(support_message_id)),
    product_stock_subscription_id TEXT COLLATE NOCASE CHECK (product_stock_subscription_id IS NULL OR akanti_uuid(product_stock_subscription_id)),
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)),
    audit_event_id TEXT COLLATE NOCASE CHECK (audit_event_id IS NULL OR akanti_uuid(audit_event_id)),
    CHECK ((recipient_kind='customer_dm' AND user_id IS NOT NULL AND staff_chat_id IS NULL AND purpose NOT IN ('staff_supplier_case','staff_supplier_bonus')) OR
           (recipient_kind='staff_chat' AND user_id IS NULL AND staff_chat_id IS NOT NULL AND purpose IN ('staff_supplier_case','staff_supplier_bonus'))),
    CHECK (action <> 'edit' OR target_message_id IS NOT NULL),
    CHECK (status <> 'succeeded' OR sent_at IS NOT NULL),
    CHECK (((campaign_id IS NOT NULL) + (campaign_batch_id IS NOT NULL)) IN (0,2)),
    CHECK (eligibility_checked_at IS NULL OR akanti_utc_time(eligibility_checked_at)),
    CHECK (next_attempt_at IS NULL OR akanti_utc_time(next_attempt_at)),
    CHECK (last_attempt_at IS NULL OR akanti_utc_time(last_attempt_at)),
    CHECK (sent_at IS NULL OR akanti_utc_time(sent_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT telegram_deliveries_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_fulfillment_id_fk FOREIGN KEY (fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_support_message_id_fk FOREIGN KEY (support_message_id) REFERENCES support_messages (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_product_stock_subscription_id_fk FOREIGN KEY (product_stock_subscription_id) REFERENCES product_stock_subscriptions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_campaign_id_campaign_batch_id_brand_id_fk FOREIGN KEY (campaign_id,campaign_batch_id,brand_id) REFERENCES campaigns (id,batch_id,brand_id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT telegram_deliveries_order_id_delivery_generation_fk FOREIGN KEY (order_id,delivery_generation) REFERENCES orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE support_tickets (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) NOT NULL,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    category TEXT NOT NULL CHECK (category IN ('general','order','payment','preorder_cancellation')),
    status TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','assigned','waiting_customer','closed')),
    source TEXT NOT NULL CHECK (source IN ('customer','admin','system')),
    assigned_admin_id TEXT COLLATE NOCASE CHECK (assigned_admin_id IS NULL OR akanti_uuid(assigned_admin_id)),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    closed_at TEXT,
    CHECK ((status='closed')=(closed_at IS NOT NULL)),
    CHECK (category <> 'preorder_cancellation' OR order_id IS NOT NULL),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CHECK (closed_at IS NULL OR akanti_utc_time(closed_at)),
    CONSTRAINT support_tickets_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT support_tickets_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT support_tickets_assigned_admin_id_fk FOREIGN KEY (assigned_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE support_messages (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    ticket_id TEXT COLLATE NOCASE CHECK (ticket_id IS NULL OR akanti_uuid(ticket_id)) NOT NULL,
    sender_kind TEXT NOT NULL CHECK (sender_kind IN ('customer','admin','system')),
    sender_admin_id TEXT COLLATE NOCASE CHECK (sender_admin_id IS NULL OR akanti_uuid(sender_admin_id)),
    sender_telegram_id INTEGER CHECK (sender_telegram_id > 0),
    body_ciphertext BLOB,
    body_key_id TEXT CHECK (body_key_id IS NULL OR (length(body_key_id) BETWEEN 1 AND 160 AND trim(body_key_id)<>'')),
    asset_ref TEXT CHECK (asset_ref IS NULL OR (length(asset_ref) BETWEEN 1 AND 1024)),
    telegram_message_id INTEGER CHECK (telegram_message_id > 0),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK ((sender_kind='admin')=(sender_admin_id IS NOT NULL)),
    CHECK (((body_ciphertext IS NOT NULL) + (body_key_id IS NOT NULL)) IN (0,2)),
    CHECK (((body_ciphertext IS NOT NULL) + (asset_ref IS NOT NULL))>0),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT support_messages_ticket_id_fk FOREIGN KEY (ticket_id) REFERENCES support_tickets (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT support_messages_sender_admin_id_fk FOREIGN KEY (sender_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE legacy_id_map (
    source_brand TEXT CHECK (source_brand IS NULL OR (length(source_brand) BETWEEN 1 AND 160 AND trim(source_brand)<>'')) NOT NULL,
    source_table TEXT CHECK (source_table IS NULL OR (length(source_table) BETWEEN 1 AND 160 AND trim(source_table)<>'')) NOT NULL,
    source_key_kind TEXT NOT NULL CHECK (source_key_kind IN ('primary','public_order_reference')),
    legacy_id TEXT CHECK (legacy_id IS NULL OR (length(legacy_id) BETWEEN 1 AND 160 AND trim(legacy_id)<>'')) NOT NULL,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')),
    target_id TEXT COLLATE NOCASE CHECK (target_id IS NULL OR akanti_uuid(target_id)) NOT NULL,
    target_brand_id TEXT COLLATE NOCASE CHECK (target_brand_id IS NULL OR akanti_uuid(target_brand_id)),
    source_fingerprint BLOB NOT NULL CHECK (length(source_fingerprint)=32),
    import_batch TEXT COLLATE NOCASE CHECK (import_batch IS NULL OR akanti_uuid(import_batch)) NOT NULL,
    import_manifest_ref TEXT CHECK (import_manifest_ref IS NULL OR (length(import_manifest_ref) BETWEEN 1 AND 1024)) NOT NULL,
    migrated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    PRIMARY KEY (source_brand,source_table,source_key_kind,legacy_id),
    CHECK (migrated_at IS NULL OR akanti_utc_time(migrated_at)),
    CONSTRAINT legacy_id_map_target_brand_id_fk FOREIGN KEY (target_brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE audit_events (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    actor_kind TEXT NOT NULL CHECK (actor_kind IN ('admin','customer','system')),
    actor_admin_id TEXT COLLATE NOCASE CHECK (actor_admin_id IS NULL OR akanti_uuid(actor_admin_id)),
    actor_user_id TEXT COLLATE NOCASE CHECK (actor_user_id IS NULL OR akanti_uuid(actor_user_id)),
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)),
    action TEXT NOT NULL CHECK (action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')),
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')),
    entity_id TEXT COLLATE NOCASE CHECK (entity_id IS NULL OR akanti_uuid(entity_id)),
    reason TEXT CHECK (reason IS NULL OR (length(reason)<=16384)) NOT NULL,
    previous_revision INTEGER CHECK (previous_revision >= 0),
    new_revision INTEGER CHECK (new_revision >= 0),
    correlation_id TEXT COLLATE NOCASE CHECK (correlation_id IS NULL OR akanti_uuid(correlation_id)) NOT NULL,
    command_sha256 BLOB CHECK (length(command_sha256)=32),
    before_channel_enforcement INTEGER CHECK (before_channel_enforcement IS NULL OR (before_channel_enforcement IN (0,1))),
    after_channel_enforcement INTEGER CHECK (after_channel_enforcement IS NULL OR (after_channel_enforcement IN (0,1))),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    CHECK ((actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR
           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR
           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)),
    CHECK ((entity_kind='platform')=(entity_id IS NULL)),
    CHECK ((action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR
           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT audit_events_actor_admin_id_fk FOREIGN KEY (actor_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT audit_events_actor_user_id_fk FOREIGN KEY (actor_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT audit_events_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE discounts (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    brand_id TEXT COLLATE NOCASE CHECK (brand_id IS NULL OR akanti_uuid(brand_id)) NOT NULL,
    code_digest BLOB NOT NULL CHECK (length(code_digest)=32),
    code_ciphertext BLOB NOT NULL CHECK (length(code_ciphertext)>0),
    code_key_id TEXT CHECK (code_key_id IS NULL OR (length(code_key_id) BETWEEN 1 AND 160 AND trim(code_key_id)<>'')) NOT NULL,
    display_prefix TEXT CHECK (display_prefix IS NULL OR (length(display_prefix) BETWEEN 1 AND 160 AND trim(display_prefix)<>'')) NOT NULL,
    issuance_batch_id TEXT COLLATE NOCASE CHECK (issuance_batch_id IS NULL OR akanti_uuid(issuance_batch_id)) NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('prepaid_entitlement','goodwill','promotion')),
    percent_off TEXT CHECK (percent_off IS NULL OR (akanti_decimal(percent_off)=1)) NOT NULL CHECK ((akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)),
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)),
    eligible_user_id TEXT COLLATE NOCASE CHECK (eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)),
    source_order_id TEXT COLLATE NOCASE CHECK (source_order_id IS NULL OR akanti_uuid(source_order_id)),
    max_total_uses INTEGER CHECK (max_total_uses >= 0),
    max_uses_per_user INTEGER CHECK (max_uses_per_user >= 0),
    redeemed_count INTEGER NOT NULL DEFAULT 0 CHECK (redeemed_count >= 0),
    usage_adjustment_units INTEGER NOT NULL DEFAULT 0,
    starts_at TEXT,
    ends_at TEXT,
    active INTEGER CHECK (active IS NULL OR (active IN (0,1))) NOT NULL DEFAULT true,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    created_by_admin_id TEXT COLLATE NOCASE CHECK (created_by_admin_id IS NULL OR akanti_uuid(created_by_admin_id)),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    UNIQUE (brand_id,code_digest),
    CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CHECK ((kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR
           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)),
    CHECK (starts_at IS NULL OR akanti_utc_time(starts_at)),
    CHECK (ends_at IS NULL OR akanti_utc_time(ends_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CONSTRAINT discounts_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discounts_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discounts_eligible_user_id_fk FOREIGN KEY (eligible_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discounts_source_order_id_fk FOREIGN KEY (source_order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discounts_created_by_admin_id_fk FOREIGN KEY (created_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE discount_uses (
    discount_id TEXT COLLATE NOCASE CHECK (discount_id IS NULL OR akanti_uuid(discount_id)) NOT NULL,
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)) NOT NULL UNIQUE,
    discount_revision_id TEXT COLLATE NOCASE CHECK (discount_revision_id IS NULL OR akanti_uuid(discount_revision_id)) NOT NULL,
    units_covered INTEGER NOT NULL CHECK (units_covered > 0),
    saved_amount TEXT CHECK (saved_amount IS NULL OR (akanti_decimal(saved_amount)=1)) NOT NULL CHECK (akanti_decimal_compare(saved_amount,'0') >= 0),
    saved_currency TEXT NOT NULL CHECK (saved_currency IN ('USD','TOMAN')),
    status TEXT NOT NULL CHECK (status IN ('held','redeemed','released')),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    transitioned_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    PRIMARY KEY (discount_id,order_id),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (transitioned_at IS NULL OR akanti_utc_time(transitioned_at)),
    CONSTRAINT discount_uses_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discount_uses_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discount_uses_discount_revision_id_fk FOREIGN KEY (discount_revision_id) REFERENCES discount_revisions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE discount_revisions (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    discount_id TEXT COLLATE NOCASE CHECK (discount_id IS NULL OR akanti_uuid(discount_id)) NOT NULL,
    revision_no INTEGER NOT NULL CHECK (revision_no >= 0),
    audit_event_id TEXT COLLATE NOCASE CHECK (audit_event_id IS NULL OR akanti_uuid(audit_event_id)) NOT NULL UNIQUE,
    percent_off TEXT CHECK (percent_off IS NULL OR (akanti_decimal(percent_off)=1)) NOT NULL CHECK ((akanti_decimal_compare(percent_off,'0') >= 0 AND akanti_decimal_compare(percent_off,'100') <= 0)),
    max_total_uses INTEGER CHECK (max_total_uses >= 0),
    max_uses_per_user INTEGER CHECK (max_uses_per_user >= 0),
    usage_adjustment_units INTEGER NOT NULL,
    redeemed_count_at_revision INTEGER NOT NULL CHECK (redeemed_count_at_revision >= 0),
    held_units_at_revision INTEGER NOT NULL CHECK (held_units_at_revision >= 0),
    starts_at TEXT,
    ends_at TEXT,
    active INTEGER CHECK (active IS NULL OR (active IN (0,1))) NOT NULL,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)),
    eligible_user_id TEXT COLLATE NOCASE CHECK (eligible_user_id IS NULL OR akanti_uuid(eligible_user_id)),
    UNIQUE (discount_id,revision_no),
    CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CHECK (starts_at IS NULL OR akanti_utc_time(starts_at)),
    CHECK (ends_at IS NULL OR akanti_utc_time(ends_at)),
    CONSTRAINT discount_revisions_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discount_revisions_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discount_revisions_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT discount_revisions_eligible_user_id_fk FOREIGN KEY (eligible_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE bot_activity_events (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) NOT NULL,
    event_type TEXT NOT NULL CHECK (event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow','payment')),
    source_kind TEXT NOT NULL CHECK (source_kind IN ('command','callback','flow','system')),
    action_code TEXT NOT NULL CHECK (action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected','topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled','flow_abandoned')),
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)),
    order_id TEXT COLLATE NOCASE CHECK (order_id IS NULL OR akanti_uuid(order_id)),
    support_ticket_id TEXT COLLATE NOCASE CHECK (support_ticket_id IS NULL OR akanti_uuid(support_ticket_id)),
    quantity_selected INTEGER CHECK (quantity_selected > 0),
    flow_revision INTEGER CHECK (flow_revision >= 0),
    telegram_update_id INTEGER CHECK (telegram_update_id >= 0),
    outcome_code TEXT NOT NULL CHECK (outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')),
    idempotency_key TEXT CHECK (idempotency_key IS NULL OR (length(idempotency_key) BETWEEN 1 AND 160 AND trim(idempotency_key)<>'')) NOT NULL,
    occurred_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    UNIQUE (user_id,idempotency_key),
    CHECK ((event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR
           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR
           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected')) OR
           (event_type='support' AND action_code IN ('support_open','support_reply')) OR
           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR
           (event_type='discount' AND action_code='discounts_open') OR
           (event_type='policy' AND action_code='policy_check') OR
           (event_type='flow' AND action_code IN ('flow_expired','flow_abandoned')) OR
           (event_type='payment' AND action_code IN ('topup_created','payment_awaiting','payment_observed','payment_review','payment_denied','payment_evidence_ready','payment_settled'))),
    CHECK (action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR product_id IS NOT NULL),
    CHECK (action_code NOT IN ('quantity_enter','checkout_created','checkout_funded','checkout_expired','checkout_replaced','checkout_rejected') OR quantity_selected IS NOT NULL),
    CHECK (action_code <> 'support_reply' OR support_ticket_id IS NOT NULL),
    CHECK (action_code NOT IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') OR order_id IS NOT NULL),
    CHECK (action_code NOT IN ('flow_expired','flow_abandoned','quantity_rejected') OR flow_revision IS NOT NULL),
    CHECK (action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)),
    CHECK (action_code <> 'checkout_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND order_id IS NULL AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL)),
    CHECK (action_code NOT IN ('checkout_funded','checkout_expired','checkout_replaced') OR (source_kind='system' AND
        outcome_code=CASE WHEN action_code='checkout_funded' THEN 'accepted' ELSE 'cancelled' END)),
    CHECK (event_type<>'payment' OR (source_kind='system' AND support_ticket_id IS NULL AND flow_revision IS NULL AND telegram_update_id IS NULL
        AND outcome_code=CASE action_code WHEN 'payment_denied' THEN 'rejected' WHEN 'payment_review' THEN 'blocked' ELSE 'accepted' END)),
    CHECK (occurred_at IS NULL OR akanti_utc_time(occurred_at)),
    CONSTRAINT bot_activity_events_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT bot_activity_events_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT bot_activity_events_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT bot_activity_events_support_ticket_id_fk FOREIGN KEY (support_ticket_id) REFERENCES support_tickets (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE supplier_request_attempts (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    supplier_request_id TEXT COLLATE NOCASE CHECK (supplier_request_id IS NULL OR akanti_uuid(supplier_request_id)) NOT NULL,
    attempt_no INTEGER NOT NULL CHECK (attempt_no > 0),
    operation TEXT NOT NULL CHECK (operation IN ('submit','status_lookup','export')),
    outcome TEXT NOT NULL DEFAULT 'in_flight' CHECK (outcome IN ('in_flight','succeeded','failed','timeout','unknown')),
    parent_submit_attempt_id TEXT COLLATE NOCASE CHECK (parent_submit_attempt_id IS NULL OR akanti_uuid(parent_submit_attempt_id)),
    admin_authorization_audit_id TEXT COLLATE NOCASE CHECK (admin_authorization_audit_id IS NULL OR akanti_uuid(admin_authorization_audit_id)),
    purchase_idempotency_key TEXT CHECK (purchase_idempotency_key IS NULL OR (length(purchase_idempotency_key) BETWEEN 1 AND 160 AND trim(purchase_idempotency_key)<>'')),
    max_charge_amount TEXT CHECK (max_charge_amount IS NULL OR (akanti_decimal(max_charge_amount)=1)) CHECK (akanti_decimal_compare(max_charge_amount,'0') > 0),
    http_status INTEGER CHECK (http_status BETWEEN 100 AND 599),
    error_code TEXT CHECK (error_code IS NULL OR (length(error_code) BETWEEN 1 AND 160 AND trim(error_code)<>'')),
    remote_ref TEXT CHECK (remote_ref IS NULL OR (length(remote_ref) BETWEEN 1 AND 160 AND trim(remote_ref)<>'')),
    evidence_ref TEXT CHECK (evidence_ref IS NULL OR (length(evidence_ref) BETWEEN 1 AND 1024)),
    started_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    finished_at TEXT,
    UNIQUE (supplier_request_id,attempt_no),
    CHECK ((outcome='in_flight')=(finished_at IS NULL)),
    CHECK (finished_at IS NULL OR finished_at >= started_at),
    CHECK ((operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR
           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)),
    CHECK (started_at IS NULL OR akanti_utc_time(started_at)),
    CHECK (finished_at IS NULL OR akanti_utc_time(finished_at)),
    CONSTRAINT supplier_request_attempts_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT supplier_request_attempts_parent_submit_attempt_id_fk FOREIGN KEY (parent_submit_attempt_id) REFERENCES supplier_request_attempts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT supplier_request_attempts_admin_authorization_audit_id_fk FOREIGN KEY (admin_authorization_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE product_supplier_sources (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)) NOT NULL,
    provider_code TEXT NOT NULL CHECK (provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')),
    external_product_id TEXT CHECK (external_product_id IS NULL OR (length(external_product_id) BETWEEN 1 AND 160 AND trim(external_product_id)<>'')) NOT NULL,
    external_variant_id TEXT CHECK (external_variant_id IS NULL OR (length(external_variant_id) BETWEEN 1 AND 160 AND trim(external_variant_id)<>'')),
    enabled INTEGER CHECK (enabled IS NULL OR (enabled IN (0,1))) NOT NULL DEFAULT false,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    observation_revision INTEGER NOT NULL DEFAULT 0 CHECK (observation_revision >= 0),
    observed_source_revision INTEGER CHECK (observed_source_revision > 0),
    observed_account_ref TEXT CHECK (observed_account_ref IS NULL OR (length(observed_account_ref) BETWEEN 1 AND 160 AND trim(observed_account_ref)<>'')),
    observed_at TEXT,
    availability_code TEXT NOT NULL DEFAULT 'unknown' CHECK (availability_code IN ('unknown','available','out_of_stock','ineligible','error')),
    available_quantity INTEGER CHECK (available_quantity >= 0),
    observed_unit_cost TEXT CHECK (observed_unit_cost IS NULL OR (akanti_decimal(observed_unit_cost)=1)) CHECK (akanti_decimal_compare(observed_unit_cost,'0') > 0),
    observed_currency TEXT CHECK (observed_currency IS NULL OR (length(observed_currency) BETWEEN 1 AND 160 AND trim(observed_currency)<>'')),
    eligibility_code TEXT NOT NULL DEFAULT 'unknown' CHECK (eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')),
    quote_ref TEXT CHECK (quote_ref IS NULL OR (length(quote_ref) BETWEEN 1 AND 160 AND trim(quote_ref)<>'')),
    observation_error_code TEXT CHECK (observation_error_code IS NULL OR (length(observation_error_code) BETWEEN 1 AND 160 AND trim(observation_error_code)<>'')),
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    UNIQUE (product_id,id),
    CHECK (((observed_unit_cost IS NOT NULL) + (observed_currency IS NOT NULL)) IN (0,2)),
    CHECK (availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)),
    CHECK (availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')),
    CHECK (availability_code <> 'out_of_stock' OR available_quantity=0),
    CHECK (observed_at IS NULL OR akanti_utc_time(observed_at)),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (updated_at IS NULL OR akanti_utc_time(updated_at)),
    CONSTRAINT product_supplier_sources_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

CREATE TABLE product_stock_subscriptions (
    id TEXT COLLATE NOCASE CHECK (id IS NULL OR akanti_uuid(id)) PRIMARY KEY,
    user_id TEXT COLLATE NOCASE CHECK (user_id IS NULL OR akanti_uuid(user_id)) NOT NULL,
    product_id TEXT COLLATE NOCASE CHECK (product_id IS NULL OR akanti_uuid(product_id)) NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'),
    triggered_at TEXT,
    cancelled_at TEXT,
    closed_at TEXT,
    CHECK (triggered_at IS NULL OR triggered_at >= created_at),
    CHECK (cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)),
    CHECK (closed_at IS NULL OR closed_at >= created_at),
    CHECK (created_at IS NULL OR akanti_utc_time(created_at)),
    CHECK (triggered_at IS NULL OR akanti_utc_time(triggered_at)),
    CHECK (cancelled_at IS NULL OR akanti_utc_time(cancelled_at)),
    CHECK (closed_at IS NULL OR akanti_utc_time(closed_at)),
    CONSTRAINT product_stock_subscriptions_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE,
    CONSTRAINT product_stock_subscriptions_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE
) STRICT;

-- Attach only validated constraints after every referenced table exists.

CREATE UNIQUE INDEX brands_distinct_iran_card ON brands (bank_card_fingerprint) WHERE market='iran' AND allow_bank;
CREATE INDEX inventory_available_product ON inventory_items (product_id,created_at,id) WHERE state='available';
CREATE UNIQUE INDEX wallet_posting_version ON wallet_entries (user_id,posting_version) WHERE is_posting;
CREATE UNIQUE INDEX wallet_opening_once ON wallet_entries (user_id) WHERE movement_kind='opening_balance' AND is_posting;
CREATE UNIQUE INDEX wallet_supplier_resolution_once ON wallet_entries (order_id) WHERE movement_kind='supplier_wallet_credit';
CREATE UNIQUE INDEX wallet_payment_credit_once ON wallet_entries (payment_id) WHERE movement_kind IN ('wallet_topup','immediate_crypto_shortage_credit','late_crypto_credit');
CREATE UNIQUE INDEX orders_one_awaiting ON orders (user_id) WHERE payment_status='awaiting_payment';
CREATE INDEX orders_paid_preorder_fifo ON orders (product_id,payment_confirmed_at,id) WHERE is_preorder AND payment_status IN ('paid','not_required') AND fulfillment_status IN ('waiting_stock','requires_admin');
CREATE UNIQUE INDEX payments_provider_identity ON payments (provider_code,provider_payment_ref) WHERE provider_payment_ref IS NOT NULL;
CREATE UNIQUE INDEX supplier_order_unresolved ON supplier_requests (order_id) WHERE status NOT IN ('failed','resolved','succeeded');
CREATE INDEX jobs_claimable ON jobs (available_at,id) WHERE status IN ('pending','retry');
CREATE UNIQUE INDEX delivery_campaign_recipient_once ON telegram_deliveries (campaign_batch_id,user_id) WHERE campaign_id IS NOT NULL;
CREATE UNIQUE INDEX delivery_manifest_part_once ON telegram_deliveries (order_id,delivery_generation,part_no,purpose) WHERE purpose='order_credentials';
CREATE UNIQUE INDEX delivery_restock_once ON telegram_deliveries (product_stock_subscription_id) WHERE product_stock_subscription_id IS NOT NULL;
CREATE UNIQUE INDEX delivery_staff_case_once ON telegram_deliveries (supplier_request_id,audit_event_id,delivery_generation,purpose) WHERE recipient_kind='staff_chat';
CREATE UNIQUE INDEX support_external_message_once ON support_messages (ticket_id,telegram_message_id) WHERE telegram_message_id IS NOT NULL;
CREATE UNIQUE INDEX discount_prepaid_source_once ON discounts (source_order_id) WHERE kind='prepaid_entitlement';
CREATE INDEX activity_retention ON bot_activity_events (occurred_at,id);
CREATE INDEX activity_member_time ON bot_activity_events (user_id,occurred_at DESC,id DESC);
CREATE UNIQUE INDEX source_without_variant ON product_supplier_sources (product_id,provider_code,external_product_id) WHERE external_variant_id IS NULL;
CREATE UNIQUE INDEX source_with_variant ON product_supplier_sources (product_id,provider_code,external_product_id,external_variant_id) WHERE external_variant_id IS NOT NULL;
CREATE UNIQUE INDEX subscription_active_once ON product_stock_subscriptions (user_id,product_id) WHERE closed_at IS NULL;
CREATE INDEX subscription_closed_retention ON product_stock_subscriptions (closed_at,id) WHERE closed_at IS NOT NULL;

CREATE TRIGGER listing_no_delete_delete BEFORE DELETE ON brand_products
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER wallet_entries_immutable_update BEFORE UPDATE ON wallet_entries
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER wallet_entries_immutable_delete BEFORE DELETE ON wallet_entries
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER audit_events_immutable_update BEFORE UPDATE ON audit_events
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER audit_events_immutable_delete BEFORE DELETE ON audit_events
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER discount_revisions_immutable_update BEFORE UPDATE ON discount_revisions
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER discount_revisions_immutable_delete BEFORE DELETE ON discount_revisions
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER support_messages_immutable_update BEFORE UPDATE ON support_messages
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER support_messages_immutable_delete BEFORE DELETE ON support_messages
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER legacy_id_map_immutable_update BEFORE UPDATE ON legacy_id_map
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER legacy_id_map_immutable_delete BEFORE DELETE ON legacy_id_map
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER orders_no_delete_delete BEFORE DELETE ON orders
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER payments_no_delete_delete BEFORE DELETE ON payments
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER fulfillments_no_delete_delete BEFORE DELETE ON fulfillments
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER supplier_requests_no_delete_delete BEFORE DELETE ON supplier_requests
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER supplier_request_attempts_no_delete_delete BEFORE DELETE ON supplier_request_attempts
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER discount_uses_no_delete_delete BEFORE DELETE ON discount_uses
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER discounts_no_delete_delete BEFORE DELETE ON discounts
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER wallets_no_delete_delete BEFORE DELETE ON wallets
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER inventory_items_no_delete_delete BEFORE DELETE ON inventory_items
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER support_tickets_no_delete_delete BEFORE DELETE ON support_tickets
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER campaigns_no_delete_delete BEFORE DELETE ON campaigns
BEGIN SELECT RAISE(ABORT,'immutable history'); END;

CREATE TRIGGER jobs_origin_guard BEFORE INSERT ON jobs
BEGIN
    SELECT CASE WHEN (NEW.payment_id IS NOT NULL) + (NEW.order_id IS NOT NULL) + (NEW.fulfillment_id IS NOT NULL) +
        (NEW.campaign_id IS NOT NULL) + (NEW.discount_id IS NOT NULL) + (NEW.support_message_id IS NOT NULL) +
        (NEW.telegram_delivery_id IS NOT NULL) + (NEW.product_stock_subscription_id IS NOT NULL) +
        (NEW.supplier_request_id IS NOT NULL) + (NEW.product_id IS NOT NULL) + (NEW.product_supplier_source_id IS NOT NULL)
        <> CASE WHEN NEW.job_kind IN ('refresh_fx','activity_cleanup','subscription_cleanup') THEN 0 ELSE 1 END
        THEN RAISE(ABORT,'job origin count') END;
    SELECT CASE WHEN NOT COALESCE(CASE
        WHEN NEW.job_kind IN ('activity_cleanup','subscription_cleanup') THEN NEW.brand_id IS NULL
        WHEN NEW.job_kind='refresh_fx' THEN NEW.brand_id IS NOT NULL
        WHEN NEW.job_kind IN ('drain_local_preorders','evaluate_restock') THEN NEW.brand_id IS NULL AND NEW.product_id IS NOT NULL
        WHEN NEW.job_kind='refresh_catalog_source' THEN NEW.brand_id IS NULL AND NEW.product_supplier_source_id IS NOT NULL
        WHEN NEW.job_kind IN ('expire_order_payment','prepare_manifest','issue_prepaid_coupon') THEN EXISTS (
            SELECT 1 FROM orders o JOIN users u ON u.id=o.user_id
            WHERE o.id=NEW.order_id AND u.brand_id=NEW.brand_id AND
              (NEW.job_kind='expire_order_payment' OR o.order_kind=CASE NEW.job_kind WHEN 'prepare_manifest' THEN 'product_purchase' ELSE 'prepaid_units' END))
        WHEN NEW.job_kind IN ('verify_payment','settle_wallet_topup') THEN EXISTS (
            SELECT 1 FROM payments p LEFT JOIN orders o ON o.id=p.order_id JOIN users u ON u.id=coalesce(p.topup_user_id,o.user_id)
            WHERE p.id=NEW.payment_id AND u.brand_id=NEW.brand_id AND (NEW.job_kind='verify_payment' OR p.topup_user_id IS NOT NULL))
        WHEN NEW.job_kind IN ('submit_supplier','reconcile_supplier','export_supplier') THEN EXISTS (
            SELECT 1 FROM supplier_requests r JOIN orders o ON o.id=r.order_id JOIN users u ON u.id=o.user_id
            WHERE r.id=NEW.supplier_request_id AND u.brand_id=NEW.brand_id AND (NEW.job_kind<>'submit_supplier' OR r.status='pending'))
        WHEN NEW.job_kind='send_telegram' THEN EXISTS (
            SELECT 1 FROM telegram_deliveries d WHERE d.id=NEW.telegram_delivery_id AND d.brand_id=NEW.brand_id)
        WHEN NEW.job_kind='campaign_materialize' THEN EXISTS (
            SELECT 1 FROM campaigns c WHERE c.id=NEW.campaign_id AND c.brand_id=NEW.brand_id)
        WHEN NEW.job_kind='coupon_notice' THEN EXISTS (
            SELECT 1 FROM discounts d WHERE d.id=NEW.discount_id AND d.brand_id=NEW.brand_id)
        WHEN NEW.job_kind='support_notice' THEN EXISTS (
            SELECT 1 FROM support_messages m JOIN support_tickets t ON t.id=m.ticket_id JOIN users u ON u.id=t.user_id
            WHERE m.id=NEW.support_message_id AND u.brand_id=NEW.brand_id)
        END,0) THEN RAISE(ABORT,'job origin scope') END;
    SELECT CASE WHEN (NEW.audit_event_id IS NOT NULL OR NEW.origin_revision IS NOT NULL) AND NOT (
        NEW.job_kind IN ('submit_supplier','reconcile_supplier','export_supplier') AND EXISTS (
            SELECT 1 FROM audit_events a WHERE a.id=NEW.audit_event_id AND a.actor_kind='admin' AND a.action='retry_supplier'
            AND a.entity_kind='supplier_request' AND a.entity_id=NEW.supplier_request_id AND a.new_revision=NEW.origin_revision
            AND a.brand_id=NEW.brand_id))
        THEN RAISE(ABORT,'job retry authorization') END;
END;

CREATE TRIGGER jobs_intent_guard BEFORE UPDATE ON jobs
WHEN (NEW.id,NEW.brand_id,NEW.job_kind,NEW.payment_id,NEW.order_id,NEW.fulfillment_id,NEW.campaign_id,NEW.discount_id,NEW.support_message_id,
    NEW.telegram_delivery_id,NEW.product_stock_subscription_id,NEW.supplier_request_id,NEW.product_id,NEW.product_supplier_source_id,
    NEW.audit_event_id,NEW.origin_revision,NEW.dedupe_key,NEW.created_at) IS NOT
    (OLD.id,OLD.brand_id,OLD.job_kind,OLD.payment_id,OLD.order_id,OLD.fulfillment_id,OLD.campaign_id,OLD.discount_id,OLD.support_message_id,
    OLD.telegram_delivery_id,OLD.product_stock_subscription_id,OLD.supplier_request_id,OLD.product_id,OLD.product_supplier_source_id,
    OLD.audit_event_id,OLD.origin_revision,OLD.dedupe_key,OLD.created_at)
BEGIN SELECT RAISE(ABORT,'immutable job intent'); END;

CREATE TRIGGER jobs_supplier_runnable BEFORE UPDATE ON jobs
WHEN NEW.job_kind='submit_supplier' AND NEW.status IN ('pending','leased','retry')
    AND NOT EXISTS (SELECT 1 FROM supplier_requests r WHERE r.id=NEW.supplier_request_id AND r.status='pending')
BEGIN SELECT RAISE(ABORT,'supplier request is not runnable'); END;

CREATE TRIGGER jobs_retention_delete BEFORE DELETE ON jobs
WHEN OLD.status NOT IN ('succeeded','failed','cancelled') OR NOT EXISTS (
    SELECT 1 FROM telegram_deliveries d JOIN product_stock_subscriptions s ON s.id=d.product_stock_subscription_id
    WHERE d.id=OLD.telegram_delivery_id AND d.purpose='restock_notice' AND d.status IN ('succeeded','failed','cancelled')
      AND s.closed_at<strftime('%Y-%m-%dT%H:%M:%f','now','-180 days') || '0000Z')
BEGIN SELECT RAISE(ABORT,'job retention'); END;

CREATE TRIGGER delivery_retention_delete BEFORE DELETE ON telegram_deliveries
WHEN OLD.status NOT IN ('succeeded','failed','cancelled') OR OLD.purpose<>'restock_notice' OR NOT EXISTS (
    SELECT 1 FROM product_stock_subscriptions s WHERE s.id=OLD.product_stock_subscription_id
      AND s.closed_at<strftime('%Y-%m-%dT%H:%M:%f','now','-180 days') || '0000Z')
BEGIN SELECT RAISE(ABORT,'delivery retention'); END;
