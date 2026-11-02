-- Canonical forward baseline 0001. Operator-invoked; never run on daemon startup.
-- Qualified only on disposable PostgreSQL 18.6; no historical import or live enablement.
-- All table declarations precede cyclic foreign keys and static guards.
CREATE SCHEMA shop;
REVOKE ALL ON SCHEMA shop FROM PUBLIC;
SET LOCAL search_path = shop, pg_catalog;
CREATE DOMAIN short_text AS text CHECK (length(VALUE) BETWEEN 1 AND 160 AND btrim(VALUE) <> '');
CREATE DOMAIN body_text AS text CHECK (length(VALUE) <= 16384);
CREATE DOMAIN asset_ref AS text CHECK (length(VALUE) BETWEEN 1 AND 1024 AND btrim(VALUE) <> '');
CREATE DOMAIN finite_numeric AS numeric CHECK (VALUE NOT IN ('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric));
CREATE DOMAIN review_code AS text CHECK (VALUE IN ('source_missing','source_disabled','account_unverified','source_ineligible','stock_shortage','cost_unverified','cost_limit','provider_unknown','provider_failed','partial_acquisition','identity_mismatch','credential_recovery'));
CREATE TABLE brands (
id uuid PRIMARY KEY,
    code short_text NOT NULL UNIQUE,
    internal_name short_text NOT NULL,
    public_name short_text NOT NULL,
    market text NOT NULL CHECK (market IN ('iran','international')),
    default_language short_text NOT NULL,
    logo_asset_ref asset_ref,
    bot_username short_text NOT NULL,
    bot_id bigint NOT NULL UNIQUE CHECK (bot_id > 0),
    bot_token_ciphertext bytea NOT NULL CHECK (octet_length(bot_token_ciphertext) > 0),
    bot_token_key_id short_text NOT NULL,
    active boolean NOT NULL DEFAULT false,
    allow_wallet boolean NOT NULL DEFAULT true,
    allow_crypto boolean NOT NULL DEFAULT false,
    allow_bank boolean NOT NULL DEFAULT false,
    allow_wallet_topup boolean NOT NULL DEFAULT false,
    zero_balance_topup boolean NOT NULL DEFAULT false,
    require_channel_join boolean NOT NULL DEFAULT false,
    support_enabled boolean NOT NULL DEFAULT false,
    fx_enabled boolean NOT NULL DEFAULT false,
    customer_support_admin_id uuid,
    bank_number_ciphertext bytea,
    bank_holder_ciphertext bytea,
    bank_key_id short_text,
    bank_card_fingerprint bytea,
    bank_changed_at timestamptz,
    current_fx finite_numeric CHECK (current_fx > 0),
    fx_source short_text,
    fx_fetched_at timestamptz,
    fx_error_code short_text,
    pricing_rounding_step finite_numeric NOT NULL DEFAULT 5000 CHECK (pricing_rounding_step > 0),
    max_exchange_rate_age_seconds bigint NOT NULL DEFAULT 3600 CHECK (max_exchange_rate_age_seconds > 0),
    telegram_update_highwater bigint NOT NULL DEFAULT -1 CHECK (telegram_update_highwater >= -1),
    telegram_update_window bytea NOT NULL DEFAULT decode(repeat('00',128),'hex') CHECK (octet_length(telegram_update_window)=128),
    policy_version bigint NOT NULL DEFAULT 0 CHECK (policy_version >= 0),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (num_nonnulls(bank_number_ciphertext,bank_holder_ciphertext,bank_key_id,bank_card_fingerprint,bank_changed_at) IN (0,5)),
    CHECK (bank_card_fingerprint IS NULL OR octet_length(bank_card_fingerprint)=32),
    CHECK (NOT allow_bank OR (market='iran' AND bank_card_fingerprint IS NOT NULL)),
    CHECK (num_nonnulls(current_fx,fx_source,fx_fetched_at) IN (0,3)),
    CHECK (NOT (active AND support_enabled) OR customer_support_admin_id IS NOT NULL),
    CHECK (isfinite(bank_changed_at)),
    CHECK (isfinite(fx_fetched_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE brand_channels (
brand_id uuid NOT NULL,
    chat_id bigint NOT NULL CHECK (chat_id < 0),
    title short_text,
    invite_link asset_ref,
    required boolean NOT NULL DEFAULT true,
    active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (brand_id,chat_id),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE users (
id uuid PRIMARY KEY,
    brand_id uuid NOT NULL,
    telegram_id bigint NOT NULL CHECK (telegram_id > 0),
    username short_text,
    first_name short_text,
    last_name short_text,
    language short_text NOT NULL,
    blocked_at timestamptz,
    banned_at timestamptz,
    marketing_opt_out_at timestamptz,
    first_seen_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    flow_state text NOT NULL DEFAULT 'idle' CHECK (flow_state IN ('idle','catalog','quantity','payment','receipt','support','discounts')),
    flow_revision bigint NOT NULL DEFAULT 0 CHECK (flow_revision >= 0),
    flow_order_id uuid,
    flow_product_id uuid,
    flow_anchor_message_id bigint CHECK (flow_anchor_message_id > 0),
    flow_expires_at timestamptz,
    UNIQUE (brand_id,telegram_id),
    UNIQUE (id,brand_id),
    CHECK (isfinite(blocked_at)),
    CHECK (isfinite(banned_at)),
    CHECK (isfinite(marketing_opt_out_at)),
    CHECK (isfinite(first_seen_at)),
    CHECK (isfinite(last_seen_at)),
    CHECK (isfinite(flow_expires_at))
);

CREATE TABLE admins (
id uuid PRIMARY KEY,
    login short_text NOT NULL UNIQUE,
    authentication_mode text NOT NULL CHECK (authentication_mode IN ('password','external')),
    password_verifier short_text,
    external_subject short_text,
    disabled_at timestamptz,
    auth_version bigint NOT NULL DEFAULT 0 CHECK (auth_version >= 0),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at timestamptz,
    telegram_user_id bigint UNIQUE CHECK (telegram_user_id > 0),
    public_contact_handle short_text,
    telegram_verified_by_admin_id uuid,
    telegram_verified_at timestamptz,
    telegram_verification_audit_id uuid,
    binding_challenge_hash bytea CHECK (octet_length(binding_challenge_hash)=32),
    binding_candidate_telegram_id bigint CHECK (binding_candidate_telegram_id > 0),
    binding_expires_at timestamptz,
    CHECK ((authentication_mode='password' AND password_verifier IS NOT NULL AND external_subject IS NULL) OR
           (authentication_mode='external' AND password_verifier IS NULL AND external_subject IS NOT NULL)),
    CHECK (num_nonnulls(telegram_user_id,telegram_verified_by_admin_id,telegram_verified_at,telegram_verification_audit_id) IN (0,4)),
    CHECK (public_contact_handle IS NULL OR telegram_user_id IS NOT NULL),
    CHECK (num_nonnulls(binding_challenge_hash,binding_candidate_telegram_id,binding_expires_at) IN (0,3)),
    CHECK (isfinite(disabled_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(last_login_at)),
    CHECK (isfinite(telegram_verified_at)),
    CHECK (isfinite(binding_expires_at))
);

CREATE TABLE admin_sessions (
id uuid PRIMARY KEY,
    admin_id uuid NOT NULL,
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash)=32),
    auth_version_at_issue bigint NOT NULL CHECK (auth_version_at_issue >= 0),
    issued_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    last_used_at timestamptz,
    CHECK (expires_at > issued_at),
    CHECK (revoked_at IS NULL OR revoked_at >= issued_at),
    CHECK (isfinite(issued_at)),
    CHECK (isfinite(expires_at)),
    CHECK (isfinite(revoked_at)),
    CHECK (isfinite(last_used_at))
);

CREATE TABLE products (
id uuid PRIMARY KEY,
    sku short_text UNIQUE,
    fulfillment_kind text NOT NULL CHECK (fulfillment_kind IN ('local','supplier','manual')),
    local_first boolean NOT NULL DEFAULT true,
    availability_policy text NOT NULL CHECK (availability_policy IN ('immediate','local_preorder')),
    active_supplier_source_id uuid,
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    title short_text NOT NULL,
    description body_text,
    icon_ref asset_ref,
    guidance body_text,
    active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (availability_policy <> 'local_preorder' OR fulfillment_kind='local'),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE brand_products (
brand_id uuid NOT NULL,
    product_id uuid NOT NULL,
    visible boolean NOT NULL DEFAULT false,
    title_mode text NOT NULL DEFAULT 'inherit' CHECK (title_mode IN ('inherit','override')),
    title_override short_text,
    description_mode text NOT NULL DEFAULT 'inherit' CHECK (description_mode IN ('inherit','override','clear')),
    description_override body_text,
    icon_mode text NOT NULL DEFAULT 'inherit' CHECK (icon_mode IN ('inherit','override','clear')),
    icon_override asset_ref,
    guidance_mode text NOT NULL DEFAULT 'inherit' CHECK (guidance_mode IN ('inherit','override','clear')),
    guidance_override body_text,
    price_usdt finite_numeric NOT NULL CHECK (price_usdt > 0),
    price_toman finite_numeric CHECK (price_toman > 0),
    auto_price_toman boolean NOT NULL DEFAULT true,
    allow_wallet boolean NOT NULL DEFAULT true,
    allow_crypto boolean NOT NULL DEFAULT false,
    allow_bank boolean NOT NULL DEFAULT false,
    min_quantity integer NOT NULL DEFAULT 1 CHECK (min_quantity > 0),
    max_quantity integer NOT NULL DEFAULT 100 CHECK (max_quantity >= min_quantity),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (brand_id,product_id),
    CHECK ((title_mode='override')=(title_override IS NOT NULL)),
    CHECK ((description_mode='override')=(description_override IS NOT NULL)),
    CHECK ((icon_mode='override')=(icon_override IS NOT NULL)),
    CHECK ((guidance_mode='override')=(guidance_override IS NOT NULL)),
    CHECK (NOT (allow_bank AND NOT auto_price_toman) OR price_toman IS NOT NULL),
    CHECK (isfinite(updated_at))
);

CREATE TABLE inventory_items (
id uuid PRIMARY KEY,
    product_id uuid NOT NULL,
    credential_ciphertext bytea NOT NULL CHECK (octet_length(credential_ciphertext)>0),
    credential_key_id short_text NOT NULL,
    source_fingerprint bytea NOT NULL CHECK (octet_length(source_fingerprint)=32),
    origin_kind text NOT NULL CHECK (origin_kind IN ('local_import','supplier_bonus')),
    supplier_request_id uuid,
    remote_item_ref short_text,
    supplier_item_key bytea UNIQUE CHECK (octet_length(supplier_item_key)=32),
    state text NOT NULL DEFAULT 'available' CHECK (state IN ('available','allocated','retired','admin_held')),
    assigned_fulfillment_id uuid UNIQUE,
    allocated_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK ((state='allocated')=(assigned_fulfillment_id IS NOT NULL)),
    CHECK ((assigned_fulfillment_id IS NULL)=(allocated_at IS NULL)),
    CHECK ((origin_kind='supplier_bonus' AND supplier_request_id IS NOT NULL AND state='admin_held' AND assigned_fulfillment_id IS NULL) OR
           (origin_kind='local_import' AND supplier_request_id IS NULL AND state <> 'admin_held')),
    CHECK (isfinite(allocated_at)),
    CHECK (isfinite(created_at))
);

CREATE TABLE wallets (
user_id uuid PRIMARY KEY,
    balance finite_numeric NOT NULL DEFAULT 0 CHECK (balance >= 0),
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (isfinite(updated_at))
);

CREATE TABLE wallet_entries (
id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    signed_amount finite_numeric NOT NULL CHECK (signed_amount <> 0),
    balance_after finite_numeric CHECK (balance_after >= 0),
    posting_version bigint CHECK (posting_version > 0),
    movement_kind text NOT NULL CHECK (movement_kind IN ('opening_balance','wallet_topup','purchase_debit','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','admin_adjustment','legacy_history')),
    is_posting boolean NOT NULL,
    idempotency_key short_text NOT NULL,
    order_id uuid,
    payment_id uuid,
    supplier_request_id uuid,
    legacy_import_batch uuid,
    admin_id uuid,
    audit_event_id uuid,
    reason body_text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (user_id,idempotency_key),
    CHECK ((is_posting AND balance_after IS NOT NULL AND posting_version IS NOT NULL AND movement_kind <> 'legacy_history') OR
           (NOT is_posting AND posting_version IS NULL AND movement_kind='legacy_history' AND legacy_import_batch IS NOT NULL)),
    CHECK (movement_kind <> 'purchase_debit' OR signed_amount < 0),
    CHECK (movement_kind IN ('purchase_debit','admin_adjustment','legacy_history') OR signed_amount > 0),
    CHECK ((movement_kind='opening_balance' AND legacy_import_batch IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR
           (movement_kind='legacy_history' AND num_nonnulls(order_id,payment_id,supplier_request_id,admin_id,audit_event_id)=0) OR
           (movement_kind='wallet_topup' AND payment_id IS NOT NULL AND num_nonnulls(order_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR
           (movement_kind='purchase_debit' AND order_id IS NOT NULL AND num_nonnulls(payment_id,supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR
           (movement_kind IN ('immediate_crypto_shortage_credit','late_crypto_credit') AND order_id IS NOT NULL AND payment_id IS NOT NULL AND num_nonnulls(supplier_request_id,legacy_import_batch,admin_id,audit_event_id)=0) OR
           (movement_kind='supplier_wallet_credit' AND order_id IS NOT NULL AND supplier_request_id IS NOT NULL AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(payment_id,legacy_import_batch)=0) OR
           (movement_kind='admin_adjustment' AND admin_id IS NOT NULL AND audit_event_id IS NOT NULL AND num_nonnulls(order_id,payment_id,supplier_request_id,legacy_import_batch)=0)),
    CHECK (isfinite(created_at))
);

CREATE TABLE orders (
id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    product_id uuid,
    quantity integer NOT NULL CHECK (quantity > 0),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    order_kind text NOT NULL CHECK (order_kind IN ('product_purchase','prepaid_units')),
    payment_status text NOT NULL CHECK (payment_status IN ('awaiting_payment','under_review','confirmation_pending','paid','not_required','expired','cancelled')),
    fulfillment_status text NOT NULL DEFAULT 'unfulfilled' CHECK (fulfillment_status IN ('unfulfilled','waiting_stock','acquiring','requires_admin','ready','delivering','completed','cancelled','compensated')),
    currency text NOT NULL CHECK (currency IN ('USDT','TOMAN')),
    payment_method text NOT NULL CHECK (payment_method IN ('wallet','crypto','bank','no_payment_required')),
    unit_price_usdt finite_numeric NOT NULL CHECK (unit_price_usdt > 0),
    unit_price_toman finite_numeric CHECK (unit_price_toman > 0),
    fx_rate_snapshot finite_numeric CHECK (fx_rate_snapshot > 0),
    fx_source_snapshot short_text,
    fx_fetched_at_snapshot timestamptz,
    auto_price_toman_snapshot boolean NOT NULL,
    pricing_rounding_step_snapshot finite_numeric NOT NULL CHECK (pricing_rounding_step_snapshot > 0),
    price_calculation_version short_text NOT NULL,
    discount_pct_snapshot finite_numeric CHECK (discount_pct_snapshot BETWEEN 0 AND 100),
    discount_units_snapshot integer CHECK (discount_units_snapshot > 0),
    payment_due_at timestamptz,
    payment_confirmed_at timestamptz,
    brand_public_name_snapshot short_text NOT NULL,
    product_title_snapshot short_text NOT NULL,
    product_description_snapshot body_text,
    product_icon_snapshot asset_ref,
    product_guidance_snapshot body_text,
    payment_terms_snapshot body_text NOT NULL,
    fulfillment_kind_snapshot text NOT NULL CHECK (fulfillment_kind_snapshot IN ('local','supplier','manual')),
    local_first_snapshot boolean NOT NULL,
    is_preorder boolean NOT NULL,
    review_code review_code,
    hold_at timestamptz,
    hold_admin_id uuid,
    hold_reason body_text,
    resolution_kind text CHECK (resolution_kind IN ('replaced','unpaid_expiry','local_preorder_cancellation','immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit','manual_fulfilled')),
    resolution_admin_id uuid,
    resolution_at timestamptz,
    resolution_audit_event_id uuid,
    resolution_wallet_entry_id uuid UNIQUE,
    delivery_generation uuid,
    manifest_asset_ref asset_ref,
    manifest_digest bytea CHECK (octet_length(manifest_digest)=32),
    manifest_prepared_at timestamptz,
    release_authorized_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (id,delivery_generation),
    CHECK (num_nonnulls(hold_at,hold_admin_id,hold_reason) IN (0,3)),
    CHECK (num_nonnulls(manifest_asset_ref,manifest_digest,manifest_prepared_at,delivery_generation) IN (0,4)),
    CHECK (release_authorized_at IS NULL OR (manifest_prepared_at IS NOT NULL AND release_authorized_at >= manifest_prepared_at)),
    CHECK (num_nonnulls(fx_rate_snapshot,fx_source_snapshot,fx_fetched_at_snapshot) IN (0,3)),
    CHECK (num_nonnulls(discount_pct_snapshot,discount_units_snapshot) IN (0,2)),
    CHECK (discount_units_snapshot IS NULL OR discount_units_snapshot=quantity),
    CHECK ((currency='TOMAN')=(payment_method='bank')),
    CHECK (currency <> 'TOMAN' OR unit_price_toman IS NOT NULL),
    CHECK (payment_method <> 'no_payment_required' OR (payment_status='not_required' AND discount_pct_snapshot IS NOT NULL AND discount_pct_snapshot=100)),
    CHECK (payment_status <> 'not_required' OR payment_method='no_payment_required'),
    CHECK (payment_status NOT IN ('paid','not_required') OR payment_confirmed_at IS NOT NULL),
    CHECK (payment_status NOT IN ('awaiting_payment','under_review','confirmation_pending') OR (payment_due_at IS NOT NULL AND payment_method IN ('crypto','bank'))),
    CHECK (payment_due_at IS NULL OR payment_due_at=created_at + INTERVAL '30 minutes'),
    CHECK (NOT is_preorder OR (order_kind='product_purchase' AND fulfillment_kind_snapshot='local')),
    CHECK ((resolution_kind IS NULL AND num_nonnulls(resolution_admin_id,resolution_at,resolution_audit_event_id,resolution_wallet_entry_id)=0) OR
           (resolution_kind IS NOT NULL AND resolution_at IS NOT NULL)),
    CHECK (resolution_kind NOT IN ('local_preorder_cancellation','supplier_wallet_credit','manual_fulfilled') OR
           (resolution_admin_id IS NOT NULL AND resolution_audit_event_id IS NOT NULL)),
    CHECK (resolution_kind NOT IN ('immediate_crypto_shortage_credit','late_crypto_credit','supplier_wallet_credit') OR resolution_wallet_entry_id IS NOT NULL),
    CHECK (resolution_kind <> 'local_preorder_cancellation' OR (is_preorder AND resolution_wallet_entry_id IS NULL AND fulfillment_status='cancelled')),
    CHECK (resolution_kind NOT IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') OR fulfillment_status='compensated'),
    CHECK (isfinite(fx_fetched_at_snapshot)),
    CHECK (isfinite(payment_due_at)),
    CHECK (isfinite(payment_confirmed_at)),
    CHECK (isfinite(hold_at)),
    CHECK (isfinite(resolution_at)),
    CHECK (isfinite(manifest_prepared_at)),
    CHECK (isfinite(release_authorized_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE payments (
id uuid PRIMARY KEY,
    order_id uuid,
    topup_user_id uuid,
    payment_method text NOT NULL CHECK (payment_method IN ('crypto','bank')),
    quote_currency text NOT NULL CHECK (quote_currency IN ('USD','TOMAN')),
    amount_due finite_numeric NOT NULL CHECK (amount_due > 0),
    amount_received finite_numeric CHECK (amount_received >= 0),
    provider_code short_text,
    provider_payment_ref short_text,
    idempotency_ref short_text NOT NULL UNIQUE,
    network_asset text CHECK (network_asset IN ('USDTTRC20','USDTBSC')),
    expected_network_amount finite_numeric CHECK (expected_network_amount > 0),
    ipn_key_id short_text,
    ipn_signature bytea CHECK (octet_length(ipn_signature)=64),
    ipn_payload_sha256 bytea CHECK (octet_length(ipn_payload_sha256)=32),
    ipn_verified_at timestamptz,
    network_amount finite_numeric CHECK (network_amount >= 0),
    provider_confirmations bigint CHECK (provider_confirmations >= 0),
    first_payment_observed_at timestamptz,
    confirmed_at timestamptz,
    status text NOT NULL DEFAULT 'created' CHECK (status IN ('created','awaiting','observed','confirming','settled','rejected','expired','review')),
    provider_status_code short_text,
    bank_destination_ciphertext bytea,
    bank_destination_key_id short_text,
    receipt_asset_ref asset_ref,
    review_admin_id uuid,
    reviewed_at timestamptz,
    review_reason body_text,
    compensating_wallet_entry_id uuid UNIQUE,
    evidence_asset_ref asset_ref,
    evidence_digest bytea CHECK (octet_length(evidence_digest)=32),
    legacy_kind short_text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (num_nonnulls(order_id,topup_user_id)=1),
    CHECK (num_nonnulls(bank_destination_ciphertext,bank_destination_key_id) IN (0,2)),
    CHECK (num_nonnulls(evidence_asset_ref,evidence_digest) IN (0,2)),
    CHECK (num_nonnulls(review_admin_id,reviewed_at,review_reason) IN (0,3)),
    CHECK (provider_payment_ref IS NULL OR provider_code IS NOT NULL),
    CHECK ((payment_method='crypto' AND quote_currency='USD' AND network_asset IS NOT NULL AND bank_destination_ciphertext IS NULL) OR
           (payment_method='bank' AND quote_currency='TOMAN' AND order_id IS NOT NULL AND bank_destination_ciphertext IS NOT NULL AND network_asset IS NULL AND network_amount IS NULL AND provider_confirmations IS NULL)),
    CHECK (topup_user_id IS NULL OR payment_method='crypto'),
    CHECK (status <> 'settled' OR confirmed_at IS NOT NULL),
    CHECK (num_nonnulls(ipn_key_id,ipn_signature,ipn_payload_sha256,ipn_verified_at) IN (0,4)),
    CHECK (ipn_key_id IS NULL OR (payment_method='crypto' AND provider_payment_ref IS NOT NULL AND expected_network_amount IS NOT NULL AND evidence_asset_ref IS NOT NULL)),
    CHECK (expected_network_amount IS NULL OR payment_method='crypto'),
    CHECK (isfinite(ipn_verified_at)),
    CHECK (isfinite(first_payment_observed_at)),
    CHECK (isfinite(confirmed_at)),
    CHECK (isfinite(reviewed_at)),
    CHECK (isfinite(created_at))
);

CREATE TABLE fulfillments (
id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    unit_no integer NOT NULL CHECK (unit_no > 0),
    kind text CHECK (kind IN ('local','supplier','manual')),
    status text NOT NULL DEFAULT 'unfilled' CHECK (status IN ('unfilled','acquired','ready','released','retained_admin')),
    supplier_request_id uuid,
    remote_item_ref short_text,
    supplier_item_key bytea UNIQUE CHECK (octet_length(supplier_item_key)=32),
    delivered_content bytea,
    delivered_content_key_id short_text,
    content_kind text CHECK (content_kind IN ('account','key','code','text','file')),
    resolution_admin_id uuid,
    resolution_note body_text,
    manifest_generation uuid,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at timestamptz,
    UNIQUE (order_id,unit_no),
    CHECK (kind IS NOT NULL OR status='unfilled'),
    CHECK ((kind IS NOT DISTINCT FROM 'supplier')=(supplier_request_id IS NOT NULL)),
    CHECK (num_nonnulls(delivered_content,delivered_content_key_id,content_kind) IN (0,3)),
    CHECK (status NOT IN ('acquired','ready','released','retained_admin') OR delivered_content IS NOT NULL),
    CHECK (status <> 'released' OR (manifest_generation IS NOT NULL AND completed_at IS NOT NULL)),
    CHECK (status <> 'retained_admin' OR resolution_admin_id IS NOT NULL),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(completed_at))
);

CREATE TABLE supplier_requests (
id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    requested_quantity integer NOT NULL CHECK (requested_quantity > 0),
    product_supplier_source_id uuid,
    source_revision bigint CHECK (source_revision > 0),
    provider_code short_text,
    account_ref short_text,
    external_product_id short_text,
    external_variant_id short_text,
    local_intent_key short_text NOT NULL UNIQUE,
    remote_ref short_text,
    request_sha256 bytea NOT NULL CHECK (octet_length(request_sha256)=32),
    status text NOT NULL DEFAULT 'blocked' CHECK (status IN ('blocked','pending','in_flight','unknown','partial_pending_admin','succeeded','failed','resolved')),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    review_code review_code,
    next_check_at timestamptz,
    evidence_ref asset_ref,
    cost_baseline_amount finite_numeric CHECK (cost_baseline_amount > 0),
    cost_currency short_text,
    cost_quote_ref short_text,
    cost_observed_at timestamptz,
    approved_cost_ceiling finite_numeric CHECK (approved_cost_ceiling > 0),
    approval_audit_id uuid,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (num_nonnulls(cost_baseline_amount,cost_currency,cost_quote_ref,cost_observed_at) IN (0,4)),
    CHECK (num_nonnulls(approved_cost_ceiling,approval_audit_id) IN (0,2)),
    CHECK (status='blocked' OR (product_supplier_source_id IS NOT NULL AND source_revision IS NOT NULL AND provider_code IS NOT NULL AND account_ref IS NOT NULL AND external_product_id IS NOT NULL AND (cost_baseline_amount IS NOT NULL OR approved_cost_ceiling IS NOT NULL))),
    CHECK (status <> 'blocked' OR review_code IS NOT NULL),
    CHECK (isfinite(next_check_at)),
    CHECK (isfinite(cost_observed_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE jobs (
id uuid PRIMARY KEY,
    brand_id uuid,
    job_kind text NOT NULL CHECK (job_kind IN ('expire_order_payment','verify_payment','settle_wallet_topup','submit_supplier','reconcile_supplier','export_supplier','prepare_manifest','issue_prepaid_coupon','send_telegram','drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx','campaign_materialize','coupon_notice','support_notice','activity_cleanup','subscription_cleanup')),
    payment_id uuid,
    order_id uuid,
    fulfillment_id uuid,
    campaign_id uuid,
    discount_id uuid,
    support_message_id uuid,
    telegram_delivery_id uuid,
    product_stock_subscription_id uuid,
    supplier_request_id uuid,
    product_id uuid,
    product_supplier_source_id uuid,
    audit_event_id uuid,
    origin_revision bigint CHECK (origin_revision >= 0),
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','leased','succeeded','retry','failed','cancelled')),
    attempts bigint NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    available_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    locked_by short_text,
    lease_until timestamptz,
    dedupe_key short_text NOT NULL UNIQUE,
    last_error_code short_text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at timestamptz,
    CHECK ((status='leased')=(locked_by IS NOT NULL)),
    CHECK ((locked_by IS NULL)=(lease_until IS NULL)),
    CHECK (status NOT IN ('succeeded','failed','cancelled') OR completed_at IS NOT NULL),
    CHECK (isfinite(available_at)),
    CHECK (isfinite(lease_until)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(completed_at))
);

CREATE TABLE campaigns (
id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    brand_id uuid NOT NULL,
    language short_text NOT NULL,
    audience_kind text NOT NULL CHECK (audience_kind IN ('all','active_since','language')),
    active_since timestamptz,
    audience_language short_text,
    message_text body_text,
    media_ref asset_ref,
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','approved','running','completed','cancelled')),
    created_by_admin_id uuid NOT NULL,
    approved_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    started_at timestamptz,
    completed_at timestamptz,
    UNIQUE (batch_id,brand_id,language),
    UNIQUE (id,batch_id,brand_id),
    CHECK (num_nonnulls(message_text,media_ref)>0),
    CHECK ((audience_kind='active_since')=(active_since IS NOT NULL)),
    CHECK ((audience_kind='language')=(audience_language IS NOT NULL)),
    CHECK (status NOT IN ('approved','running','completed') OR approved_at IS NOT NULL),
    CHECK (isfinite(active_since)),
    CHECK (isfinite(approved_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(started_at)),
    CHECK (isfinite(completed_at))
);

CREATE TABLE telegram_deliveries (
id uuid PRIMARY KEY,
    brand_id uuid NOT NULL,
    recipient_kind text NOT NULL CHECK (recipient_kind IN ('customer_dm','staff_chat')),
    user_id uuid,
    staff_chat_id bigint CHECK (staff_chat_id < 0),
    purpose text NOT NULL CHECK (purpose IN ('order_credentials','order_status','campaign','discount_notice','support_notice','restock_notice','staff_supplier_case','staff_supplier_bonus')),
    action text NOT NULL CHECK (action IN ('send','edit')),
    target_message_id bigint CHECK (target_message_id > 0),
    part_no integer NOT NULL DEFAULT 1 CHECK (part_no > 0),
    delivery_generation uuid,
    recipient_language short_text,
    eligibility_checked_at timestamptz,
    dedupe_key short_text NOT NULL UNIQUE,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','leased','succeeded','retry','failed','cancelled','uncertain')),
    attempts bigint NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_attempt_at timestamptz,
    telegram_message_id bigint CHECK (telegram_message_id > 0),
    last_error_code short_text,
    sent_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    campaign_id uuid,
    campaign_batch_id uuid,
    discount_id uuid,
    order_id uuid,
    fulfillment_id uuid,
    support_message_id uuid,
    product_stock_subscription_id uuid,
    supplier_request_id uuid,
    audit_event_id uuid,
    CHECK ((recipient_kind='customer_dm' AND user_id IS NOT NULL AND staff_chat_id IS NULL AND purpose NOT IN ('staff_supplier_case','staff_supplier_bonus')) OR
           (recipient_kind='staff_chat' AND user_id IS NULL AND staff_chat_id IS NOT NULL AND purpose IN ('staff_supplier_case','staff_supplier_bonus'))),
    CHECK (action <> 'edit' OR target_message_id IS NOT NULL),
    CHECK (status <> 'succeeded' OR sent_at IS NOT NULL),
    CHECK (num_nonnulls(campaign_id,campaign_batch_id) IN (0,2)),
    CHECK (isfinite(eligibility_checked_at)),
    CHECK (isfinite(next_attempt_at)),
    CHECK (isfinite(last_attempt_at)),
    CHECK (isfinite(sent_at)),
    CHECK (isfinite(created_at))
);

CREATE TABLE support_tickets (
id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    order_id uuid,
    category text NOT NULL CHECK (category IN ('general','order','payment','preorder_cancellation')),
    status text NOT NULL DEFAULT 'open' CHECK (status IN ('open','assigned','waiting_customer','closed')),
    source text NOT NULL CHECK (source IN ('customer','admin','system')),
    assigned_admin_id uuid,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    closed_at timestamptz,
    CHECK ((status='closed')=(closed_at IS NOT NULL)),
    CHECK (category <> 'preorder_cancellation' OR order_id IS NOT NULL),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at)),
    CHECK (isfinite(closed_at))
);

CREATE TABLE support_messages (
id uuid PRIMARY KEY,
    ticket_id uuid NOT NULL,
    sender_kind text NOT NULL CHECK (sender_kind IN ('customer','admin','system')),
    sender_admin_id uuid,
    sender_telegram_id bigint CHECK (sender_telegram_id > 0),
    body_ciphertext bytea,
    body_key_id short_text,
    asset_ref asset_ref,
    telegram_message_id bigint CHECK (telegram_message_id > 0),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK ((sender_kind='admin')=(sender_admin_id IS NOT NULL)),
    CHECK (num_nonnulls(body_ciphertext,body_key_id) IN (0,2)),
    CHECK (num_nonnulls(body_ciphertext,asset_ref)>0),
    CHECK (isfinite(created_at))
);

CREATE TABLE legacy_id_map (
source_brand short_text NOT NULL,
    source_table short_text NOT NULL,
    source_key_kind text NOT NULL CHECK (source_key_kind IN ('primary','public_order_reference')),
    legacy_id short_text NOT NULL,
    target_kind text NOT NULL CHECK (target_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event')),
    target_id uuid NOT NULL,
    target_brand_id uuid,
    source_fingerprint bytea NOT NULL CHECK (octet_length(source_fingerprint)=32),
    import_batch uuid NOT NULL,
    import_manifest_ref asset_ref NOT NULL,
    migrated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_brand,source_table,source_key_kind,legacy_id),
    CHECK (isfinite(migrated_at))
);

CREATE TABLE audit_events (
id uuid PRIMARY KEY,
    actor_kind text NOT NULL CHECK (actor_kind IN ('admin','customer','system')),
    actor_admin_id uuid,
    actor_user_id uuid,
    brand_id uuid,
    action text NOT NULL CHECK (action IN ('create','update','disable','enable','bind_telegram','revoke_telegram','channel_enforcement','select_source','stock_import','manual_fulfillment','hold_preorder','resume_preorder','cancel_preorder','retry_supplier','supplier_credit','approve_cost','coupon_issue','coupon_edit','wallet_adjustment','support_assign','campaign_approve','delivery_recovery','legacy_import','maintenance')),
    entity_kind text NOT NULL CHECK (entity_kind IN ('brand','user','admin','product','brand_product','inventory_item','wallet','wallet_entry','order','payment','fulfillment','supplier_request','campaign','telegram_delivery','support_ticket','support_message','discount','discount_revision','supplier_request_attempt','product_supplier_source','product_stock_subscription','bot_activity_event','platform')),
    entity_id uuid,
    reason body_text NOT NULL,
    previous_revision bigint CHECK (previous_revision >= 0),
    new_revision bigint CHECK (new_revision >= 0),
    correlation_id uuid NOT NULL,
    command_sha256 bytea CHECK (octet_length(command_sha256)=32),
    before_channel_enforcement boolean,
    after_channel_enforcement boolean,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK ((actor_kind='admin' AND actor_admin_id IS NOT NULL AND actor_user_id IS NULL) OR
           (actor_kind='customer' AND actor_admin_id IS NULL AND actor_user_id IS NOT NULL) OR
           (actor_kind='system' AND actor_admin_id IS NULL AND actor_user_id IS NULL)),
    CHECK ((entity_kind='platform')=(entity_id IS NULL)),
    CHECK ((action='channel_enforcement' AND entity_kind='brand' AND before_channel_enforcement IS NOT NULL AND after_channel_enforcement IS NOT NULL) OR
           (action <> 'channel_enforcement' AND before_channel_enforcement IS NULL AND after_channel_enforcement IS NULL)),
    CHECK (isfinite(created_at))
);

CREATE TABLE discounts (
id uuid PRIMARY KEY,
    brand_id uuid NOT NULL,
    code_digest bytea NOT NULL CHECK (octet_length(code_digest)=32),
    code_ciphertext bytea NOT NULL CHECK (octet_length(code_ciphertext)>0),
    code_key_id short_text NOT NULL,
    display_prefix short_text NOT NULL,
    issuance_batch_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('prepaid_entitlement','goodwill','promotion')),
    percent_off finite_numeric NOT NULL CHECK (percent_off BETWEEN 0 AND 100),
    product_id uuid,
    eligible_user_id uuid,
    source_order_id uuid,
    max_total_uses bigint CHECK (max_total_uses >= 0),
    max_uses_per_user bigint CHECK (max_uses_per_user >= 0),
    redeemed_count bigint NOT NULL DEFAULT 0 CHECK (redeemed_count >= 0),
    usage_adjustment_units bigint NOT NULL DEFAULT 0,
    starts_at timestamptz,
    ends_at timestamptz,
    active boolean NOT NULL DEFAULT true,
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    created_by_admin_id uuid,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (brand_id,code_digest),
    CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CHECK ((kind='prepaid_entitlement' AND product_id IS NOT NULL AND eligible_user_id IS NOT NULL AND source_order_id IS NOT NULL) OR
           (kind <> 'prepaid_entitlement' AND source_order_id IS NULL)),
    CHECK (created_by_admin_id IS NOT NULL OR kind='prepaid_entitlement'),
    CHECK (isfinite(starts_at)),
    CHECK (isfinite(ends_at)),
    CHECK (isfinite(created_at))
);

CREATE TABLE discount_uses (
discount_id uuid NOT NULL,
    order_id uuid NOT NULL UNIQUE,
    discount_revision_id uuid NOT NULL,
    units_covered integer NOT NULL CHECK (units_covered > 0),
    saved_amount finite_numeric NOT NULL CHECK (saved_amount >= 0),
    saved_currency text NOT NULL CHECK (saved_currency IN ('USD','TOMAN')),
    status text NOT NULL CHECK (status IN ('held','redeemed','released')),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    transitioned_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (discount_id,order_id),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(transitioned_at))
);

CREATE TABLE discount_revisions (
id uuid PRIMARY KEY,
    discount_id uuid NOT NULL,
    revision_no bigint NOT NULL CHECK (revision_no >= 0),
    audit_event_id uuid NOT NULL UNIQUE,
    percent_off finite_numeric NOT NULL CHECK (percent_off BETWEEN 0 AND 100),
    max_total_uses bigint CHECK (max_total_uses >= 0),
    max_uses_per_user bigint CHECK (max_uses_per_user >= 0),
    usage_adjustment_units bigint NOT NULL,
    redeemed_count_at_revision bigint NOT NULL CHECK (redeemed_count_at_revision >= 0),
    held_units_at_revision bigint NOT NULL CHECK (held_units_at_revision >= 0),
    starts_at timestamptz,
    ends_at timestamptz,
    active boolean NOT NULL,
    product_id uuid,
    eligible_user_id uuid,
    UNIQUE (discount_id,revision_no),
    CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
    CHECK (isfinite(starts_at)),
    CHECK (isfinite(ends_at))
);

CREATE TABLE bot_activity_events (
id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    event_type text NOT NULL CHECK (event_type IN ('navigation','quantity','checkout','support','subscription','discount','policy','flow')),
    source_kind text NOT NULL CHECK (source_kind IN ('command','callback','flow','system')),
    action_code text NOT NULL CHECK (action_code IN ('start','catalog_open','product_open','quantity_enter','checkout_open','support_open','support_reply','subscribe','unsubscribe','discounts_open','policy_check','checkout_created','flow_expired','quantity_rejected')),
    product_id uuid,
    order_id uuid,
    support_ticket_id uuid,
    quantity_selected integer CHECK (quantity_selected > 0),
    flow_revision bigint CHECK (flow_revision >= 0),
    telegram_update_id bigint CHECK (telegram_update_id >= 0),
    outcome_code text NOT NULL CHECK (outcome_code IN ('accepted','rejected','unavailable','blocked','cancelled','duplicate')),
    idempotency_key short_text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (user_id,idempotency_key),
    CHECK ((event_type='navigation' AND action_code IN ('start','catalog_open','product_open')) OR
           (event_type='quantity' AND action_code IN ('quantity_enter','quantity_rejected')) OR
           (event_type='checkout' AND action_code IN ('checkout_open','checkout_created')) OR
           (event_type='support' AND action_code IN ('support_open','support_reply')) OR
           (event_type='subscription' AND action_code IN ('subscribe','unsubscribe')) OR
           (event_type='discount' AND action_code='discounts_open') OR
           (event_type='policy' AND action_code='policy_check') OR
           (event_type='flow' AND action_code='flow_expired')),
    CHECK (action_code NOT IN ('product_open','quantity_enter','checkout_open','checkout_created','subscribe','unsubscribe','quantity_rejected') OR product_id IS NOT NULL),
    CHECK (action_code NOT IN ('quantity_enter','checkout_created') OR quantity_selected IS NOT NULL),
    CHECK (action_code <> 'support_reply' OR support_ticket_id IS NOT NULL),
    CHECK (action_code <> 'checkout_created' OR order_id IS NOT NULL),
    CHECK (action_code NOT IN ('flow_expired','quantity_rejected') OR flow_revision IS NOT NULL),
    CHECK (action_code <> 'quantity_rejected' OR (source_kind='flow' AND outcome_code='rejected' AND quantity_selected IS NULL)),
    CHECK (isfinite(occurred_at))
);

CREATE TABLE supplier_request_attempts (
id uuid PRIMARY KEY,
    supplier_request_id uuid NOT NULL,
    attempt_no bigint NOT NULL CHECK (attempt_no > 0),
    operation text NOT NULL CHECK (operation IN ('submit','status_lookup','export')),
    outcome text NOT NULL DEFAULT 'in_flight' CHECK (outcome IN ('in_flight','succeeded','failed','timeout','unknown')),
    parent_submit_attempt_id uuid,
    admin_authorization_audit_id uuid,
    purchase_idempotency_key short_text,
    max_charge_amount finite_numeric CHECK (max_charge_amount > 0),
    http_status integer CHECK (http_status BETWEEN 100 AND 599),
    error_code short_text,
    remote_ref short_text,
    evidence_ref asset_ref,
    started_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at timestamptz,
    UNIQUE (supplier_request_id,attempt_no),
    CHECK ((outcome='in_flight')=(finished_at IS NULL)),
    CHECK (finished_at IS NULL OR finished_at >= started_at),
    CHECK ((operation='submit' AND parent_submit_attempt_id IS NULL AND max_charge_amount IS NOT NULL) OR
           (operation IN ('status_lookup','export') AND parent_submit_attempt_id IS NOT NULL AND purchase_idempotency_key IS NULL AND max_charge_amount IS NULL AND admin_authorization_audit_id IS NULL)),
    CHECK (isfinite(started_at)),
    CHECK (isfinite(finished_at))
);

CREATE TABLE product_supplier_sources (
id uuid PRIMARY KEY,
    product_id uuid NOT NULL,
    provider_code text NOT NULL CHECK (provider_code IN ('akunding','canboso','prodseller','rapidx','roboticvn','synthetic')),
    external_product_id short_text NOT NULL,
    external_variant_id short_text,
    enabled boolean NOT NULL DEFAULT false,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    observation_revision bigint NOT NULL DEFAULT 0 CHECK (observation_revision >= 0),
    observed_source_revision bigint CHECK (observed_source_revision > 0),
    observed_account_ref short_text,
    observed_at timestamptz,
    availability_code text NOT NULL DEFAULT 'unknown' CHECK (availability_code IN ('unknown','available','out_of_stock','ineligible','error')),
    available_quantity integer CHECK (available_quantity >= 0),
    observed_unit_cost finite_numeric CHECK (observed_unit_cost > 0),
    observed_currency short_text,
    eligibility_code text NOT NULL DEFAULT 'unknown' CHECK (eligibility_code IN ('unknown','ready_item','requires_buyer_input','ineligible','error')),
    quote_ref short_text,
    observation_error_code short_text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (product_id,id),
    CHECK (num_nonnulls(observed_unit_cost,observed_currency) IN (0,2)),
    CHECK (availability_code NOT IN ('available','out_of_stock') OR (observed_at IS NOT NULL AND observed_source_revision IS NOT NULL AND observed_account_ref IS NOT NULL AND available_quantity IS NOT NULL AND observed_unit_cost IS NOT NULL AND quote_ref IS NOT NULL)),
    CHECK (availability_code <> 'available' OR (available_quantity > 0 AND eligibility_code='ready_item')),
    CHECK (availability_code <> 'out_of_stock' OR available_quantity=0),
    CHECK (isfinite(observed_at)),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(updated_at))
);

CREATE TABLE product_stock_subscriptions (
id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    product_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    triggered_at timestamptz,
    cancelled_at timestamptz,
    closed_at timestamptz,
    CHECK (triggered_at IS NULL OR triggered_at >= created_at),
    CHECK (cancelled_at IS NULL OR (closed_at IS NOT NULL AND cancelled_at >= created_at)),
    CHECK (closed_at IS NULL OR closed_at >= created_at),
    CHECK (isfinite(created_at)),
    CHECK (isfinite(triggered_at)),
    CHECK (isfinite(cancelled_at)),
    CHECK (isfinite(closed_at))
);

-- Attach only validated constraints after every referenced table exists.
ALTER TABLE brand_channels ADD CONSTRAINT brand_channels_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE users ADD CONSTRAINT users_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE users ADD CONSTRAINT users_flow_order_id_fk FOREIGN KEY (flow_order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE users ADD CONSTRAINT users_flow_product_id_fk FOREIGN KEY (flow_product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE admins ADD CONSTRAINT admins_telegram_verified_by_admin_id_fk FOREIGN KEY (telegram_verified_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE admins ADD CONSTRAINT admins_telegram_verification_audit_id_fk FOREIGN KEY (telegram_verification_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE brands ADD CONSTRAINT brands_customer_support_admin_id_fk FOREIGN KEY (customer_support_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE admin_sessions ADD CONSTRAINT admin_sessions_admin_id_fk FOREIGN KEY (admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE products ADD CONSTRAINT products_id_active_supplier_source_id_fk FOREIGN KEY (id,active_supplier_source_id) REFERENCES product_supplier_sources (product_id,id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE brand_products ADD CONSTRAINT brand_products_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE brand_products ADD CONSTRAINT brand_products_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE inventory_items ADD CONSTRAINT inventory_items_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE inventory_items ADD CONSTRAINT inventory_items_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE inventory_items ADD CONSTRAINT inventory_items_assigned_fulfillment_id_fk FOREIGN KEY (assigned_fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallets ADD CONSTRAINT wallets_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_user_id_fk FOREIGN KEY (user_id) REFERENCES wallets (user_id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_payment_id_fk FOREIGN KEY (payment_id) REFERENCES payments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_admin_id_fk FOREIGN KEY (admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE wallet_entries ADD CONSTRAINT wallet_entries_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_hold_admin_id_fk FOREIGN KEY (hold_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_resolution_admin_id_fk FOREIGN KEY (resolution_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_resolution_audit_event_id_fk FOREIGN KEY (resolution_audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE orders ADD CONSTRAINT orders_resolution_wallet_entry_id_fk FOREIGN KEY (resolution_wallet_entry_id) REFERENCES wallet_entries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE payments ADD CONSTRAINT payments_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE payments ADD CONSTRAINT payments_topup_user_id_fk FOREIGN KEY (topup_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE payments ADD CONSTRAINT payments_review_admin_id_fk FOREIGN KEY (review_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE payments ADD CONSTRAINT payments_compensating_wallet_entry_id_fk FOREIGN KEY (compensating_wallet_entry_id) REFERENCES wallet_entries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE fulfillments ADD CONSTRAINT fulfillments_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE fulfillments ADD CONSTRAINT fulfillments_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE fulfillments ADD CONSTRAINT fulfillments_resolution_admin_id_fk FOREIGN KEY (resolution_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE fulfillments ADD CONSTRAINT fulfillments_order_id_manifest_generation_fk FOREIGN KEY (order_id,manifest_generation) REFERENCES orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_requests ADD CONSTRAINT supplier_requests_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_requests ADD CONSTRAINT supplier_requests_product_supplier_source_id_fk FOREIGN KEY (product_supplier_source_id) REFERENCES product_supplier_sources (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_requests ADD CONSTRAINT supplier_requests_approval_audit_id_fk FOREIGN KEY (approval_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_payment_id_fk FOREIGN KEY (payment_id) REFERENCES payments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_fulfillment_id_fk FOREIGN KEY (fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_campaign_id_fk FOREIGN KEY (campaign_id) REFERENCES campaigns (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_support_message_id_fk FOREIGN KEY (support_message_id) REFERENCES support_messages (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_telegram_delivery_id_fk FOREIGN KEY (telegram_delivery_id) REFERENCES telegram_deliveries (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_product_stock_subscription_id_fk FOREIGN KEY (product_stock_subscription_id) REFERENCES product_stock_subscriptions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_product_supplier_source_id_fk FOREIGN KEY (product_supplier_source_id) REFERENCES product_supplier_sources (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE jobs ADD CONSTRAINT jobs_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE campaigns ADD CONSTRAINT campaigns_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE campaigns ADD CONSTRAINT campaigns_created_by_admin_id_fk FOREIGN KEY (created_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_fulfillment_id_fk FOREIGN KEY (fulfillment_id) REFERENCES fulfillments (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_support_message_id_fk FOREIGN KEY (support_message_id) REFERENCES support_messages (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_product_stock_subscription_id_fk FOREIGN KEY (product_stock_subscription_id) REFERENCES product_stock_subscriptions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_campaign_id_campaign_batch_id_brand_id_fk FOREIGN KEY (campaign_id,campaign_batch_id,brand_id) REFERENCES campaigns (id,batch_id,brand_id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE telegram_deliveries ADD CONSTRAINT telegram_deliveries_order_id_delivery_generation_fk FOREIGN KEY (order_id,delivery_generation) REFERENCES orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE support_tickets ADD CONSTRAINT support_tickets_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE support_tickets ADD CONSTRAINT support_tickets_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE support_tickets ADD CONSTRAINT support_tickets_assigned_admin_id_fk FOREIGN KEY (assigned_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE support_messages ADD CONSTRAINT support_messages_ticket_id_fk FOREIGN KEY (ticket_id) REFERENCES support_tickets (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE support_messages ADD CONSTRAINT support_messages_sender_admin_id_fk FOREIGN KEY (sender_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE legacy_id_map ADD CONSTRAINT legacy_id_map_target_brand_id_fk FOREIGN KEY (target_brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE audit_events ADD CONSTRAINT audit_events_actor_admin_id_fk FOREIGN KEY (actor_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE audit_events ADD CONSTRAINT audit_events_actor_user_id_fk FOREIGN KEY (actor_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE audit_events ADD CONSTRAINT audit_events_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discounts ADD CONSTRAINT discounts_brand_id_fk FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discounts ADD CONSTRAINT discounts_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discounts ADD CONSTRAINT discounts_eligible_user_id_fk FOREIGN KEY (eligible_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discounts ADD CONSTRAINT discounts_source_order_id_fk FOREIGN KEY (source_order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discounts ADD CONSTRAINT discounts_created_by_admin_id_fk FOREIGN KEY (created_by_admin_id) REFERENCES admins (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_uses ADD CONSTRAINT discount_uses_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_uses ADD CONSTRAINT discount_uses_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_uses ADD CONSTRAINT discount_uses_discount_revision_id_fk FOREIGN KEY (discount_revision_id) REFERENCES discount_revisions (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_revisions ADD CONSTRAINT discount_revisions_discount_id_fk FOREIGN KEY (discount_id) REFERENCES discounts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_revisions ADD CONSTRAINT discount_revisions_audit_event_id_fk FOREIGN KEY (audit_event_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_revisions ADD CONSTRAINT discount_revisions_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE discount_revisions ADD CONSTRAINT discount_revisions_eligible_user_id_fk FOREIGN KEY (eligible_user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE bot_activity_events ADD CONSTRAINT bot_activity_events_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE bot_activity_events ADD CONSTRAINT bot_activity_events_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE bot_activity_events ADD CONSTRAINT bot_activity_events_order_id_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE bot_activity_events ADD CONSTRAINT bot_activity_events_support_ticket_id_fk FOREIGN KEY (support_ticket_id) REFERENCES support_tickets (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_request_attempts ADD CONSTRAINT supplier_request_attempts_supplier_request_id_fk FOREIGN KEY (supplier_request_id) REFERENCES supplier_requests (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_request_attempts ADD CONSTRAINT supplier_request_attempts_parent_submit_attempt_id_fk FOREIGN KEY (parent_submit_attempt_id) REFERENCES supplier_request_attempts (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE supplier_request_attempts ADD CONSTRAINT supplier_request_attempts_admin_authorization_audit_id_fk FOREIGN KEY (admin_authorization_audit_id) REFERENCES audit_events (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE product_supplier_sources ADD CONSTRAINT product_supplier_sources_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE product_stock_subscriptions ADD CONSTRAINT product_stock_subscriptions_user_id_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE product_stock_subscriptions ADD CONSTRAINT product_stock_subscriptions_product_id_fk FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;

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



-- Static, typed guards. These are database integrity checks, not authorization APIs.
CREATE FUNCTION require_true(ok boolean, code text) RETURNS void
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF ok IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'schema guard: %', code USING ERRCODE='23514';
    END IF;
END $$;

CREATE FUNCTION member_brand(member_id uuid) RETURNS uuid
LANGUAGE sql STABLE SET search_path=shop,pg_catalog AS $$
    SELECT brand_id FROM users WHERE id=member_id
$$;

CREATE FUNCTION order_brand(order_ref uuid) RETURNS uuid
LANGUAGE sql STABLE SET search_path=shop,pg_catalog AS $$
    SELECT u.brand_id FROM orders o JOIN users u ON u.id=o.user_id WHERE o.id=order_ref
$$;

CREATE FUNCTION payment_member(payment_ref uuid) RETURNS uuid
LANGUAGE sql STABLE SET search_path=shop,pg_catalog AS $$
    SELECT coalesce(p.topup_user_id,o.user_id) FROM payments p LEFT JOIN orders o ON o.id=p.order_id WHERE p.id=payment_ref
$$;

CREATE FUNCTION legacy_allowed(kind text, target uuid, batch uuid DEFAULT NULL) RETURNS boolean
LANGUAGE sql STABLE SET search_path=shop,pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname='shop_legacy_import'
        AND pg_catalog.pg_has_role(current_user,r.oid,'MEMBER'))
    AND EXISTS (SELECT 1 FROM legacy_id_map m WHERE m.target_kind=kind AND m.target_id=target
        AND (batch IS NULL OR m.import_batch=batch))
$$;

CREATE FUNCTION immutable_history() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    RAISE EXCEPTION 'immutable history' USING ERRCODE='23514';
END $$;

CREATE FUNCTION telegram_update_seen(highwater bigint,seen bytea,update_id bigint) RETURNS boolean
LANGUAGE sql IMMUTABLE STRICT SET search_path=shop,pg_catalog AS $$
    SELECT CASE WHEN update_id<0 OR update_id>highwater THEN false WHEN highwater-update_id>=1024 THEN true
        ELSE (get_byte(seen,((highwater-update_id)/8)::integer) & (1<<((highwater-update_id)%8)::integer))<>0 END
$$;

CREATE FUNCTION guard_brands() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE offset_no integer;
BEGIN
    PERFORM require_true((NEW.telegram_update_highwater=-1 AND NEW.telegram_update_window=decode(repeat('00',128),'hex')) OR
        (NEW.telegram_update_highwater>=0 AND (get_byte(NEW.telegram_update_window,0)&1)=1),'telegram_checkpoint_shape');
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.code,NEW.bot_id)=ROW(OLD.id,OLD.code,OLD.bot_id),'brand_identity_immutable');
        PERFORM require_true(NEW.policy_version >= OLD.policy_version,'brand_revision');
        PERFORM require_true(NEW.telegram_update_highwater>=OLD.telegram_update_highwater,'telegram_checkpoint_monotonic');
        FOR offset_no IN 0..1023 LOOP
            IF OLD.telegram_update_highwater>=offset_no AND (get_byte(OLD.telegram_update_window,offset_no/8)&(1<<(offset_no%8)))<>0 THEN
                PERFORM require_true(telegram_update_seen(NEW.telegram_update_highwater,NEW.telegram_update_window,OLD.telegram_update_highwater-offset_no),'telegram_checkpoint_replay');
            END IF;
        END LOOP;
        IF ROW(NEW.bank_number_ciphertext,NEW.bank_holder_ciphertext,NEW.bank_key_id,NEW.bank_card_fingerprint,NEW.bank_changed_at)
            IS DISTINCT FROM ROW(OLD.bank_number_ciphertext,OLD.bank_holder_ciphertext,OLD.bank_key_id,OLD.bank_card_fingerprint,OLD.bank_changed_at) THEN
            PERFORM require_true(NEW.policy_version=OLD.policy_version+1,'card_revision');
        END IF;
    END IF;
    IF NEW.active AND NEW.support_enabled THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM admins WHERE id=NEW.customer_support_admin_id AND disabled_at IS NULL
            AND telegram_user_id IS NOT NULL AND public_contact_handle IS NOT NULL),'active_public_contact');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER brands_guard BEFORE INSERT OR UPDATE ON brands FOR EACH ROW EXECUTE FUNCTION guard_brands();

CREATE FUNCTION guard_users() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.brand_id,NEW.telegram_id,NEW.first_seen_at)=ROW(OLD.id,OLD.brand_id,OLD.telegram_id,OLD.first_seen_at),'membership_identity_immutable');
        PERFORM require_true(NEW.flow_revision >= OLD.flow_revision,'flow_revision');
        IF ROW(NEW.flow_state,NEW.flow_order_id,NEW.flow_product_id,NEW.flow_anchor_message_id,NEW.flow_expires_at)
            IS DISTINCT FROM ROW(OLD.flow_state,OLD.flow_order_id,OLD.flow_product_id,OLD.flow_anchor_message_id,OLD.flow_expires_at) THEN
            PERFORM require_true(NEW.flow_revision=OLD.flow_revision+1,'flow_transition_revision');
        END IF;
    END IF;
    IF NEW.flow_order_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=NEW.flow_order_id AND user_id=NEW.id
            AND (NEW.flow_product_id IS NULL OR product_id=NEW.flow_product_id)),'flow_same_member');
    END IF;
    IF NEW.flow_product_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM brand_products WHERE brand_id=NEW.brand_id AND product_id=NEW.flow_product_id),'flow_listing');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER users_guard BEFORE INSERT OR UPDATE ON users FOR EACH ROW EXECUTE FUNCTION guard_users();

CREATE FUNCTION guard_products() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(NEW.id=OLD.id AND NEW.revision>=OLD.revision,'product_identity_revision');
    END IF;
    IF NEW.active_supplier_source_id IS NOT NULL AND (TG_OP='INSERT' OR NEW.active_supplier_source_id IS DISTINCT FROM OLD.active_supplier_source_id) THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM product_supplier_sources WHERE id=NEW.active_supplier_source_id AND product_id=NEW.id AND enabled),'selected_enabled_source');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER products_guard BEFORE INSERT OR UPDATE ON products FOR EACH ROW EXECUTE FUNCTION guard_products();

CREATE FUNCTION guard_listing() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.brand_id,NEW.product_id)=ROW(OLD.brand_id,OLD.product_id) AND NEW.revision>=OLD.revision,'listing_identity_revision');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER listing_guard BEFORE UPDATE ON brand_products FOR EACH ROW EXECUTE FUNCTION guard_listing();
CREATE TRIGGER listing_no_delete BEFORE DELETE ON brand_products FOR EACH ROW EXECUTE FUNCTION immutable_history();

CREATE FUNCTION guard_orders() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF NEW.product_id IS NULL THEN
        PERFORM require_true(legacy_allowed('order',NEW.id),'historical_order_requires_import_proof');
    ELSE
        PERFORM require_true(EXISTS(SELECT 1 FROM brand_products WHERE brand_id=member_brand(NEW.user_id) AND product_id=NEW.product_id),'order_listing_brand');
    END IF;
    IF NEW.hold_at IS NOT NULL THEN
        PERFORM require_true(NEW.is_preorder AND NEW.payment_status IN ('paid','not_required') AND NEW.fulfillment_status IN ('waiting_stock','requires_admin'),'hold_paid_local_preorder');
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.user_id,NEW.product_id,NEW.quantity,NEW.order_kind,NEW.currency,NEW.payment_method,
            NEW.unit_price_usdt,NEW.unit_price_toman,NEW.fx_rate_snapshot,NEW.fx_source_snapshot,NEW.fx_fetched_at_snapshot,
            NEW.auto_price_toman_snapshot,NEW.pricing_rounding_step_snapshot,NEW.price_calculation_version,NEW.discount_pct_snapshot,NEW.discount_units_snapshot,
            NEW.payment_due_at,NEW.brand_public_name_snapshot,NEW.product_title_snapshot,NEW.product_description_snapshot,NEW.product_icon_snapshot,
            NEW.product_guidance_snapshot,NEW.payment_terms_snapshot,NEW.fulfillment_kind_snapshot,NEW.local_first_snapshot,NEW.is_preorder,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.user_id,OLD.product_id,OLD.quantity,OLD.order_kind,OLD.currency,OLD.payment_method,
            OLD.unit_price_usdt,OLD.unit_price_toman,OLD.fx_rate_snapshot,OLD.fx_source_snapshot,OLD.fx_fetched_at_snapshot,
            OLD.auto_price_toman_snapshot,OLD.pricing_rounding_step_snapshot,OLD.price_calculation_version,OLD.discount_pct_snapshot,OLD.discount_units_snapshot,
            OLD.payment_due_at,OLD.brand_public_name_snapshot,OLD.product_title_snapshot,OLD.product_description_snapshot,OLD.product_icon_snapshot,
            OLD.product_guidance_snapshot,OLD.payment_terms_snapshot,OLD.fulfillment_kind_snapshot,OLD.local_first_snapshot,OLD.is_preorder,OLD.created_at),'order_quote_immutable');
        PERFORM require_true(NEW.revision>=OLD.revision,'order_revision');
        IF OLD.payment_confirmed_at IS NOT NULL THEN
            PERFORM require_true(NEW.payment_confirmed_at IS NOT DISTINCT FROM OLD.payment_confirmed_at,'acceptance_clock_immutable');
        END IF;
        IF OLD.payment_status IN ('paid','not_required') THEN
            PERFORM require_true(NEW.payment_status=OLD.payment_status,'accepted_payment_history');
        END IF;
        IF OLD.delivery_generation IS NOT NULL THEN
            PERFORM require_true(ROW(NEW.delivery_generation,NEW.manifest_asset_ref,NEW.manifest_digest,NEW.manifest_prepared_at)
                IS NOT DISTINCT FROM ROW(OLD.delivery_generation,OLD.manifest_asset_ref,OLD.manifest_digest,OLD.manifest_prepared_at),'manifest_immutable');
        END IF;
        IF OLD.release_authorized_at IS NOT NULL THEN
            PERFORM require_true(NEW.release_authorized_at IS NOT DISTINCT FROM OLD.release_authorized_at,'release_immutable');
        END IF;
        IF OLD.resolution_kind IS NOT NULL THEN
            PERFORM require_true(ROW(NEW.resolution_kind,NEW.resolution_admin_id,NEW.resolution_at,NEW.resolution_audit_event_id,NEW.resolution_wallet_entry_id)
                IS NOT DISTINCT FROM ROW(OLD.resolution_kind,OLD.resolution_admin_id,OLD.resolution_at,OLD.resolution_audit_event_id,OLD.resolution_wallet_entry_id),'resolution_immutable');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER orders_guard BEFORE INSERT OR UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION guard_orders();

CREATE FUNCTION guard_payments() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE o orders;
BEGIN
    IF NEW.order_id IS NOT NULL THEN
        SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
        PERFORM require_true(o.payment_method=NEW.payment_method AND
            NEW.quote_currency=CASE WHEN o.currency='TOMAN' THEN 'TOMAN' ELSE 'USD' END,'payment_order_method');
    END IF;
    IF NEW.compensating_wallet_entry_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM wallet_entries WHERE id=NEW.compensating_wallet_entry_id
            AND user_id=coalesce(NEW.topup_user_id,o.user_id) AND payment_id=NEW.id),'payment_credit_owner');
    END IF;
    IF NEW.legacy_kind IS NOT NULL THEN
        PERFORM require_true(legacy_allowed('payment',NEW.id),'legacy_payment_requires_import_proof');
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.order_id,NEW.topup_user_id,NEW.payment_method,NEW.quote_currency,NEW.amount_due,NEW.idempotency_ref,
            NEW.network_asset,NEW.bank_destination_ciphertext,NEW.bank_destination_key_id,NEW.legacy_kind,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.order_id,OLD.topup_user_id,OLD.payment_method,OLD.quote_currency,OLD.amount_due,OLD.idempotency_ref,
            OLD.network_asset,OLD.bank_destination_ciphertext,OLD.bank_destination_key_id,OLD.legacy_kind,OLD.created_at),'payment_quote_origin_immutable');
        IF OLD.provider_payment_ref IS NOT NULL THEN
            PERFORM require_true(ROW(NEW.provider_code,NEW.provider_payment_ref,NEW.expected_network_amount) IS NOT DISTINCT FROM ROW(OLD.provider_code,OLD.provider_payment_ref,OLD.expected_network_amount),'provider_identity_immutable');
        END IF;
        IF OLD.first_payment_observed_at IS NOT NULL THEN
            PERFORM require_true(NEW.first_payment_observed_at IS NOT DISTINCT FROM OLD.first_payment_observed_at,'first_payment_clock_immutable');
        END IF;
        IF OLD.confirmed_at IS NOT NULL THEN
            PERFORM require_true(NEW.confirmed_at IS NOT DISTINCT FROM OLD.confirmed_at,'payment_confirmation_immutable');
        END IF;
        IF OLD.receipt_asset_ref IS NOT NULL THEN
            PERFORM require_true(NEW.receipt_asset_ref IS NOT DISTINCT FROM OLD.receipt_asset_ref,'receipt_origin_immutable');
        END IF;
        IF OLD.status='settled' THEN PERFORM require_true(NEW.status='settled','settlement_history'); END IF;
        IF OLD.compensating_wallet_entry_id IS NOT NULL THEN
            PERFORM require_true(NEW.compensating_wallet_entry_id IS NOT DISTINCT FROM OLD.compensating_wallet_entry_id,'payment_credit_immutable');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER payments_guard BEFORE INSERT OR UPDATE ON payments FOR EACH ROW EXECUTE FUNCTION guard_payments();

CREATE FUNCTION guard_wallet_entries() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE o orders; r supplier_requests;
BEGIN
    PERFORM require_true(length(btrim(NEW.reason))>0,'ledger_reason');
    IF NEW.order_id IS NOT NULL THEN
        SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
        PERFORM require_true(o.user_id=NEW.user_id,'ledger_order_member');
    END IF;
    IF NEW.payment_id IS NOT NULL THEN
        PERFORM require_true(payment_member(NEW.payment_id)=NEW.user_id,'ledger_payment_member');
        IF NEW.order_id IS NOT NULL THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM payments WHERE id=NEW.payment_id AND order_id=NEW.order_id),'ledger_payment_order');
        ELSE
            PERFORM require_true(EXISTS(SELECT 1 FROM payments WHERE id=NEW.payment_id AND topup_user_id=NEW.user_id),'ledger_topup_origin');
        END IF;
    END IF;
    IF NEW.movement_kind IN ('legacy_history','opening_balance') THEN
        PERFORM require_true(legacy_allowed('wallet_entry',NEW.id,NEW.legacy_import_batch),'ledger_import_proof');
    ELSIF NEW.movement_kind='purchase_debit' THEN
        PERFORM require_true(o.payment_method='wallet','wallet_purchase_method');
    ELSIF NEW.movement_kind='immediate_crypto_shortage_credit' THEN
        PERFORM require_true(o.order_kind='product_purchase' AND NOT o.is_preorder AND o.fulfillment_kind_snapshot='local'
            AND o.payment_method='crypto' AND NOT EXISTS(SELECT 1 FROM supplier_requests WHERE order_id=o.id),'local_only_shortage_credit');
    ELSIF NEW.movement_kind='late_crypto_credit' THEN
        PERFORM require_true(o.payment_method='crypto' AND o.payment_status IN ('expired','cancelled'),'late_crypto_origin');
    ELSIF NEW.movement_kind='supplier_wallet_credit' THEN
        SELECT * INTO STRICT r FROM supplier_requests WHERE id=NEW.supplier_request_id;
        PERFORM require_true(r.order_id=o.id AND r.status IN ('failed','resolved') AND o.payment_status IN ('paid','not_required')
            AND o.payment_method <> 'no_payment_required','supplier_credit_order');
    END IF;
    IF NEW.audit_event_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.audit_event_id AND actor_kind='admin'
            AND actor_admin_id=NEW.admin_id AND brand_id=member_brand(NEW.user_id)
            AND ((NEW.movement_kind='supplier_wallet_credit' AND action='supplier_credit' AND
                ((entity_kind='order' AND entity_id=NEW.order_id) OR (entity_kind='supplier_request' AND entity_id=NEW.supplier_request_id)))
              OR (NEW.movement_kind='admin_adjustment' AND action='wallet_adjustment' AND entity_kind='wallet' AND entity_id=NEW.user_id))),'ledger_audit_origin');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER ledger_guard BEFORE INSERT ON wallet_entries FOR EACH ROW EXECUTE FUNCTION guard_wallet_entries();

CREATE FUNCTION guard_wallet_shape() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        PERFORM require_true(NEW.version=0 AND NEW.balance=0,'wallet_starts_zero');
    ELSE
        PERFORM require_true(NEW.user_id=OLD.user_id AND NEW.version=OLD.version+1,'wallet_version_step');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER wallet_shape BEFORE INSERT OR UPDATE ON wallets FOR EACH ROW EXECUTE FUNCTION guard_wallet_shape();

CREATE FUNCTION check_wallet_posting() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE entry wallet_entries; previous_balance finite_numeric; current_version bigint;
BEGIN
    IF TG_TABLE_NAME='wallets' THEN
        IF NEW.version=0 THEN RETURN NULL; END IF;
        SELECT * INTO entry FROM wallet_entries WHERE user_id=NEW.user_id AND is_posting AND posting_version=NEW.version;
        PERFORM require_true(entry.id IS NOT NULL AND entry.balance_after=NEW.balance,'wallet_requires_matching_posting');
    ELSE
        IF NOT NEW.is_posting THEN RETURN NULL; END IF;
        entry:=NEW;
    END IF;
    SELECT version INTO current_version FROM wallets WHERE user_id=entry.user_id;
    PERFORM require_true(current_version>=entry.posting_version,'posting_committed_version');
    IF entry.posting_version=1 THEN previous_balance:=0;
    ELSE
        SELECT balance_after INTO previous_balance FROM wallet_entries WHERE user_id=entry.user_id AND is_posting AND posting_version=entry.posting_version-1;
    END IF;
    PERFORM require_true(previous_balance IS NOT NULL AND entry.balance_after=previous_balance+entry.signed_amount,'posting_exact_chain');
    IF entry.movement_kind='late_crypto_credit' THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM orders o JOIN payments p ON p.order_id=o.id WHERE o.id=entry.order_id AND o.user_id=entry.user_id
            AND o.payment_status IN ('expired','cancelled') AND p.id=entry.payment_id AND p.status='settled' AND p.compensating_wallet_entry_id=entry.id
            AND p.payment_method='crypto' AND p.quote_currency='USD' AND p.amount_due=entry.signed_amount AND p.amount_received=p.amount_due),'late_credit_retains_terminal_origin');
    ELSIF entry.movement_kind IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=entry.order_id AND user_id=entry.user_id
            AND resolution_kind=entry.movement_kind AND resolution_wallet_entry_id=entry.id),'compensation_requires_order_resolution');
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER wallet_posting_consistency AFTER INSERT OR UPDATE ON wallets DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_wallet_posting();
CREATE CONSTRAINT TRIGGER ledger_posting_consistency AFTER INSERT ON wallet_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_wallet_posting();

CREATE FUNCTION guard_sources() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.product_id,NEW.provider_code,NEW.external_product_id,NEW.external_variant_id,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.product_id,OLD.provider_code,OLD.external_product_id,OLD.external_variant_id,OLD.created_at),'source_identity_immutable');
        PERFORM require_true(NEW.revision>=OLD.revision AND NEW.observation_revision>=OLD.observation_revision,'source_revision');
        IF NEW.enabled IS DISTINCT FROM OLD.enabled THEN PERFORM require_true(NEW.revision=OLD.revision+1,'source_enabled_revision'); END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER sources_guard BEFORE INSERT OR UPDATE ON product_supplier_sources FOR EACH ROW EXECUTE FUNCTION guard_sources();

CREATE FUNCTION guard_supplier_requests() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE o orders;
BEGIN
    SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
    PERFORM require_true(o.order_kind='product_purchase' AND o.fulfillment_kind_snapshot='supplier' AND o.payment_status IN ('paid','not_required')
        AND o.fulfillment_status NOT IN ('cancelled','compensated') AND NEW.requested_quantity<=o.quantity,'supplier_paid_goods_remainder');
    IF NEW.product_supplier_source_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM product_supplier_sources WHERE id=NEW.product_supplier_source_id AND product_id=o.product_id
            AND provider_code=NEW.provider_code AND external_product_id=NEW.external_product_id
            AND external_variant_id IS NOT DISTINCT FROM NEW.external_variant_id),'supplier_source_snapshot');
        IF TG_OP='INSERT' AND NEW.status <> 'blocked' THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM products p JOIN product_supplier_sources s ON s.id=p.active_supplier_source_id
                WHERE p.id=o.product_id AND s.id=NEW.product_supplier_source_id AND s.enabled AND s.revision=NEW.source_revision),'accepted_selected_source');
        END IF;
    END IF;
    IF NEW.approval_audit_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.approval_audit_id AND actor_kind='admin' AND action='approve_cost'
            AND entity_kind='supplier_request' AND entity_id=NEW.id AND brand_id=order_brand(NEW.order_id)
            AND (new_revision=NEW.revision OR (TG_OP='UPDATE' AND NEW.approval_audit_id=OLD.approval_audit_id
                AND NEW.approved_cost_ceiling=OLD.approved_cost_ceiling AND new_revision<=NEW.revision))),'request_cost_approval');
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.order_id,NEW.requested_quantity,NEW.product_supplier_source_id,NEW.source_revision,
            NEW.provider_code,NEW.account_ref,NEW.external_product_id,NEW.external_variant_id,NEW.local_intent_key,NEW.request_sha256,
            NEW.cost_baseline_amount,NEW.cost_currency,NEW.cost_quote_ref,NEW.cost_observed_at,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.order_id,OLD.requested_quantity,OLD.product_supplier_source_id,OLD.source_revision,
            OLD.provider_code,OLD.account_ref,OLD.external_product_id,OLD.external_variant_id,OLD.local_intent_key,OLD.request_sha256,
            OLD.cost_baseline_amount,OLD.cost_currency,OLD.cost_quote_ref,OLD.cost_observed_at,OLD.created_at),'supplier_binding_immutable');
        PERFORM require_true(NEW.revision>=OLD.revision,'request_revision');
        IF OLD.remote_ref IS NOT NULL THEN PERFORM require_true(NEW.remote_ref IS NOT DISTINCT FROM OLD.remote_ref,'purchase_ref_immutable'); END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER supplier_requests_guard BEFORE INSERT OR UPDATE ON supplier_requests FOR EACH ROW EXECUTE FUNCTION guard_supplier_requests();

CREATE FUNCTION lock_supplier_item(request_id uuid, item_key bytea) RETURNS void
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE product_ref uuid;
BEGIN
    SELECT o.product_id INTO STRICT product_ref FROM supplier_requests r JOIN orders o ON o.id=r.order_id WHERE r.id=request_id;
    PERFORM 1 FROM products WHERE id=product_ref FOR UPDATE;
    PERFORM 1 FROM supplier_requests WHERE id=request_id FOR UPDATE;
    IF item_key IS NOT NULL THEN PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(encode(item_key,'hex'),781132)); END IF;
END $$;

CREATE FUNCTION guard_fulfillments() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE o orders; r supplier_requests;
BEGIN
    SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
    PERFORM require_true(o.order_kind='product_purchase' AND NEW.unit_no<=o.quantity,'goods_unit_range');
    IF NEW.status <> 'unfilled' THEN
        PERFORM require_true(o.payment_status IN ('paid','not_required') AND o.fulfillment_status NOT IN ('cancelled','compensated'),'unit_accepted_order');
    END IF;
    IF NEW.supplier_request_id IS NOT NULL THEN
        PERFORM lock_supplier_item(NEW.supplier_request_id,NEW.supplier_item_key);
        SELECT * INTO STRICT r FROM supplier_requests WHERE id=NEW.supplier_request_id;
        PERFORM require_true(r.order_id=NEW.order_id,'unit_request_order');
        PERFORM require_true(NOT EXISTS(SELECT 1 FROM fulfillments WHERE supplier_request_id=r.id AND id<>NEW.id)
            OR (SELECT count(*) FROM fulfillments WHERE supplier_request_id=r.id AND id<>NEW.id)<r.requested_quantity,'supplier_unit_limit');
        IF NEW.supplier_item_key IS NOT NULL THEN
            PERFORM require_true(NOT EXISTS(SELECT 1 FROM inventory_items WHERE supplier_item_key=NEW.supplier_item_key),'customer_bonus_disjoint');
        END IF;
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.order_id,NEW.unit_no,NEW.created_at)=ROW(OLD.id,OLD.order_id,OLD.unit_no,OLD.created_at),'unit_identity_immutable');
        IF OLD.status <> 'unfilled' THEN
            PERFORM require_true(ROW(NEW.kind,NEW.supplier_request_id,NEW.remote_item_ref,NEW.supplier_item_key,NEW.delivered_content,NEW.delivered_content_key_id,NEW.content_kind)
                IS NOT DISTINCT FROM ROW(OLD.kind,OLD.supplier_request_id,OLD.remote_item_ref,OLD.supplier_item_key,OLD.delivered_content,OLD.delivered_content_key_id,OLD.content_kind),'acquisition_immutable');
        END IF;
        IF OLD.status='released' THEN PERFORM require_true(NEW IS NOT DISTINCT FROM OLD,'released_unit_immutable'); END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER fulfillments_guard BEFORE INSERT OR UPDATE ON fulfillments FOR EACH ROW EXECUTE FUNCTION guard_fulfillments();

CREATE FUNCTION guard_inventory() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE o orders; f fulfillments;
BEGIN
    PERFORM 1 FROM products WHERE id=NEW.product_id FOR UPDATE;
    IF NEW.origin_kind='supplier_bonus' THEN
        PERFORM lock_supplier_item(NEW.supplier_request_id,NEW.supplier_item_key);
        PERFORM require_true(EXISTS(SELECT 1 FROM supplier_requests r JOIN orders ord ON ord.id=r.order_id
            WHERE r.id=NEW.supplier_request_id AND ord.product_id=NEW.product_id),'bonus_product');
        IF NEW.supplier_item_key IS NOT NULL THEN
            PERFORM require_true(NOT EXISTS(SELECT 1 FROM fulfillments WHERE supplier_item_key=NEW.supplier_item_key),'bonus_customer_disjoint');
        END IF;
    END IF;
    IF NEW.assigned_fulfillment_id IS NOT NULL THEN
        SELECT * INTO STRICT f FROM fulfillments WHERE id=NEW.assigned_fulfillment_id;
        SELECT * INTO STRICT o FROM orders WHERE id=f.order_id FOR UPDATE;
        PERFORM require_true(o.product_id=NEW.product_id AND f.kind='local' AND o.payment_status IN ('paid','not_required')
            AND o.hold_at IS NULL AND o.fulfillment_status NOT IN ('cancelled','compensated'),'local_allocation_owner');
        PERFORM require_true(NOT EXISTS(SELECT 1 FROM orders head WHERE head.product_id=NEW.product_id AND head.is_preorder
            AND head.payment_status IN ('paid','not_required') AND head.fulfillment_status IN ('waiting_stock','requires_admin')
            AND head.id<>o.id AND (NOT o.is_preorder OR (head.payment_confirmed_at,head.id)<(o.payment_confirmed_at,o.id))),'local_fifo_head');
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.product_id,NEW.origin_kind,NEW.supplier_request_id,NEW.remote_item_ref,NEW.supplier_item_key,NEW.source_fingerprint,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.product_id,OLD.origin_kind,OLD.supplier_request_id,OLD.remote_item_ref,OLD.supplier_item_key,OLD.source_fingerprint,OLD.created_at),'inventory_origin_immutable');
        IF OLD.assigned_fulfillment_id IS NOT NULL THEN
            PERFORM require_true(ROW(NEW.assigned_fulfillment_id,NEW.allocated_at,NEW.credential_ciphertext,NEW.credential_key_id)
                IS NOT DISTINCT FROM ROW(OLD.assigned_fulfillment_id,OLD.allocated_at,OLD.credential_ciphertext,OLD.credential_key_id),'allocated_stock_immutable');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER inventory_guard BEFORE INSERT OR UPDATE ON inventory_items FOR EACH ROW EXECUTE FUNCTION guard_inventory();

CREATE FUNCTION check_order_integrity() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE oid uuid; o orders; local_count bigint; remote_count bigint; ready_count bigint;
BEGIN
    IF TG_TABLE_NAME='orders' THEN oid:=NEW.id;
    ELSIF TG_TABLE_NAME='inventory_items' THEN SELECT order_id INTO oid FROM fulfillments WHERE id=NEW.assigned_fulfillment_id;
    ELSE oid:=NEW.order_id;
    END IF;
    IF oid IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO STRICT o FROM orders WHERE id=oid;
    SELECT count(*) INTO local_count FROM inventory_items i JOIN fulfillments f ON f.id=i.assigned_fulfillment_id WHERE f.order_id=oid;
    SELECT coalesce(sum(requested_quantity),0) INTO remote_count FROM supplier_requests WHERE order_id=oid AND status NOT IN ('failed','resolved');
    PERFORM require_true(local_count+remote_count<=o.quantity,'bound_split_quantity');
    IF o.order_kind='prepaid_units' THEN PERFORM require_true(local_count=0 AND remote_count=0 AND NOT EXISTS(SELECT 1 FROM fulfillments WHERE order_id=oid),'prepaid_no_goods'); END IF;
    IF o.is_preorder THEN PERFORM require_true(local_count IN (0,o.quantity),'preorder_whole_allocation'); END IF;
    IF o.is_preorder AND o.payment_status IN ('paid','not_required') AND local_count=0 AND o.resolution_kind IS NULL THEN
        PERFORM require_true(o.fulfillment_status IN ('waiting_stock','requires_admin'),'unallocated_preorder_stays_in_fifo');
    END IF;
    PERFORM require_true(NOT EXISTS(SELECT 1 FROM fulfillments f WHERE f.order_id=oid AND f.kind='local' AND f.status IN ('acquired','ready','released')
        AND NOT EXISTS(SELECT 1 FROM inventory_items i WHERE i.assigned_fulfillment_id=f.id)),'local_unit_allocation_authority');
    IF o.release_authorized_at IS NOT NULL OR o.fulfillment_status IN ('ready','delivering','completed') THEN
        PERFORM require_true(o.payment_status IN ('paid','not_required') AND o.hold_at IS NULL AND o.review_code IS NULL
            AND (o.resolution_kind IS NULL OR o.resolution_kind='manual_fulfilled'),'release_order_state');
        IF o.order_kind='prepaid_units' THEN
            PERFORM require_true(o.release_authorized_at IS NULL AND EXISTS(SELECT 1 FROM discounts WHERE kind='prepaid_entitlement' AND source_order_id=o.id),'prepaid_entitlement_completion');
        ELSE
            SELECT count(*) INTO ready_count FROM fulfillments WHERE order_id=oid AND status IN ('ready','released');
            PERFORM require_true(ready_count=o.quantity AND o.release_authorized_at IS NOT NULL AND o.delivery_generation IS NOT NULL,'complete_manifest_only');
            PERFORM require_true(NOT EXISTS(SELECT 1 FROM supplier_requests WHERE order_id=oid AND status NOT IN ('succeeded','resolved')),'release_supplier_reconciled');
            PERFORM require_true(NOT EXISTS(SELECT 1 FROM discount_uses WHERE order_id=oid AND status <> 'redeemed'),'release_coupon_redeemed');
        END IF;
    END IF;
    PERFORM require_true(NOT EXISTS(SELECT 1 FROM fulfillments WHERE order_id=oid AND status='released'
        AND (o.release_authorized_at IS NULL OR manifest_generation IS DISTINCT FROM o.delivery_generation)),'unit_release_generation');
    IF o.resolution_wallet_entry_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM wallet_entries WHERE id=o.resolution_wallet_entry_id AND user_id=o.user_id
            AND order_id=o.id AND movement_kind=o.resolution_kind AND is_posting
            AND (o.resolution_kind<>'supplier_wallet_credit' OR (admin_id=o.resolution_admin_id AND audit_event_id=o.resolution_audit_event_id))),'resolution_ledger_link');
    END IF;
    IF TG_TABLE_NAME='discount_uses' THEN
        IF NEW.status='redeemed' THEN
            PERFORM require_true(o.release_authorized_at IS NOT NULL OR (o.order_kind='prepaid_units' AND o.fulfillment_status='completed'),'redemption_requires_complete_obligation');
        END IF;
    END IF;
    IF o.resolution_kind IN ('local_preorder_cancellation','immediate_crypto_shortage_credit','supplier_wallet_credit') THEN
        PERFORM require_true(o.release_authorized_at IS NULL AND NOT EXISTS(SELECT 1 FROM discount_uses WHERE order_id=oid AND status='held'),'resolved_no_live_hold_or_release');
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER order_integrity AFTER INSERT OR UPDATE ON orders DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_order_integrity();
CREATE CONSTRAINT TRIGGER units_order_integrity AFTER INSERT OR UPDATE ON fulfillments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_order_integrity();
CREATE CONSTRAINT TRIGGER inventory_order_integrity AFTER INSERT OR UPDATE ON inventory_items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_order_integrity();
CREATE CONSTRAINT TRIGGER requests_order_integrity AFTER INSERT OR UPDATE ON supplier_requests DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_order_integrity();
CREATE CONSTRAINT TRIGGER claims_order_integrity AFTER INSERT OR UPDATE ON discount_uses DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_order_integrity();

CREATE FUNCTION guard_attempts() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE r supplier_requests; previous_no bigint;
BEGIN
    SELECT * INTO STRICT r FROM supplier_requests WHERE id=NEW.supplier_request_id FOR UPDATE;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(OLD.outcome='in_flight' AND NEW.outcome<>'in_flight','attempt_finalize_once');
        PERFORM require_true(ROW(NEW.id,NEW.supplier_request_id,NEW.attempt_no,NEW.operation,NEW.parent_submit_attempt_id,
            NEW.admin_authorization_audit_id,NEW.purchase_idempotency_key,NEW.max_charge_amount,NEW.started_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.supplier_request_id,OLD.attempt_no,OLD.operation,OLD.parent_submit_attempt_id,
            OLD.admin_authorization_audit_id,OLD.purchase_idempotency_key,OLD.max_charge_amount,OLD.started_at),'attempt_intent_immutable');
        RETURN NEW;
    END IF;
    SELECT coalesce(max(attempt_no),0) INTO previous_no FROM supplier_request_attempts WHERE supplier_request_id=r.id;
    PERFORM require_true(NEW.attempt_no=previous_no+1 AND NEW.outcome='in_flight','attempt_sequence_intent_first');
    IF NEW.operation='submit' THEN
        PERFORM require_true(r.status IN ('pending','in_flight') AND NEW.max_charge_amount<=coalesce(r.approved_cost_ceiling,r.cost_baseline_amount*1.10),'attempt_cost_ceiling');
        PERFORM require_true(NOT EXISTS(SELECT 1 FROM supplier_request_attempts a WHERE a.supplier_request_id=r.id AND a.operation='submit'
            AND ((a.outcome='failed' AND a.error_code='confirmed_no_effect' AND a.evidence_ref IS NOT NULL) OR
                (a.outcome IN ('timeout','unknown') AND EXISTS(SELECT 1 FROM supplier_request_attempts lookup WHERE lookup.parent_submit_attempt_id=a.id
                    AND lookup.operation='status_lookup' AND lookup.outcome='failed' AND lookup.error_code='confirmed_no_effect' AND lookup.evidence_ref IS NOT NULL))) IS NOT TRUE),'unknown_submit_requires_reconciliation');
        IF EXISTS(SELECT 1 FROM supplier_request_attempts WHERE supplier_request_id=r.id AND operation='submit') THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.admin_authorization_audit_id AND actor_kind='admin'
                AND action='retry_supplier' AND entity_kind='supplier_request' AND entity_id=r.id AND new_revision=r.revision
                AND brand_id=order_brand(r.order_id)),'explicit_retry_authorization');
        ELSE PERFORM require_true(NEW.admin_authorization_audit_id IS NULL,'initial_submit_no_retry_audit'); END IF;
    ELSE
        PERFORM require_true(EXISTS(SELECT 1 FROM supplier_request_attempts WHERE id=NEW.parent_submit_attempt_id
            AND supplier_request_id=r.id AND operation='submit' AND attempt_no<NEW.attempt_no),'lookup_original_submit');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER attempts_guard BEFORE INSERT OR UPDATE ON supplier_request_attempts FOR EACH ROW EXECUTE FUNCTION guard_attempts();
CREATE UNIQUE INDEX attempt_retry_authorization_once ON supplier_request_attempts(admin_authorization_audit_id) WHERE admin_authorization_audit_id IS NOT NULL;

CREATE FUNCTION guard_support() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_TABLE_NAME='support_tickets' THEN
        IF NEW.order_id IS NOT NULL THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=NEW.order_id AND user_id=NEW.user_id),'ticket_same_member');
        END IF;
        IF TG_OP='UPDATE' THEN
            PERFORM require_true(ROW(NEW.id,NEW.user_id,NEW.order_id,NEW.created_at) IS NOT DISTINCT FROM ROW(OLD.id,OLD.user_id,OLD.order_id,OLD.created_at),'ticket_origin_immutable');
        END IF;
    ELSE
        IF NEW.sender_kind='customer' AND NEW.sender_telegram_id IS NOT NULL THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM support_tickets t JOIN users u ON u.id=t.user_id WHERE t.id=NEW.ticket_id AND u.telegram_id=NEW.sender_telegram_id),'support_sender_identity');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER ticket_guard BEFORE INSERT OR UPDATE ON support_tickets FOR EACH ROW EXECUTE FUNCTION guard_support();
CREATE TRIGGER support_message_guard BEFORE INSERT ON support_messages FOR EACH ROW EXECUTE FUNCTION guard_support();

CREATE FUNCTION guard_discounts() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE source_order orders;
BEGIN
    IF NEW.eligible_user_id IS NOT NULL THEN PERFORM require_true(member_brand(NEW.eligible_user_id)=NEW.brand_id,'coupon_member_brand'); END IF;
    IF NEW.product_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM brand_products WHERE brand_id=NEW.brand_id AND product_id=NEW.product_id),'coupon_listing');
    END IF;
    IF NEW.kind='prepaid_entitlement' THEN
        SELECT * INTO STRICT source_order FROM orders WHERE id=NEW.source_order_id;
        PERFORM require_true(source_order.order_kind='prepaid_units' AND source_order.payment_status='paid'
            AND order_brand(source_order.id)=NEW.brand_id,'prepaid_paid_source');
        IF TG_OP='INSERT' THEN
            PERFORM require_true(source_order.user_id=NEW.eligible_user_id AND source_order.product_id=NEW.product_id
                AND NEW.percent_off=100 AND NEW.max_total_uses=source_order.quantity,'prepaid_initial_binding');
        END IF;
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.brand_id,NEW.kind,NEW.source_order_id,NEW.issuance_batch_id,NEW.created_by_admin_id,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.brand_id,OLD.kind,OLD.source_order_id,OLD.issuance_batch_id,OLD.created_by_admin_id,OLD.created_at),'coupon_provenance_immutable');
        PERFORM require_true(NEW.revision>=OLD.revision AND NEW.redeemed_count>=OLD.redeemed_count,'coupon_revision_redemption_history');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER discounts_guard BEFORE INSERT OR UPDATE ON discounts FOR EACH ROW EXECUTE FUNCTION guard_discounts();

