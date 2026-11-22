SET LOCAL search_path=shop,pg_catalog;
REVOKE ALL ON SCHEMA shop FROM PUBLIC;
ALTER TABLE shop.fulfillments ADD CONSTRAINT fulfillments_order_id_manifest_generation_fk FOREIGN KEY (order_id,manifest_generation) REFERENCES shop.orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ADD CONSTRAINT telegram_deliveries_order_id_delivery_generation_fk FOREIGN KEY (order_id,delivery_generation) REFERENCES shop.orders (id,delivery_generation) ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.brands ALTER CONSTRAINT brands_customer_support_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.brand_channels ALTER CONSTRAINT brand_channels_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.users ALTER CONSTRAINT users_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.users ALTER CONSTRAINT users_flow_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.users ALTER CONSTRAINT users_flow_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.admins ALTER CONSTRAINT admins_telegram_verified_by_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.admins ALTER CONSTRAINT admins_telegram_verification_audit_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.admin_sessions ALTER CONSTRAINT admin_sessions_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.products ALTER CONSTRAINT products_id_active_supplier_source_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.brand_products ALTER CONSTRAINT brand_products_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.brand_products ALTER CONSTRAINT brand_products_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.inventory_items ALTER CONSTRAINT inventory_items_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.inventory_items ALTER CONSTRAINT inventory_items_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.inventory_items ALTER CONSTRAINT inventory_items_assigned_fulfillment_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallets ALTER CONSTRAINT wallets_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_payment_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.wallet_entries ALTER CONSTRAINT wallet_entries_audit_event_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_hold_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_resolution_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_resolution_audit_event_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.orders ALTER CONSTRAINT orders_resolution_wallet_entry_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.payments ALTER CONSTRAINT payments_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.payments ALTER CONSTRAINT payments_topup_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.payments ALTER CONSTRAINT payments_review_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.payments ALTER CONSTRAINT payments_compensating_wallet_entry_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.fulfillments ALTER CONSTRAINT fulfillments_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.fulfillments ALTER CONSTRAINT fulfillments_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.fulfillments ALTER CONSTRAINT fulfillments_resolution_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.fulfillments ALTER CONSTRAINT fulfillments_order_id_manifest_generation_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_requests ALTER CONSTRAINT supplier_requests_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_requests ALTER CONSTRAINT supplier_requests_product_supplier_source_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_requests ALTER CONSTRAINT supplier_requests_approval_audit_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_payment_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_fulfillment_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_campaign_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_discount_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_support_message_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_telegram_delivery_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_product_stock_subscription_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_product_supplier_source_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.jobs ALTER CONSTRAINT jobs_audit_event_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.campaigns ALTER CONSTRAINT campaigns_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.campaigns ALTER CONSTRAINT campaigns_created_by_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_discount_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_fulfillment_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_support_message_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_product_stock_subscription_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_audit_event_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_campaign_id_campaign_batch_id_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.telegram_deliveries ALTER CONSTRAINT telegram_deliveries_order_id_delivery_generation_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.support_tickets ALTER CONSTRAINT support_tickets_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.support_tickets ALTER CONSTRAINT support_tickets_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.support_tickets ALTER CONSTRAINT support_tickets_assigned_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.support_messages ALTER CONSTRAINT support_messages_ticket_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.support_messages ALTER CONSTRAINT support_messages_sender_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.legacy_id_map ALTER CONSTRAINT legacy_id_map_target_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.audit_events ALTER CONSTRAINT audit_events_actor_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.audit_events ALTER CONSTRAINT audit_events_actor_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.audit_events ALTER CONSTRAINT audit_events_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discounts ALTER CONSTRAINT discounts_brand_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discounts ALTER CONSTRAINT discounts_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discounts ALTER CONSTRAINT discounts_eligible_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discounts ALTER CONSTRAINT discounts_source_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discounts ALTER CONSTRAINT discounts_created_by_admin_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_uses ALTER CONSTRAINT discount_uses_discount_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_uses ALTER CONSTRAINT discount_uses_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_uses ALTER CONSTRAINT discount_uses_discount_revision_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_revisions ALTER CONSTRAINT discount_revisions_discount_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_revisions ALTER CONSTRAINT discount_revisions_audit_event_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_revisions ALTER CONSTRAINT discount_revisions_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.discount_revisions ALTER CONSTRAINT discount_revisions_eligible_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.bot_activity_events ALTER CONSTRAINT bot_activity_events_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.bot_activity_events ALTER CONSTRAINT bot_activity_events_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.bot_activity_events ALTER CONSTRAINT bot_activity_events_order_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.bot_activity_events ALTER CONSTRAINT bot_activity_events_support_ticket_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_request_attempts ALTER CONSTRAINT supplier_request_attempts_supplier_request_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_request_attempts ALTER CONSTRAINT supplier_request_attempts_parent_submit_attempt_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.supplier_request_attempts ALTER CONSTRAINT supplier_request_attempts_admin_authorization_audit_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.product_supplier_sources ALTER CONSTRAINT product_supplier_sources_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.product_stock_subscriptions ALTER CONSTRAINT product_stock_subscriptions_user_id_fk DEFERRABLE INITIALLY IMMEDIATE;
ALTER TABLE shop.product_stock_subscriptions ALTER CONSTRAINT product_stock_subscriptions_product_id_fk DEFERRABLE INITIALLY IMMEDIATE;
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
        IF OLD.hosted_invoice_started_at IS NOT NULL THEN
            PERFORM require_true(NEW.hosted_invoice_started_at IS NOT DISTINCT FROM OLD.hosted_invoice_started_at,'hosted_invoice_attempt_immutable');
        END IF;
        IF OLD.hosted_invoice_ref IS NOT NULL THEN
            PERFORM require_true(ROW(NEW.hosted_invoice_ref,NEW.hosted_invoice_url) IS NOT DISTINCT FROM ROW(OLD.hosted_invoice_ref,OLD.hosted_invoice_url),'hosted_invoice_identity_immutable');
        END IF;
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
DECLARE entry wallet_entries; previous_balance numeric; current_version bigint;
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
            PERFORM require_true(o.release_authorized_at IS NOT NULL OR (o.order_kind='prepaid_units' AND o.fulfillment_status='completed') OR
                (o.order_kind='product_purchase' AND NOT o.is_preorder AND o.payment_status IN ('paid','not_required') AND o.payment_method IN ('wallet','no_payment_required')
                    AND local_count=o.quantity AND (SELECT count(*) FROM fulfillments WHERE order_id=oid)=o.quantity
                    AND NOT EXISTS(SELECT 1 FROM supplier_requests WHERE order_id=oid)
                    AND (SELECT count(*) FROM fulfillments f JOIN inventory_items i ON i.assigned_fulfillment_id=f.id
                        WHERE f.order_id=oid AND f.kind='local' AND f.status IN ('acquired','ready','released') AND i.state='allocated')=o.quantity),
                'redemption_requires_complete_obligation');
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
    IF TG_OP='INSERT' AND NEW.created_by_admin_id IS NULL AND NEW.kind<>'prepaid_entitlement' THEN
        PERFORM require_true(legacy_allowed('discount',NEW.id),'legacy_coupon_provenance');
    END IF;
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

