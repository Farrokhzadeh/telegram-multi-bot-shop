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