CREATE FUNCTION guard_discount_uses() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE d discounts; o orders; rev discount_revisions; used_units numeric; held_units numeric;
BEGIN
    SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
    SELECT * INTO STRICT d FROM discounts WHERE id=NEW.discount_id FOR UPDATE;
    SELECT * INTO STRICT rev FROM discount_revisions WHERE id=NEW.discount_revision_id;
    PERFORM require_true(rev.discount_id=d.id AND d.brand_id=member_brand(o.user_id) AND NEW.units_covered=o.quantity
        AND NEW.saved_currency=CASE WHEN o.currency='TOMAN' THEN 'TOMAN' ELSE 'USD' END,'coupon_whole_order_scope');
    IF TG_OP='INSERT' THEN
        PERFORM require_true(rev.revision_no=d.revision AND rev.active AND (rev.product_id IS NULL OR rev.product_id=o.product_id)
            AND (rev.eligible_user_id IS NULL OR rev.eligible_user_id=o.user_id)
            AND (rev.starts_at IS NULL OR rev.starts_at<=CURRENT_TIMESTAMP) AND (rev.ends_at IS NULL OR rev.ends_at>CURRENT_TIMESTAMP),'coupon_grant_eligibility');
        IF d.kind='prepaid_entitlement' THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=d.source_order_id AND user_id=o.user_id AND product_id=o.product_id),'prepaid_paid_owner');
        END IF;
        SELECT coalesce(sum(units_covered),0) INTO held_units FROM discount_uses WHERE discount_id=d.id AND status='held';
        used_units:=greatest(0,d.redeemed_count::numeric+d.usage_adjustment_units::numeric);
        PERFORM require_true(d.max_total_uses IS NULL OR NEW.units_covered<=greatest(0,d.max_total_uses-used_units-held_units),'coupon_total_capacity');
        SELECT coalesce(sum(u.units_covered),0) INTO used_units FROM discount_uses u JOIN orders prior ON prior.id=u.order_id
            WHERE u.discount_id=d.id AND prior.user_id=o.user_id AND u.status IN ('held','redeemed');
        PERFORM require_true(d.max_uses_per_user IS NULL OR NEW.units_covered<=greatest(0,d.max_uses_per_user-used_units),'coupon_member_capacity');
        PERFORM require_true(NEW.status IN ('held','redeemed'),'new_claim_live');
    ELSE
        PERFORM require_true(ROW(NEW.discount_id,NEW.order_id,NEW.discount_revision_id,NEW.units_covered,NEW.saved_amount,NEW.saved_currency,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.discount_id,OLD.order_id,OLD.discount_revision_id,OLD.units_covered,OLD.saved_amount,OLD.saved_currency,OLD.created_at),'claim_grant_immutable');
        PERFORM require_true(NEW.status=OLD.status OR (OLD.status='held' AND NEW.status IN ('redeemed','released')),'claim_terminal_once');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER claim_guard BEFORE INSERT OR UPDATE ON discount_uses FOR EACH ROW EXECUTE FUNCTION guard_discount_uses();

