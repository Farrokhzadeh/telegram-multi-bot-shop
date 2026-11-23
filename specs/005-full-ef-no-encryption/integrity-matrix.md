# EF integrity implementation and test matrix
Input main08721b0. This matrix distinguishes the bounded foundation from the complete model port. Existing SQLite SQL assertions and all PostgreSQL migration guards remain active until their replacement and affected-path qualification are complete. A retained guard is not a completed EF assertion.
## Command boundary
- One existing native transaction; EF flushes tracked writes, retains affected identities across early SaveChangesAsync and validates before the native commit. Rejected writes poison the command even if caught.
- Runtime command interception uses EF command-source metadata, not SQL text. Direct ExecuteUpdate/Delete, raw SQL and runtime migration commands are rejected. SaveChanges uses tracked capture; the scoped UpdateAsync adapter selects at most257 rows, rejects batches above256, intersects the mutation with captured IDs, checks affected count and captures both images. Only User/Wallet updates have foundation adapters; other adapters and deletion/retention paths remain T013/store work. No new generic repository is introduced.
- Scope is capped at1024 distinct affected identities per command. Wallet validation uses affected wallets/new postings and indexed predecessor lookup; it does not replay/count the historical ledger. Foundation qualification assumes a previously accepted baseline; full existing-file validation belongs to explicit adoption.
- Read-only/unadmitted writes, immutable tracked history and forbidden bulk/raw bypasses reject. Native SQLite query-only/immediate-writer and PostgreSQL isolation/writer fences remain intact. Cancellation is checked after flush/validation and before the original commit boundary.
## SQLite assertion dispositions
| Original rule | Affected scope | Current implementation | Evidence/remaining gate |
| --- | --- | --- | --- |
| wallet_posting_chain | Added posting IDs, predecessor by(user,version), final wallet version | EfIntegrity; exact first/predecessor arithmetic; no ledger-wide count/window | EfIntegrityTests: exact accepted posting/wrong arithmetic; PG acceptance pending CI |
| wallet_posting_balance | Changed wallet user IDs and wallets of added postings | EfIntegrity; zero state or indexed matching final posting | EfIntegrityTests: tracked/set-based incomplete writes, early save, restart |
| wallet_compensation_origin | Added posting IDs; changed order/payment reverse dependencies still T013 | EfIntegrity for added postings; full related-principal expansion T013 | Both provider translation on accepted posting; compensation negatives T013 |
| order_split_quantity | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_prepaid_no_goods | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_preorder_whole_allocation | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_preorder_fifo | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_unit_allocation | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_release_state | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_complete_manifest | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_release_generation | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_resolution_ledger | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| order_resolution_hold | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| coupon_revision_required | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| coupon_redemption_count | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| coupon_redeemed_obligation | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
| audit_customer_brand | Added audit IDs and changed customer IDs | EfIntegrity; actor brand/reason/platform rules | EfIntegrityTests: early-saved invalid actor brand; existing session tests |
| audit_entity_scope | Related orders/fulfillments/inventory/supplier requests/discounts/audits per rule | Original SQL retained; EF port and reverse-scope expansion T013 | Existing regression remains mandatory; focused EF cases T013/T025 |
## PostgreSQL guard dispositions
All85 triggers below remain SQL-02 migration definitions, including deferred money/release checks and immutable/delete/truncate defenses. T013 must map each function assertion to affected entities or explicitly retain the database constraint; store qualification must exercise its before/after behavior. Existing trigger-count tests prove inventory, not every business transition. No trigger is removed by this foundation.
| Function/rule family | Exact trigger names | Foundation disposition |
| --- | --- | --- |
| guard_brands | brands_guard | Retained; full model/transition disposition T013 |
| guard_users | users_guard | Retained; full model/transition disposition T013 |
| guard_products | products_guard | Retained; full model/transition disposition T013 |
| guard_listing | listing_guard | Retained; full model/transition disposition T013 |
| immutable_history | listing_no_delete, activity_no_update, wallet_entries_immutable, audit_events_immutable, discount_revisions_immutable, support_messages_immutable, legacy_id_map_immutable, orders_no_delete, payments_no_delete, fulfillments_no_delete, supplier_requests_no_delete, supplier_request_attempts_no_delete, discount_uses_no_delete, discounts_no_delete, wallets_no_delete, inventory_items_no_delete, support_tickets_no_delete, campaigns_no_delete, brands_no_truncate, brand_channels_no_truncate, users_no_truncate, admins_no_truncate, admin_sessions_no_truncate, products_no_truncate, brand_products_no_truncate, inventory_items_no_truncate, wallets_no_truncate, wallet_entries_no_truncate, orders_no_truncate, payments_no_truncate, fulfillments_no_truncate, supplier_requests_no_truncate, jobs_no_truncate, campaigns_no_truncate, telegram_deliveries_no_truncate, support_tickets_no_truncate, support_messages_no_truncate, legacy_id_map_no_truncate, audit_events_no_truncate, discounts_no_truncate, discount_uses_no_truncate, discount_revisions_no_truncate, bot_activity_events_no_truncate, supplier_request_attempts_no_truncate, product_supplier_sources_no_truncate, product_stock_subscriptions_no_truncate | Retained; full model/transition disposition T013 |
| guard_orders | orders_guard | Retained; full model/transition disposition T013 |
| guard_payments | payments_guard | Retained; full model/transition disposition T013 |
| guard_wallet_entries | ledger_guard | Retained; wallet EF fixture adds independent pre-commit checks |
| guard_wallet_shape | wallet_shape | Retained; wallet EF fixture adds independent pre-commit checks |
| check_wallet_posting | wallet_posting_consistency, ledger_posting_consistency | Retained; wallet EF fixture adds independent pre-commit checks |
| guard_sources | sources_guard | Retained; full model/transition disposition T013 |
| guard_supplier_requests | supplier_requests_guard | Retained; full model/transition disposition T013 |
| guard_fulfillments | fulfillments_guard | Retained; full model/transition disposition T013 |
| guard_inventory | inventory_guard | Retained; full model/transition disposition T013 |
| check_order_integrity | order_integrity, units_order_integrity, inventory_order_integrity, requests_order_integrity, claims_order_integrity | Retained; full model/transition disposition T013 |
| guard_attempts | attempts_guard | Retained; full model/transition disposition T013 |
| guard_support | ticket_guard, support_message_guard | Retained; full model/transition disposition T013 |
| guard_discounts | discounts_guard | Retained; full model/transition disposition T013 |
| guard_discount_uses | claim_guard | Retained; full model/transition disposition T013 |
| record_payment_activity | payment_activity | Retained; full model/transition disposition T013 |
| guard_subscription_activity | subscription_guard, activity_guard | Retained; full model/transition disposition T013 |
| record_order_activity | order_activity | Retained; full model/transition disposition T013 |
| guard_deliveries | deliveries_guard | Retained; full model/transition disposition T013 |
| guard_jobs | jobs_guard | Retained; full model/transition disposition T013 |
| guard_coupon_revision | coupon_revision_guard | Retained; full model/transition disposition T013 |
| check_coupon_history | coupon_history, claim_history | Retained; full model/transition disposition T013 |
| guard_campaigns | campaign_guard | Retained; full model/transition disposition T013 |
| guard_admin_sessions | admin_session_guard | Retained; full model/transition disposition T013 |
| guard_retained_work_delete | jobs_retention_delete, delivery_retention_delete | Retained; full model/transition disposition T013 |
| check_reference_registry | audit_reference_guard, legacy_reference_guard | Retained; full model/transition disposition T013 |
## Negative gates
The final local EF/SQLite selection passed38/38, zero skipped,13.199s; managed build passed with zero warnings/errors. Earlier18-case regression also passed. The first test-source build failed because a fixture namespace import was missing; corrected before the passing build. A formatter invocation used the wrong output configuration; rerun against the existing Debug formatter. Exact-commit CI must still qualify PostgreSQL acceptance, pre-commit rejection, EF admission lock timeout, unknown commit acknowledgement and full formatting/regression.
Actual Akanti rebuild cases cover wallet_entries,fulfillments,telegram_deliveries and jobs: retained ledger row/content, exact27 trigger definitions,95 deferred references including both nullable-generation descriptors, STRICT and immutable-delete rejection. Initial tests exposed trigger loss and cross-table trigger references preventing rename; the generator now drops/recreates the complete reviewed trigger set around EF rebuild DDL. WHEN clauses are retained intact. Rebuild requires an offline exclusive target; there is no existing-file upgrade command or daemon migration. Further provider/model representation changes must version their reviewed guard definitions rather than blindly reuse encrypted-baseline SQL.
Mapped User/Order cycle tests prove complete deferred commit, incomplete-cycle rollback and duplicate customer rejection. Adoption rejects foreign migration IDs/ProductVersion while leaving that metadata unchanged. T008 remains open until exact-candidate CI also passes the native transaction fault tests. Full entity-dependent assertion and transition coverage remains T013/T025.
