using Microsoft.Data.Sqlite;
using Platform.Core;

namespace Platform.Persistence;

internal static class SqliteIntegrity
{
	private static readonly (string Code, string Sql)[] Checks =
	[
		("wallet_posting_chain", """
			WITH postings AS (
				SELECT user_id,posting_version,signed_amount,balance_after,
					row_number() OVER (PARTITION BY user_id ORDER BY posting_version) AS ordinal,
					lag(balance_after) OVER (PARTITION BY user_id ORDER BY posting_version) AS previous
				FROM wallet_entries WHERE is_posting=1
			)
			SELECT 1 FROM postings WHERE posting_version<>ordinal
				OR balance_after<>akanti_decimal_add(coalesce(previous,'0'),signed_amount) LIMIT 1
			"""),
		("wallet_posting_balance", """
			SELECT 1 FROM wallets w WHERE (w.version=0 AND w.balance<>'0')
				OR (w.version>0 AND NOT EXISTS (
					SELECT 1 FROM wallet_entries e WHERE e.user_id=w.user_id AND e.is_posting=1
						AND e.posting_version=w.version AND e.balance_after=w.balance))
				OR w.version<>(SELECT count(*) FROM wallet_entries e WHERE e.user_id=w.user_id AND e.is_posting=1)
			LIMIT 1
			"""),
		("wallet_compensation_origin", """
			SELECT 1 FROM wallet_entries e WHERE e.is_posting=1 AND (
				(e.movement_kind='late_crypto_credit' AND NOT EXISTS (
					SELECT 1 FROM orders o JOIN payments p ON p.order_id=o.id
					WHERE o.id=e.order_id AND o.user_id=e.user_id AND o.payment_status IN ('expired','cancelled')
						AND p.id=e.payment_id AND p.status='settled' AND p.compensating_wallet_entry_id=e.id
						AND p.payment_method='crypto' AND p.quote_currency='USD'
						AND p.amount_due=e.signed_amount AND p.amount_received=p.amount_due))
				OR (e.movement_kind IN ('immediate_crypto_shortage_credit','supplier_wallet_credit') AND NOT EXISTS (
					SELECT 1 FROM orders o WHERE o.id=e.order_id AND o.user_id=e.user_id
						AND o.resolution_kind=e.movement_kind AND o.resolution_wallet_entry_id=e.id))
			) LIMIT 1
			"""),
		("order_split_quantity", """
			SELECT 1 FROM orders o WHERE (
				(SELECT count(*) FROM inventory_items i JOIN fulfillments f ON f.id=i.assigned_fulfillment_id WHERE f.order_id=o.id)
				+ (SELECT coalesce(sum(requested_quantity),0) FROM supplier_requests r WHERE r.order_id=o.id AND r.status NOT IN ('failed','resolved'))
			)>o.quantity LIMIT 1
			"""),
		("order_prepaid_no_goods", """
			SELECT 1 FROM orders o WHERE o.order_kind='prepaid_units' AND (
				EXISTS(SELECT 1 FROM fulfillments f WHERE f.order_id=o.id)
				OR EXISTS(SELECT 1 FROM supplier_requests r WHERE r.order_id=o.id AND r.status NOT IN ('failed','resolved')))
			LIMIT 1
			"""),
		("order_preorder_whole_allocation", """
			SELECT 1 FROM orders o WHERE o.is_preorder=1 AND
				(SELECT count(*) FROM inventory_items i JOIN fulfillments f ON f.id=i.assigned_fulfillment_id WHERE f.order_id=o.id)
				NOT IN (0,o.quantity) LIMIT 1
			"""),
		("order_preorder_fifo", """
			SELECT 1 FROM orders o WHERE o.is_preorder=1 AND o.payment_status IN ('paid','not_required')
				AND o.resolution_kind IS NULL AND o.fulfillment_status NOT IN ('waiting_stock','requires_admin')
				AND NOT EXISTS(SELECT 1 FROM inventory_items i JOIN fulfillments f ON f.id=i.assigned_fulfillment_id WHERE f.order_id=o.id)
			LIMIT 1
			"""),
		("order_unit_allocation", """
			SELECT 1 FROM fulfillments f WHERE f.kind='local' AND f.status IN ('acquired','ready','released')
				AND NOT EXISTS(SELECT 1 FROM inventory_items i WHERE i.assigned_fulfillment_id=f.id) LIMIT 1
			"""),
		("order_release_state", """
			SELECT 1 FROM orders o WHERE (o.release_authorized_at IS NOT NULL
				OR o.fulfillment_status IN ('ready','delivering','completed'))
				AND (o.payment_status NOT IN ('paid','not_required') OR o.hold_at IS NOT NULL
					OR o.review_code IS NOT NULL
					OR (o.resolution_kind IS NOT NULL AND o.resolution_kind<>'manual_fulfilled'))
			LIMIT 1
			"""),
		("order_complete_manifest", """
			SELECT 1 FROM orders o WHERE (o.release_authorized_at IS NOT NULL
				OR o.fulfillment_status IN ('ready','delivering','completed')) AND (
				(o.order_kind='prepaid_units' AND (
					o.release_authorized_at IS NOT NULL OR NOT EXISTS(
						SELECT 1 FROM discounts d WHERE d.kind='prepaid_entitlement' AND d.source_order_id=o.id)))
				OR (o.order_kind='product_purchase' AND (
					o.release_authorized_at IS NULL OR o.delivery_generation IS NULL
					OR (SELECT count(*) FROM fulfillments f WHERE f.order_id=o.id AND f.status IN ('ready','released'))<>o.quantity
					OR EXISTS(SELECT 1 FROM supplier_requests r WHERE r.order_id=o.id AND r.status NOT IN ('succeeded','resolved'))
					OR EXISTS(SELECT 1 FROM discount_uses u WHERE u.order_id=o.id AND u.status<>'redeemed')))
			) LIMIT 1
			"""),
		("order_release_generation", """
			SELECT 1 FROM fulfillments f JOIN orders o ON o.id=f.order_id
			WHERE f.status='released' AND (o.release_authorized_at IS NULL
				OR f.manifest_generation IS NOT o.delivery_generation) LIMIT 1
			"""),
		("order_resolution_ledger", """
			SELECT 1 FROM orders o WHERE o.resolution_wallet_entry_id IS NOT NULL AND NOT EXISTS (
				SELECT 1 FROM wallet_entries e WHERE e.id=o.resolution_wallet_entry_id AND e.user_id=o.user_id
					AND e.order_id=o.id AND e.movement_kind=o.resolution_kind AND e.is_posting=1
					AND (o.resolution_kind<>'supplier_wallet_credit'
						OR (e.admin_id=o.resolution_admin_id AND e.audit_event_id=o.resolution_audit_event_id)))
			LIMIT 1
			"""),
		("order_resolution_hold", """
			SELECT 1 FROM orders o WHERE o.resolution_kind IN (
				'local_preorder_cancellation','immediate_crypto_shortage_credit','supplier_wallet_credit')
				AND (o.release_authorized_at IS NOT NULL
					OR EXISTS(SELECT 1 FROM discount_uses u WHERE u.order_id=o.id AND u.status='held'))
			LIMIT 1
			"""),
		("coupon_revision_required", """
			SELECT 1 FROM discounts d WHERE NOT EXISTS(
				SELECT 1 FROM discount_revisions r WHERE r.discount_id=d.id AND r.revision_no=d.revision)
			LIMIT 1
			"""),
		("coupon_redemption_count", """
			SELECT 1 FROM discounts d WHERE d.redeemed_count<>(
				SELECT coalesce(sum(u.units_covered),0) FROM discount_uses u
				WHERE u.discount_id=d.id AND u.status='redeemed')
			LIMIT 1
			"""),
		("coupon_redeemed_obligation", """
			SELECT 1 FROM discount_uses u JOIN orders o ON o.id=u.order_id WHERE u.status='redeemed' AND NOT (
				o.release_authorized_at IS NOT NULL
				OR (o.order_kind='prepaid_units' AND o.fulfillment_status='completed')
				OR (o.order_kind='product_purchase' AND o.is_preorder=0
					AND o.payment_status IN ('paid','not_required') AND o.payment_method IN ('wallet','no_payment_required')
					AND (SELECT count(*) FROM inventory_items i JOIN fulfillments f ON f.id=i.assigned_fulfillment_id WHERE f.order_id=o.id)=o.quantity
					AND (SELECT count(*) FROM fulfillments f WHERE f.order_id=o.id)=o.quantity
					AND NOT EXISTS(SELECT 1 FROM supplier_requests r WHERE r.order_id=o.id)
					AND (SELECT count(*) FROM fulfillments f JOIN inventory_items i ON i.assigned_fulfillment_id=f.id
						WHERE f.order_id=o.id AND f.kind='local' AND f.status IN ('acquired','ready','released') AND i.state='allocated')=o.quantity)
			) LIMIT 1
			"""),
		("audit_customer_brand", """
			SELECT 1 FROM audit_events a LEFT JOIN users u ON u.id=a.actor_user_id
			WHERE trim(a.reason)='' OR (a.actor_kind='customer' AND (u.id IS NULL OR u.brand_id IS NOT a.brand_id))
				OR (a.entity_kind='platform' AND (a.actor_kind NOT IN ('system','admin')
					OR a.brand_id IS NOT NULL OR a.action NOT IN ('maintenance','legacy_import')))
			LIMIT 1
			"""),
		("audit_entity_scope", """
			SELECT 1 FROM audit_events a WHERE a.entity_kind IN ('brand','user','wallet','wallet_entry','order','payment','fulfillment','supplier_request',
				'campaign','telegram_delivery','discount','support_ticket','support_message','admin','product','inventory_item')
				AND NOT (
					(a.entity_kind='brand' AND EXISTS(SELECT 1 FROM brands b WHERE b.id=a.entity_id AND (a.brand_id IS NULL OR b.id=a.brand_id)))
					OR (a.entity_kind='admin' AND EXISTS(SELECT 1 FROM admins x WHERE x.id=a.entity_id))
					OR (a.entity_kind='product' AND EXISTS(SELECT 1 FROM products x WHERE x.id=a.entity_id))
					OR (a.entity_kind='inventory_item' AND EXISTS(SELECT 1 FROM inventory_items x WHERE x.id=a.entity_id))
					OR (a.entity_kind='user' AND EXISTS(SELECT 1 FROM users x WHERE x.id=a.entity_id AND x.brand_id IS a.brand_id))
					OR (a.entity_kind='wallet' AND EXISTS(SELECT 1 FROM wallets x JOIN users u ON u.id=x.user_id WHERE x.user_id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='wallet_entry' AND EXISTS(SELECT 1 FROM wallet_entries x JOIN users u ON u.id=x.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='order' AND EXISTS(SELECT 1 FROM orders x JOIN users u ON u.id=x.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='payment' AND EXISTS(SELECT 1 FROM payments x JOIN users u ON u.id=coalesce(x.topup_user_id,(SELECT user_id FROM orders WHERE id=x.order_id)) WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='fulfillment' AND EXISTS(SELECT 1 FROM fulfillments x JOIN orders o ON o.id=x.order_id JOIN users u ON u.id=o.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='supplier_request' AND EXISTS(SELECT 1 FROM supplier_requests x JOIN orders o ON o.id=x.order_id JOIN users u ON u.id=o.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='campaign' AND EXISTS(SELECT 1 FROM campaigns x WHERE x.id=a.entity_id AND x.brand_id IS a.brand_id))
					OR (a.entity_kind='telegram_delivery' AND EXISTS(SELECT 1 FROM telegram_deliveries x WHERE x.id=a.entity_id AND x.brand_id IS a.brand_id))
					OR (a.entity_kind='discount' AND EXISTS(SELECT 1 FROM discounts x WHERE x.id=a.entity_id AND x.brand_id IS a.brand_id))
					OR (a.entity_kind='support_ticket' AND EXISTS(SELECT 1 FROM support_tickets x JOIN users u ON u.id=x.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
					OR (a.entity_kind='support_message' AND EXISTS(SELECT 1 FROM support_messages x JOIN support_tickets t ON t.id=x.ticket_id JOIN users u ON u.id=t.user_id WHERE x.id=a.entity_id AND u.brand_id IS a.brand_id))
				) LIMIT 1
			""")
	];

	internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "PRAGMA user_version";
		if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 1) return;
		command.CommandText = "PRAGMA foreign_key_check";
		await using (var rows = await command.ExecuteReaderAsync(ct))
			if (await rows.ReadAsync(ct)) throw new InvalidOperationException("SQLite commit rejected: foreign_key_check.");
		foreach (var (code, sql) in Checks)
		{
			ct.ThrowIfCancellationRequested();
			command.CommandText = sql;
			if (await command.ExecuteScalarAsync(ct) is not null)
				throw new InvalidOperationException("SQLite commit rejected: " + code + ".");
		}
	}
}