CREATE FUNCTION guard_subscription_activity() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_OP='DELETE' THEN
        IF TG_TABLE_NAME='bot_activity_events' THEN
            PERFORM require_true(OLD.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours','activity_retention_cutoff');
        ELSE
            PERFORM require_true(OLD.closed_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours','subscription_retention_cutoff');
        END IF;
        RETURN OLD;
    END IF;
    IF NEW.product_id IS NOT NULL THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM brand_products WHERE brand_id=member_brand(NEW.user_id) AND product_id=NEW.product_id),'context_listing');
    END IF;
    IF TG_TABLE_NAME='bot_activity_events' THEN
        IF NEW.order_id IS NOT NULL THEN PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=NEW.order_id AND user_id=NEW.user_id),'activity_order_member'); END IF;
        IF NEW.support_ticket_id IS NOT NULL THEN PERFORM require_true(EXISTS(SELECT 1 FROM support_tickets WHERE id=NEW.support_ticket_id AND user_id=NEW.user_id),'activity_ticket_member'); END IF;
    ELSIF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.user_id,NEW.product_id,NEW.created_at)=ROW(OLD.id,OLD.user_id,OLD.product_id,OLD.created_at),'consent_origin_immutable');
        IF OLD.triggered_at IS NOT NULL THEN PERFORM require_true(NEW.triggered_at IS NOT DISTINCT FROM OLD.triggered_at,'consent_enqueue_clock'); END IF;
        IF OLD.cancelled_at IS NOT NULL THEN PERFORM require_true(NEW.cancelled_at IS NOT DISTINCT FROM OLD.cancelled_at,'consent_cancel_clock'); END IF;
        IF OLD.closed_at IS NOT NULL THEN PERFORM require_true(NEW IS NOT DISTINCT FROM OLD,'consent_terminal_immutable'); END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER subscription_guard BEFORE INSERT OR UPDATE OR DELETE ON product_stock_subscriptions FOR EACH ROW EXECUTE FUNCTION guard_subscription_activity();