CREATE FUNCTION activity_payment_origin(e bot_activity_events) RETURNS boolean
LANGUAGE sql STABLE SET search_path=shop,pg_catalog AS $$
    SELECT EXISTS(SELECT 1 FROM payments p LEFT JOIN orders o ON o.id=p.order_id JOIN users u ON u.id=coalesce(p.topup_user_id,o.user_id)
        WHERE u.id=e.user_id AND e.event_type='payment' AND e.source_kind='system' AND e.support_ticket_id IS NULL AND e.flow_revision IS NULL AND e.telegram_update_id IS NULL
            AND ROW(e.product_id,e.order_id,e.quantity_selected) IS NOT DISTINCT FROM ROW(o.product_id,o.id,o.quantity)
            AND CASE e.action_code WHEN 'payment_denied' THEN EXISTS(SELECT 1 FROM audit_events a WHERE a.entity_kind='payment' AND a.entity_id=p.id
                AND a.brand_id=u.brand_id AND a.actor_kind='admin' AND a.action='update' AND a.created_at=e.occurred_at
                AND e.idempotency_key='workflow:payment_denied:'||replace(a.id::text,'-',''))
            ELSE e.idempotency_key='workflow:'||e.action_code||':'||replace(p.id::text,'-','') AND
                CASE e.action_code WHEN 'topup_created' THEN p.topup_user_id IS NOT NULL AND p.created_at=e.occurred_at
                    WHEN 'payment_awaiting' THEN p.provider_payment_ref IS NOT NULL
                    WHEN 'payment_observed' THEN p.first_payment_observed_at=e.occurred_at
                    WHEN 'payment_evidence_ready' THEN p.confirmed_at=e.occurred_at
                    WHEN 'payment_review' THEN p.status='review'
                    WHEN 'payment_settled' THEN p.status='settled' AND p.confirmed_at=e.occurred_at ELSE false END END)