CREATE TRIGGER activity_guard BEFORE INSERT OR DELETE ON bot_activity_events FOR EACH ROW EXECUTE FUNCTION guard_subscription_activity();
CREATE TRIGGER activity_no_update BEFORE UPDATE ON bot_activity_events FOR EACH ROW EXECUTE FUNCTION immutable_history();

CREATE FUNCTION guard_deliveries() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE origin_count integer; member_id uuid; origin_brand uuid; o orders; sub product_stock_subscriptions;
BEGIN
    origin_count:=num_nonnulls(NEW.campaign_id,NEW.discount_id,NEW.order_id,NEW.support_message_id,NEW.product_stock_subscription_id,NEW.supplier_request_id);
    PERFORM require_true(origin_count=1,'delivery_typed_origin');
    IF NEW.recipient_kind='customer_dm' THEN
        PERFORM require_true(member_brand(NEW.user_id)=NEW.brand_id,'delivery_member_brand');
    END IF;
    IF NEW.order_id IS NOT NULL THEN
        SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id;
        PERFORM require_true(NEW.purpose IN ('order_status','order_credentials') AND NEW.user_id=o.user_id AND NEW.brand_id=order_brand(o.id),'delivery_order_owner');
        IF NEW.fulfillment_id IS NOT NULL THEN PERFORM require_true(EXISTS(SELECT 1 FROM fulfillments WHERE id=NEW.fulfillment_id AND order_id=o.id),'delivery_unit_order'); END IF;
        IF NEW.purpose='order_credentials' THEN
            PERFORM require_true(o.release_authorized_at IS NOT NULL AND NEW.delivery_generation=o.delivery_generation
                AND o.fulfillment_status IN ('ready','delivering','completed') AND o.hold_at IS NULL AND o.review_code IS NULL,'delivery_complete_manifest');
            IF NEW.action='edit' THEN
                PERFORM require_true(EXISTS(SELECT 1 FROM telegram_deliveries anchor WHERE anchor.order_id=o.id AND anchor.user_id=o.user_id
                    AND anchor.brand_id=NEW.brand_id AND anchor.purpose='order_status' AND anchor.status='succeeded'
                    AND anchor.telegram_message_id=NEW.target_message_id),'validated_saved_order_anchor');
            ELSE
                PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.audit_event_id AND actor_kind='admin' AND action='delivery_recovery'
                    AND entity_kind='order' AND entity_id=o.id AND brand_id=NEW.brand_id),'credential_send_explicit_recovery');
            END IF;
        END IF;
    ELSE
        PERFORM require_true(NEW.fulfillment_id IS NULL AND (NEW.campaign_batch_id IS NULL OR NEW.campaign_id IS NOT NULL),'delivery_auxiliary_origin');
        IF NEW.campaign_id IS NOT NULL THEN
            PERFORM require_true(NEW.purpose='campaign' AND NEW.delivery_generation IS NULL,'campaign_purpose');
        ELSIF NEW.discount_id IS NOT NULL THEN
            SELECT brand_id,eligible_user_id INTO origin_brand,member_id FROM discounts WHERE id=NEW.discount_id;
            PERFORM require_true(NEW.purpose='discount_notice' AND origin_brand=NEW.brand_id AND (member_id IS NULL OR member_id=NEW.user_id),'coupon_notice_scope');
        ELSIF NEW.support_message_id IS NOT NULL THEN
            SELECT t.user_id INTO member_id FROM support_messages m JOIN support_tickets t ON t.id=m.ticket_id WHERE m.id=NEW.support_message_id;
            PERFORM require_true(NEW.purpose='support_notice' AND member_id=NEW.user_id,'support_notice_member');
        ELSIF NEW.product_stock_subscription_id IS NOT NULL THEN
            SELECT * INTO STRICT sub FROM product_stock_subscriptions WHERE id=NEW.product_stock_subscription_id;
            PERFORM require_true(NEW.purpose='restock_notice' AND sub.user_id=NEW.user_id,'restock_notice_member');
            IF NEW.status IN ('pending','leased','retry') THEN PERFORM require_true(sub.cancelled_at IS NULL AND sub.closed_at IS NULL,'restock_active_consent'); END IF;
        ELSE
            PERFORM require_true(NEW.recipient_kind='staff_chat' AND NEW.brand_id=(SELECT order_brand(order_id) FROM supplier_requests WHERE id=NEW.supplier_request_id)
                AND NEW.audit_event_id IS NOT NULL AND NEW.delivery_generation IS NOT NULL,'staff_case_origin');
        END IF;
    END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.brand_id,NEW.recipient_kind,NEW.user_id,NEW.staff_chat_id,NEW.purpose,NEW.action,NEW.target_message_id,
            NEW.part_no,NEW.delivery_generation,NEW.campaign_id,NEW.campaign_batch_id,NEW.discount_id,NEW.order_id,NEW.fulfillment_id,
            NEW.support_message_id,NEW.product_stock_subscription_id,NEW.supplier_request_id,NEW.dedupe_key,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.brand_id,OLD.recipient_kind,OLD.user_id,OLD.staff_chat_id,OLD.purpose,OLD.action,OLD.target_message_id,
            OLD.part_no,OLD.delivery_generation,OLD.campaign_id,OLD.campaign_batch_id,OLD.discount_id,OLD.order_id,OLD.fulfillment_id,
            OLD.support_message_id,OLD.product_stock_subscription_id,OLD.supplier_request_id,OLD.dedupe_key,OLD.created_at),'delivery_intent_immutable');
        IF OLD.status='succeeded' THEN PERFORM require_true(NEW.status='succeeded' AND NEW.telegram_message_id IS NOT DISTINCT FROM OLD.telegram_message_id,'known_send_success'); END IF;
        IF OLD.status='uncertain' AND NEW.purpose='order_credentials' AND NEW.status IN ('pending','retry','leased') THEN
            PERFORM require_true(NEW.audit_event_id IS DISTINCT FROM OLD.audit_event_id AND EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.audit_event_id
                AND actor_kind='admin' AND action='delivery_recovery' AND entity_kind='order' AND entity_id=NEW.order_id),'uncertain_send_requires_recovery');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER deliveries_guard BEFORE INSERT OR UPDATE ON telegram_deliveries FOR EACH ROW EXECUTE FUNCTION guard_deliveries();

CREATE FUNCTION guard_jobs() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE origins integer; expected_brand uuid; supplier_kind boolean;
BEGIN
    origins:=num_nonnulls(NEW.payment_id,NEW.order_id,NEW.fulfillment_id,NEW.campaign_id,NEW.discount_id,NEW.support_message_id,
        NEW.telegram_delivery_id,NEW.product_stock_subscription_id,NEW.supplier_request_id,NEW.product_id,NEW.product_supplier_source_id);
    supplier_kind:=NEW.job_kind IN ('submit_supplier','reconcile_supplier','export_supplier');
    IF NEW.job_kind='refresh_fx' THEN
        PERFORM require_true(origins=0 AND NEW.brand_id IS NOT NULL,'fx_brand_origin');
    ELSIF NEW.job_kind IN ('activity_cleanup','subscription_cleanup') THEN
        PERFORM require_true(origins=0 AND NEW.brand_id IS NULL,'maintenance_origin');
    ELSE
        PERFORM require_true(origins=1,'job_single_typed_origin');
        CASE NEW.job_kind
        WHEN 'expire_order_payment','prepare_manifest','issue_prepaid_coupon' THEN
            expected_brand:=order_brand(NEW.order_id);
            PERFORM require_true(NEW.order_id IS NOT NULL,'job_order_origin');
            IF NEW.job_kind <> 'expire_order_payment' THEN
                PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=NEW.order_id AND order_kind=
                    CASE WHEN NEW.job_kind='prepare_manifest' THEN 'product_purchase' ELSE 'prepaid_units' END),'job_order_kind');
            END IF;
        WHEN 'verify_payment','settle_wallet_topup' THEN
            expected_brand:=member_brand(payment_member(NEW.payment_id));
            PERFORM require_true(NEW.payment_id IS NOT NULL,'job_payment_origin');
            IF NEW.job_kind='settle_wallet_topup' THEN PERFORM require_true(EXISTS(SELECT 1 FROM payments WHERE id=NEW.payment_id AND topup_user_id IS NOT NULL),'topup_job_origin'); END IF;
        WHEN 'submit_supplier','reconcile_supplier','export_supplier' THEN
            expected_brand:=(SELECT order_brand(order_id) FROM supplier_requests WHERE id=NEW.supplier_request_id);
            PERFORM require_true(NEW.supplier_request_id IS NOT NULL,'job_supplier_origin');
            IF NEW.job_kind='submit_supplier' AND (TG_OP='INSERT' OR NEW.status IN ('pending','leased','retry')) THEN PERFORM require_true(EXISTS(SELECT 1 FROM supplier_requests WHERE id=NEW.supplier_request_id AND status='pending'),'job_runnable_request'); END IF;
        WHEN 'send_telegram' THEN expected_brand:=(SELECT brand_id FROM telegram_deliveries WHERE id=NEW.telegram_delivery_id);
        WHEN 'campaign_materialize' THEN expected_brand:=(SELECT brand_id FROM campaigns WHERE id=NEW.campaign_id);
        WHEN 'coupon_notice' THEN expected_brand:=(SELECT brand_id FROM discounts WHERE id=NEW.discount_id);
        WHEN 'support_notice' THEN expected_brand:=(SELECT member_brand(t.user_id) FROM support_messages m JOIN support_tickets t ON t.id=m.ticket_id WHERE m.id=NEW.support_message_id);
        WHEN 'drain_local_preorders','evaluate_restock' THEN PERFORM require_true(NEW.product_id IS NOT NULL AND NEW.brand_id IS NULL,'shared_product_origin');
        WHEN 'refresh_catalog_source' THEN PERFORM require_true(NEW.product_supplier_source_id IS NOT NULL AND NEW.brand_id IS NULL,'shared_source_origin');
        ELSE PERFORM require_true(false,'unknown_job_kind');
        END CASE;
        IF NEW.job_kind NOT IN ('drain_local_preorders','evaluate_restock','refresh_catalog_source','refresh_fx') THEN
            PERFORM require_true(expected_brand IS NOT NULL AND NEW.brand_id=expected_brand,'job_origin_brand');
        END IF;
    END IF;
    IF supplier_kind THEN
        PERFORM require_true(num_nonnulls(NEW.audit_event_id,NEW.origin_revision) IN (0,2),'job_retry_tuple');
        IF NEW.audit_event_id IS NOT NULL THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.audit_event_id AND actor_kind='admin' AND action='retry_supplier'
                AND entity_kind='supplier_request' AND entity_id=NEW.supplier_request_id AND new_revision=NEW.origin_revision
                AND brand_id=NEW.brand_id),'job_retry_audit');
        END IF;
    ELSE PERFORM require_true(NEW.audit_event_id IS NULL AND NEW.origin_revision IS NULL,'job_no_extra_authority'); END IF;
    IF TG_OP='UPDATE' THEN
        PERFORM require_true(ROW(NEW.id,NEW.brand_id,NEW.job_kind,NEW.payment_id,NEW.order_id,NEW.fulfillment_id,NEW.campaign_id,NEW.discount_id,
            NEW.support_message_id,NEW.telegram_delivery_id,NEW.product_stock_subscription_id,NEW.supplier_request_id,NEW.product_id,
            NEW.product_supplier_source_id,NEW.audit_event_id,NEW.origin_revision,NEW.dedupe_key,NEW.created_at)
            IS NOT DISTINCT FROM ROW(OLD.id,OLD.brand_id,OLD.job_kind,OLD.payment_id,OLD.order_id,OLD.fulfillment_id,OLD.campaign_id,OLD.discount_id,
            OLD.support_message_id,OLD.telegram_delivery_id,OLD.product_stock_subscription_id,OLD.supplier_request_id,OLD.product_id,
            OLD.product_supplier_source_id,OLD.audit_event_id,OLD.origin_revision,OLD.dedupe_key,OLD.created_at),'job_intent_immutable');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER jobs_guard BEFORE INSERT OR UPDATE ON jobs FOR EACH ROW EXECUTE FUNCTION guard_jobs();