$$;

CREATE FUNCTION record_payment_activity() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE member_id uuid; product_id uuid; quantity_no integer; action_name text; original_at timestamptz; identity_id uuid; outcome_name text; event_row bot_activity_events;
BEGIN
    SELECT coalesce(NEW.topup_user_id,o.user_id),o.product_id,o.quantity INTO member_id,product_id,quantity_no FROM (SELECT 1) seed LEFT JOIN orders o ON o.id=NEW.order_id;
    IF member_id IS NULL THEN RETURN NEW; END IF;
    FOR action_name IN SELECT unnest(ARRAY['topup_created','payment_awaiting','payment_observed','payment_evidence_ready','payment_review','payment_denied','payment_settled']) LOOP
        original_at:=NULL; identity_id:=NEW.id; outcome_name:='accepted';
        IF action_name='topup_created' AND TG_OP='INSERT' AND NEW.topup_user_id IS NOT NULL THEN original_at:=NEW.created_at;
        ELSIF TG_OP='UPDATE' THEN
            IF action_name='payment_awaiting' AND OLD.provider_payment_ref IS NULL AND NEW.provider_payment_ref IS NOT NULL THEN original_at:=clock_timestamp();
            ELSIF action_name='payment_observed' AND OLD.first_payment_observed_at IS NULL AND NEW.first_payment_observed_at IS NOT NULL THEN original_at:=NEW.first_payment_observed_at;
            ELSIF action_name='payment_evidence_ready' AND OLD.confirmed_at IS NULL AND NEW.confirmed_at IS NOT NULL THEN original_at:=NEW.confirmed_at;
            ELSIF action_name='payment_review' AND OLD.status<>'review' AND NEW.status='review' THEN original_at:=clock_timestamp(); outcome_name:='blocked';
            ELSIF action_name='payment_denied' AND OLD.status<>'rejected' AND NEW.status='rejected' THEN
                SELECT a.id,a.created_at INTO identity_id,original_at FROM audit_events a WHERE a.entity_kind='payment' AND a.entity_id=NEW.id AND a.actor_kind='admin'
                    AND a.actor_admin_id=NEW.review_admin_id AND a.action='update' AND a.created_at=CURRENT_TIMESTAMP ORDER BY a.id LIMIT 1;
                outcome_name:='rejected';
            ELSIF action_name='payment_settled' AND OLD.status<>'settled' AND NEW.status='settled' THEN original_at:=NEW.confirmed_at;
            END IF;
        END IF;
        IF original_at IS NULL OR original_at>clock_timestamp() OR original_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours' THEN CONTINUE; END IF;
        event_row:=ROW(gen_random_uuid(),member_id,'payment','system',action_name,product_id,NEW.order_id,NULL,quantity_no,NULL,NULL,outcome_name,
            'workflow:'||action_name||':'||replace(identity_id::text,'-',''),original_at)::bot_activity_events;
        INSERT INTO bot_activity_events SELECT (event_row).* ON CONFLICT(user_id,idempotency_key) DO NOTHING;
        IF NOT FOUND THEN
            PERFORM require_true(EXISTS(SELECT 1 FROM bot_activity_events e WHERE e.user_id=member_id AND e.idempotency_key=event_row.idempotency_key
                AND ROW(e.event_type,e.source_kind,e.action_code,e.product_id,e.order_id,e.support_ticket_id,e.quantity_selected,e.flow_revision,e.telegram_update_id,e.outcome_code,e.occurred_at)
                IS NOT DISTINCT FROM ROW(event_row.event_type,event_row.source_kind,event_row.action_code,event_row.product_id,event_row.order_id,event_row.support_ticket_id,event_row.quantity_selected,event_row.flow_revision,event_row.telegram_update_id,event_row.outcome_code,event_row.occurred_at)),'activity_identity_conflict');
        END IF;
    END LOOP;
    RETURN NEW;