CREATE FUNCTION guard_coupon_revision() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE d discounts; held numeric;
BEGIN
    SELECT * INTO STRICT d FROM discounts WHERE id=NEW.discount_id FOR UPDATE;
    SELECT coalesce(sum(units_covered),0) INTO held FROM discount_uses WHERE discount_id=d.id AND status='held';
    PERFORM require_true(ROW(NEW.revision_no,NEW.percent_off,NEW.max_total_uses,NEW.max_uses_per_user,NEW.usage_adjustment_units,
        NEW.redeemed_count_at_revision,NEW.starts_at,NEW.ends_at,NEW.active,NEW.product_id,NEW.eligible_user_id)
        IS NOT DISTINCT FROM ROW(d.revision,d.percent_off,d.max_total_uses,d.max_uses_per_user,d.usage_adjustment_units,
        d.redeemed_count,d.starts_at,d.ends_at,d.active,d.product_id,d.eligible_user_id),'revision_post_edit_snapshot');
    PERFORM require_true(NEW.held_units_at_revision=held,'revision_held_context');
    PERFORM require_true(EXISTS(SELECT 1 FROM audit_events WHERE id=NEW.audit_event_id AND actor_kind='admin' AND (NEW.revision_no>0 OR d.created_by_admin_id IS NOT NULL) AND entity_kind='discount'
        AND entity_id=d.id AND brand_id=d.brand_id AND action IN ('coupon_issue','coupon_edit') AND new_revision=NEW.revision_no)
        OR (NEW.revision_no=0 AND d.revision=0 AND d.created_by_admin_id IS NULL AND d.kind='prepaid_entitlement'
            AND d.percent_off=100 AND d.starts_at IS NULL AND d.ends_at IS NULL AND d.active AND d.redeemed_count=0 AND d.usage_adjustment_units=0
            AND EXISTS(SELECT 1 FROM audit_events a JOIN orders o ON o.id=d.source_order_id WHERE a.id=NEW.audit_event_id AND a.actor_kind='system'
                AND a.action='coupon_issue' AND a.entity_kind='discount' AND a.entity_id=d.id AND a.brand_id=d.brand_id AND a.new_revision=0
                AND a.correlation_id=o.id AND a.reason='paid_source_entitlement' AND o.order_kind='prepaid_units' AND o.payment_status='paid'
                AND o.user_id=d.eligible_user_id AND o.product_id=d.product_id AND d.max_total_uses=o.quantity
                AND (EXISTS(SELECT 1 FROM wallet_entries w WHERE w.order_id=o.id AND w.user_id=o.user_id AND w.movement_kind='purchase_debit' AND w.is_posting AND w.signed_amount<0)
                    OR EXISTS(SELECT 1 FROM payments p WHERE p.order_id=o.id AND p.status='settled' AND p.confirmed_at IS NOT NULL AND p.amount_received>=p.amount_due)))),'revision_audit');
    RETURN NEW;
END $$;
CREATE TRIGGER coupon_revision_guard BEFORE INSERT ON discount_revisions FOR EACH ROW EXECUTE FUNCTION guard_coupon_revision();

CREATE FUNCTION check_coupon_history() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE did uuid; d discounts; actual_redeemed numeric; edited boolean;
BEGIN
    IF TG_TABLE_NAME='discounts' THEN
        did:=NEW.id;
        IF TG_OP='INSERT' THEN edited:=true;
        ELSE
            edited:=ROW(NEW.percent_off,NEW.max_total_uses,NEW.max_uses_per_user,NEW.usage_adjustment_units,NEW.starts_at,NEW.ends_at,NEW.active,NEW.product_id,NEW.eligible_user_id)
                IS DISTINCT FROM ROW(OLD.percent_off,OLD.max_total_uses,OLD.max_uses_per_user,OLD.usage_adjustment_units,OLD.starts_at,OLD.ends_at,OLD.active,OLD.product_id,OLD.eligible_user_id);
            PERFORM require_true(NEW.revision=OLD.revision+CASE WHEN edited THEN 1 ELSE 0 END,'coupon_edit_revision_step');
        END IF;
        IF edited THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM discount_revisions WHERE discount_id=did AND revision_no=NEW.revision),'coupon_revision_required');
        END IF;
    ELSE did:=NEW.discount_id;
    END IF;
    SELECT * INTO STRICT d FROM discounts WHERE id=did;
    SELECT coalesce(sum(units_covered),0) INTO actual_redeemed FROM discount_uses WHERE discount_id=did AND status='redeemed';
    PERFORM require_true(d.redeemed_count=actual_redeemed OR legacy_allowed('discount',d.id),'actual_redemption_counter');
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER coupon_history AFTER INSERT OR UPDATE ON discounts DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_coupon_history();
CREATE CONSTRAINT TRIGGER claim_history AFTER INSERT OR UPDATE ON discount_uses DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_coupon_history();