END $$;
CREATE TRIGGER payment_activity AFTER INSERT OR UPDATE ON payments FOR EACH ROW EXECUTE FUNCTION record_payment_activity();

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
        IF NEW.event_type='payment' THEN PERFORM require_true(activity_payment_origin(NEW),'activity_actual_payment_transition'); END IF;
        IF NEW.order_id IS NOT NULL THEN PERFORM require_true(EXISTS(SELECT 1 FROM orders WHERE id=NEW.order_id AND user_id=NEW.user_id),'activity_order_member'); END IF;
        IF NEW.support_ticket_id IS NOT NULL THEN PERFORM require_true(EXISTS(SELECT 1 FROM support_tickets WHERE id=NEW.support_ticket_id AND user_id=NEW.user_id),'activity_ticket_member'); END IF;
        IF NEW.action_code IN ('checkout_funded','checkout_expired','checkout_replaced') THEN
            PERFORM require_true(NEW.support_ticket_id IS NULL AND NEW.flow_revision IS NULL AND NEW.telegram_update_id IS NULL AND
                EXISTS(SELECT 1 FROM orders o WHERE o.id=NEW.order_id AND o.user_id=NEW.user_id AND o.product_id=NEW.product_id AND o.quantity=NEW.quantity_selected
                    AND CASE NEW.action_code WHEN 'checkout_funded' THEN o.payment_status IN ('paid','not_required') AND o.payment_confirmed_at=NEW.occurred_at
                        WHEN 'checkout_expired' THEN o.resolution_kind='unpaid_expiry' AND o.resolution_at=NEW.occurred_at
                        WHEN 'checkout_replaced' THEN o.resolution_kind='replaced' AND o.resolution_at=NEW.occurred_at ELSE false END),'activity_actual_checkout_transition');
        END IF;
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

CREATE FUNCTION record_order_activity() RETURNS trigger
LANGUAGE plpgsql SET search_path=shop,pg_catalog AS $$
DECLARE action_name text; original_at timestamptz; outcome_name text; event_key text;
BEGIN
    IF NEW.product_id IS NULL THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' AND NEW.payment_status=OLD.payment_status THEN RETURN NEW; END IF;
    IF NEW.payment_status IN ('paid','not_required') THEN action_name:='checkout_funded'; original_at:=NEW.payment_confirmed_at; outcome_name:='accepted';
    ELSIF TG_OP='UPDATE' AND NEW.payment_status='expired' AND NEW.resolution_kind='unpaid_expiry' THEN
        action_name:='checkout_expired'; original_at:=NEW.resolution_at; outcome_name:='cancelled';
    ELSIF TG_OP='UPDATE' AND NEW.payment_status='cancelled' AND NEW.resolution_kind='replaced' THEN
        action_name:='checkout_replaced'; original_at:=NEW.resolution_at; outcome_name:='cancelled';
    ELSE RETURN NEW;
    END IF;
    IF original_at IS NULL OR original_at>clock_timestamp() OR original_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours' THEN RETURN NEW; END IF;
    event_key:='workflow:'||action_name||':'||replace(NEW.id::text,'-','');
    INSERT INTO bot_activity_events(id,user_id,event_type,source_kind,action_code,product_id,order_id,quantity_selected,outcome_code,idempotency_key,occurred_at)
    VALUES(gen_random_uuid(),NEW.user_id,'checkout','system',action_name,NEW.product_id,NEW.id,NEW.quantity,outcome_name,event_key,original_at)
    ON CONFLICT(user_id,idempotency_key) DO NOTHING;
    IF NOT FOUND THEN
        PERFORM require_true(EXISTS(SELECT 1 FROM bot_activity_events e WHERE e.user_id=NEW.user_id AND e.idempotency_key=event_key
            AND ROW(e.event_type,e.source_kind,e.action_code,e.product_id,e.order_id,e.quantity_selected,e.outcome_code,e.occurred_at)
                IS NOT DISTINCT FROM ROW('checkout'::text,'system'::text,action_name,NEW.product_id,NEW.id,NEW.quantity,outcome_name,original_at)
            AND e.support_ticket_id IS NULL AND e.flow_revision IS NULL AND e.telegram_update_id IS NULL),'activity_identity_conflict');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER order_activity AFTER INSERT OR UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION record_order_activity();

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
                    OR EXISTS(SELECT 1 FROM payments p WHERE p.order_id=o.id AND p.status='settled' AND p.confirmed_at IS NOT NULL AND p.amount_received>=p.amount_due))))
        OR (NEW.revision_no=0 AND d.revision=0 AND d.created_by_admin_id IS NULL AND legacy_allowed('discount',d.id) AND legacy_allowed('discount_revision',NEW.id)
            AND EXISTS(SELECT 1 FROM audit_events a JOIN legacy_id_map m ON m.target_kind='discount' AND m.target_id=d.id AND m.import_batch=a.correlation_id
                WHERE a.id=NEW.audit_event_id AND a.actor_kind='system' AND a.action='legacy_import' AND a.entity_kind='discount' AND a.entity_id=d.id
                    AND a.brand_id=d.brand_id AND a.new_revision=0 AND a.reason LIKE 'protected:%' AND a.command_sha256 IS NOT NULL)),'revision_audit');
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