CREATE FUNCTION guard_campaigns() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    PERFORM require_true(ROW(NEW.id,NEW.batch_id,NEW.brand_id,NEW.language,NEW.created_at,NEW.created_by_admin_id)
        IS NOT DISTINCT FROM ROW(OLD.id,OLD.batch_id,OLD.brand_id,OLD.language,OLD.created_at,OLD.created_by_admin_id),'campaign_launch_immutable');
    RETURN NEW;
END $$;
CREATE TRIGGER campaign_guard BEFORE UPDATE ON campaigns FOR EACH ROW EXECUTE FUNCTION guard_campaigns();

CREATE FUNCTION guard_admin_sessions() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    PERFORM require_true(ROW(NEW.id,NEW.admin_id,NEW.token_hash,NEW.auth_version_at_issue,NEW.issued_at,NEW.expires_at)
        IS NOT DISTINCT FROM ROW(OLD.id,OLD.admin_id,OLD.token_hash,OLD.auth_version_at_issue,OLD.issued_at,OLD.expires_at),'session_origin_immutable');
    IF OLD.revoked_at IS NOT NULL THEN PERFORM require_true(NEW.revoked_at IS NOT DISTINCT FROM OLD.revoked_at,'session_revocation_immutable'); END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER admin_session_guard BEFORE UPDATE ON admin_sessions FOR EACH ROW EXECUTE FUNCTION guard_admin_sessions();

CREATE FUNCTION guard_retained_work_delete() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE subscription_id uuid;
BEGIN
    PERFORM require_true(OLD.status IN ('succeeded','failed','cancelled'),'retention_terminal_work');
    IF TG_TABLE_NAME='jobs' THEN
        SELECT product_stock_subscription_id INTO subscription_id FROM telegram_deliveries WHERE id=OLD.telegram_delivery_id AND purpose='restock_notice'
            AND status IN ('succeeded','failed','cancelled');
    ELSE
        PERFORM require_true(OLD.purpose='restock_notice','retention_restock_only');
        subscription_id:=OLD.product_stock_subscription_id;
    END IF;
    PERFORM require_true(EXISTS(SELECT 1 FROM product_stock_subscriptions WHERE id=subscription_id
        AND closed_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'),'retention_closed_consent');
    RETURN OLD;
END $$;
CREATE TRIGGER jobs_retention_delete BEFORE DELETE ON jobs FOR EACH ROW EXECUTE FUNCTION guard_retained_work_delete();
CREATE TRIGGER delivery_retention_delete BEFORE DELETE ON telegram_deliveries FOR EACH ROW EXECUTE FUNCTION guard_retained_work_delete();


CREATE FUNCTION valid_entity(kind text,target uuid,scope uuid) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path=shop,pg_catalog AS $$
BEGIN
    CASE kind
    WHEN 'admin' THEN RETURN EXISTS(SELECT 1 FROM admins WHERE id=target);
    WHEN 'product' THEN RETURN EXISTS(SELECT 1 FROM products WHERE id=target);
    WHEN 'inventory_item' THEN RETURN EXISTS(SELECT 1 FROM inventory_items WHERE id=target);
    WHEN 'product_supplier_source' THEN RETURN EXISTS(SELECT 1 FROM product_supplier_sources WHERE id=target);
    WHEN 'brand' THEN RETURN EXISTS(SELECT 1 FROM brands WHERE id=target AND (scope IS NULL OR id=scope));
    WHEN 'brand_product' THEN RETURN EXISTS(SELECT 1 FROM brand_products WHERE product_id=target AND brand_id=scope);
    WHEN 'user' THEN RETURN EXISTS(SELECT 1 FROM users WHERE id=target AND member_brand(id)=scope);
    WHEN 'wallet' THEN RETURN EXISTS(SELECT 1 FROM wallets WHERE user_id=target AND member_brand(user_id)=scope);
    WHEN 'wallet_entry' THEN RETURN EXISTS(SELECT 1 FROM wallet_entries WHERE id=target AND member_brand(user_id)=scope);
    WHEN 'order' THEN RETURN EXISTS(SELECT 1 FROM orders WHERE id=target AND member_brand(user_id)=scope);
    WHEN 'payment' THEN RETURN EXISTS(SELECT 1 FROM payments WHERE id=target AND member_brand(payment_member(id))=scope);
    WHEN 'fulfillment' THEN RETURN EXISTS(SELECT 1 FROM fulfillments WHERE id=target AND order_brand(order_id)=scope);
    WHEN 'supplier_request' THEN RETURN EXISTS(SELECT 1 FROM supplier_requests WHERE id=target AND order_brand(order_id)=scope);
    WHEN 'campaign' THEN RETURN EXISTS(SELECT 1 FROM campaigns WHERE id=target AND brand_id=scope);
    WHEN 'telegram_delivery' THEN RETURN EXISTS(SELECT 1 FROM telegram_deliveries WHERE id=target AND brand_id=scope);
    WHEN 'support_ticket' THEN RETURN EXISTS(SELECT 1 FROM support_tickets WHERE id=target AND member_brand(user_id)=scope);
    WHEN 'support_message' THEN RETURN EXISTS(SELECT 1 FROM support_messages WHERE id=target AND (SELECT member_brand(t.user_id) FROM support_tickets t WHERE t.id=ticket_id)=scope);
    WHEN 'discount' THEN RETURN EXISTS(SELECT 1 FROM discounts WHERE id=target AND brand_id=scope);
    WHEN 'discount_revision' THEN RETURN EXISTS(SELECT 1 FROM discount_revisions WHERE id=target AND (SELECT d.brand_id FROM discounts d WHERE d.id=discount_id)=scope);
    WHEN 'supplier_request_attempt' THEN RETURN EXISTS(SELECT 1 FROM supplier_request_attempts WHERE id=target AND (SELECT order_brand(r.order_id) FROM supplier_requests r WHERE r.id=supplier_request_id)=scope);
    WHEN 'product_stock_subscription' THEN RETURN EXISTS(SELECT 1 FROM product_stock_subscriptions WHERE id=target AND member_brand(user_id)=scope);
    WHEN 'bot_activity_event' THEN RETURN EXISTS(SELECT 1 FROM bot_activity_events WHERE id=target AND member_brand(user_id)=scope);
    ELSE RETURN false;
    END CASE;
END $$;

CREATE FUNCTION check_reference_registry() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
BEGIN
    IF TG_TABLE_NAME='legacy_id_map' THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname='shop_legacy_import'
            AND pg_catalog.pg_has_role(current_user,r.oid,'MEMBER')),'restricted_legacy_import_role');
        PERFORM require_true(valid_entity(NEW.target_kind,NEW.target_id,NEW.target_brand_id),'legacy_static_target');
        PERFORM require_true(NEW.source_key_kind <> 'public_order_reference' OR NEW.target_kind='order','legacy_order_alias_only');
    ELSE
        PERFORM require_true(length(btrim(NEW.reason))>0,'audit_reason');
        IF NEW.entity_kind='platform' THEN
            PERFORM require_true(NEW.actor_kind IN ('system','admin') AND NEW.brand_id IS NULL AND NEW.action IN ('maintenance','legacy_import'),'platform_audit_scope');
        ELSE PERFORM require_true(valid_entity(NEW.entity_kind,NEW.entity_id,NEW.brand_id),'audit_static_target'); END IF;
        IF NEW.actor_kind='customer' THEN PERFORM require_true(member_brand(NEW.actor_user_id)=NEW.brand_id,'audit_customer_brand'); END IF;
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER audit_reference_guard AFTER INSERT ON audit_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_reference_registry();
CREATE CONSTRAINT TRIGGER legacy_reference_guard AFTER INSERT ON legacy_id_map DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION check_reference_registry();

CREATE TRIGGER wallet_entries_immutable BEFORE UPDATE OR DELETE ON wallet_entries FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER audit_events_immutable BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discount_revisions_immutable BEFORE UPDATE OR DELETE ON discount_revisions FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER support_messages_immutable BEFORE UPDATE OR DELETE ON support_messages FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER legacy_id_map_immutable BEFORE UPDATE OR DELETE ON legacy_id_map FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER orders_no_delete BEFORE DELETE ON orders FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER payments_no_delete BEFORE DELETE ON payments FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER fulfillments_no_delete BEFORE DELETE ON fulfillments FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER supplier_requests_no_delete BEFORE DELETE ON supplier_requests FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER supplier_request_attempts_no_delete BEFORE DELETE ON supplier_request_attempts FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discount_uses_no_delete BEFORE DELETE ON discount_uses FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discounts_no_delete BEFORE DELETE ON discounts FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER wallets_no_delete BEFORE DELETE ON wallets FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER inventory_items_no_delete BEFORE DELETE ON inventory_items FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER support_tickets_no_delete BEFORE DELETE ON support_tickets FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER campaigns_no_delete BEFORE DELETE ON campaigns FOR EACH ROW EXECUTE FUNCTION immutable_history();
CREATE TRIGGER brands_no_truncate BEFORE TRUNCATE ON brands FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER brand_channels_no_truncate BEFORE TRUNCATE ON brand_channels FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER users_no_truncate BEFORE TRUNCATE ON users FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER admins_no_truncate BEFORE TRUNCATE ON admins FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER admin_sessions_no_truncate BEFORE TRUNCATE ON admin_sessions FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER products_no_truncate BEFORE TRUNCATE ON products FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER brand_products_no_truncate BEFORE TRUNCATE ON brand_products FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER inventory_items_no_truncate BEFORE TRUNCATE ON inventory_items FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER wallets_no_truncate BEFORE TRUNCATE ON wallets FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER wallet_entries_no_truncate BEFORE TRUNCATE ON wallet_entries FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER orders_no_truncate BEFORE TRUNCATE ON orders FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER payments_no_truncate BEFORE TRUNCATE ON payments FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER fulfillments_no_truncate BEFORE TRUNCATE ON fulfillments FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER supplier_requests_no_truncate BEFORE TRUNCATE ON supplier_requests FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER jobs_no_truncate BEFORE TRUNCATE ON jobs FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER campaigns_no_truncate BEFORE TRUNCATE ON campaigns FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER telegram_deliveries_no_truncate BEFORE TRUNCATE ON telegram_deliveries FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER support_tickets_no_truncate BEFORE TRUNCATE ON support_tickets FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER support_messages_no_truncate BEFORE TRUNCATE ON support_messages FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER legacy_id_map_no_truncate BEFORE TRUNCATE ON legacy_id_map FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discounts_no_truncate BEFORE TRUNCATE ON discounts FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discount_uses_no_truncate BEFORE TRUNCATE ON discount_uses FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER discount_revisions_no_truncate BEFORE TRUNCATE ON discount_revisions FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER bot_activity_events_no_truncate BEFORE TRUNCATE ON bot_activity_events FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER supplier_request_attempts_no_truncate BEFORE TRUNCATE ON supplier_request_attempts FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER product_supplier_sources_no_truncate BEFORE TRUNCATE ON product_supplier_sources FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();
CREATE TRIGGER product_stock_subscriptions_no_truncate BEFORE TRUNCATE ON product_stock_subscriptions FOR EACH STATEMENT EXECUTE FUNCTION immutable_history();

REVOKE ALL ON ALL TABLES IN SCHEMA shop FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA shop FROM PUBLIC;