CREATE FUNCTION activity_retention_covered(event_id uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,shop AS $$
    SELECT coalesce((SELECT (shop.activity_payment_origin(e) OR COALESCE(shop.telegram_update_seen(b.telegram_update_highwater,b.telegram_update_window,e.telegram_update_id),false)
        OR (e.action_code IN ('checkout_created','checkout_funded','checkout_expired','checkout_replaced') AND EXISTS(
            SELECT 1 FROM shop.orders o WHERE o.id=e.order_id AND o.user_id=e.user_id AND o.product_id=e.product_id AND o.quantity=e.quantity_selected
                AND CASE e.action_code WHEN 'checkout_created' THEN o.created_at=e.occurred_at
                    WHEN 'checkout_funded' THEN o.payment_status IN ('paid','not_required') AND o.payment_confirmed_at=e.occurred_at
                    WHEN 'checkout_expired' THEN o.resolution_kind='unpaid_expiry' AND o.resolution_at=e.occurred_at
                    WHEN 'checkout_replaced' THEN o.resolution_kind='replaced' AND o.resolution_at=e.occurred_at ELSE false END))
        OR COALESCE(e.action_code IN ('flow_expired','flow_abandoned') AND e.flow_revision IS NOT NULL AND u.flow_revision>e.flow_revision,false)
        OR (e.action_code IN ('quantity_enter','checkout_rejected') AND e.source_kind='flow'
            AND e.outcome_code=CASE WHEN e.action_code='quantity_enter' THEN 'accepted' ELSE 'rejected' END AND e.product_id IS NOT NULL
            AND e.quantity_selected IS NOT NULL AND e.order_id IS NULL AND e.support_ticket_id IS NULL AND e.flow_revision IS NULL
            AND e.telegram_update_id IS NULL AND e.idempotency_key ~ ('^workflow:'||e.action_code||':[0-9a-f]{32}$'))
        OR (e.action_code='policy_check' AND e.source_kind='system' AND e.outcome_code IN ('accepted','blocked') AND e.product_id IS NULL
            AND e.order_id IS NULL AND e.support_ticket_id IS NULL AND e.quantity_selected IS NULL AND e.flow_revision IS NULL AND e.telegram_update_id IS NULL
            AND e.idempotency_key ~ '^workflow:policy_check:[0-9a-f]{32}$')
        OR (e.action_code='discounts_open' AND e.source_kind='flow' AND e.outcome_code IN ('accepted','rejected') AND e.product_id IS NULL
            AND e.order_id IS NULL AND e.support_ticket_id IS NULL AND e.quantity_selected IS NULL AND e.flow_revision IS NULL
            AND e.telegram_update_id IS NULL AND e.idempotency_key ~ '^coupon_lookup:[0-9a-f]{32}$')) FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id JOIN shop.brands b ON b.id=u.brand_id WHERE e.id=event_id),false)
$$;
CREATE FUNCTION activity_cleanup_candidates(batch_limit integer) RETURNS TABLE(event_id uuid)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,shop AS $$
    SELECT e.id FROM shop.bot_activity_events e WHERE e.occurred_at<CURRENT_TIMESTAMP-INTERVAL '4320 hours'
        AND shop.activity_retention_covered(e.id) ORDER BY e.occurred_at,e.id LIMIT least(greatest(batch_limit,0),500) FOR UPDATE OF e SKIP LOCKED
$$;

REVOKE ALL ON ALL TABLES IN SCHEMA shop FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA shop FROM PUBLIC;
