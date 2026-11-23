# EF integrity implementation and test matrix
Qualified foundation db8aa69; current T013 candidate extends all18 original assertions. This matrix distinguishes local/source evidence from pending full-provider CI. Existing SQLite SQL assertions and all PostgreSQL migration guards remain active until their replacement and affected-path qualification are complete. A retained guard is not a completed EF assertion.
## Command boundary
- One existing native transaction; EF flushes tracked writes, retains affected identities across early SaveChangesAsync and validates before the native commit. Rejected writes poison the command even if caught.
- Runtime command interception uses EF command-source metadata, not SQL text. Direct ExecuteUpdate/Delete, raw SQL and runtime migration commands are rejected. SaveChanges uses tracked capture; the scoped UpdateAsync adapter selects at most257 rows, rejects batches above256, intersects the mutation with captured IDs, checks affected count and captures both images. Only User/Wallet updates have foundation adapters; other adapters and deletion/retention paths remain T013/store work. No new generic repository is introduced.
- Scope is capped at1024 distinct affected identities per command. Wallet validation uses affected wallets/new postings and indexed predecessor lookup; it does not replay/count the historical ledger. Foundation qualification assumes a previously accepted baseline; full existing-file validation belongs to explicit adoption.
- Read-only/unadmitted writes, immutable tracked history and forbidden bulk/raw bypasses reject. Native SQLite query-only/immediate-writer and PostgreSQL isolation/writer fences remain intact. Cancellation is checked after flush/validation and before the original commit boundary.
## SQLite assertion dispositions
| Original rule | Affected scope | Current implementation | Evidence/remaining gate |
| --- | --- | --- | --- |
| wallet_posting_chain | New immutable posting IDs and indexed predecessor by(user,version) | EfIntegrity: exact arithmetic, contiguous append and final wallet version | EfIntegrityTests: two flushed28-digit postings, wrong arithmetic, immutable history |
| wallet_posting_balance | Affected wallet user IDs and original/final wallet version | EfIntegrity: matching final posting, zero state and monotonic version; no historical count/window | EfIntegrityTests: missing posting, tracked/bulk regression to an older matching posting, restart |
| wallet_compensation_origin | New posting IDs plus existing postings linked to touched orders/payments | EfIntegrity: related-principal closure | EfCommerceIntegrityTests: payment status/received amount and order-only changes preserve accepted credit |
| order_split_quantity | Touched orders; supplier and inventory/fulfillment changes | EfIntegrity.Commerce | Order/allocation and supplier-only over-quantity rejection |
| order_prepaid_no_goods | Touched orders and their fulfillment/request graph | EfIntegrity.Commerce | Prepaid goods rejection |
| order_preorder_whole_allocation | Touched order and old/new assigned-fulfillment order | EfIntegrity.Commerce | Partial preorder allocation rejection |
| order_preorder_fifo | Touched paid preorder | EfIntegrity.Commerce; original assertion semantics unchanged | Paid preorder without accepted allocation/state rejection |
| order_unit_allocation | Touched order via fulfillment or old/new inventory assignment | EfIntegrity.Commerce | Missing unit inventory and allocation removal with unchanged fulfillment |
| order_release_state | Touched release/ready/delivering/completed order | EfIntegrity.Commerce | Unpaid ready-state rejection |
| order_complete_manifest | Touched release order and its units/requests/discount uses | EfIntegrity.Commerce | Incomplete manifest rejection |
| order_release_generation | Touched order and released fulfillments | EfIntegrity.Commerce | Matched-generation unit without release authorization; principal flushed before descriptor FK |
| order_resolution_ledger | Touched resolution order and indexed referenced posting | EfIntegrity.Commerce | Unrelated accepted posting cannot satisfy a resolution |
| order_resolution_hold | Touched resolution order and discount uses | EfIntegrity.Commerce | Cancellation cannot retain a held coupon |
| coupon_revision_required | Touched discount IDs from discount/revision/use changes | EfIntegrity.Commerce | Missing revision rejection |
| coupon_redemption_count | Touched discounts and indexed relevant usage rows | EfIntegrity.Commerce; per-discount aggregate, no global usage scan | Counter mismatch rejection |
| coupon_redeemed_obligation | Touched orders including discount-use mutations | EfIntegrity.Commerce | Redemption without accepted obligation rejection |
| audit_customer_brand | Added audit IDs and changed customer IDs | EfIntegrity; immutable ownership prevents parent reassignment | Actor/brand and caught-failure tests |
| audit_entity_scope | Added audit IDs; deleted entity identities; immutable parent ownership | EfIntegrity.Audits; canonical registry kinds included | Missing target, audited product deletion and provider acceptance; normal updates avoid a global reference scan |
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

## CI follow-up status
First full candidate4010a26 failed4/941 PostgreSQL-related integration cases;937 integration,260 unit and148 contract cases passed. Preserve that failed evidence. Follow-up addresses array-enabled data-source wiring, native error classification and fixture proxy authentication and adds collection probes. Its local build/38 checks pass; exact follow-up CI is pending owner review. Keep foundation gates unchecked until that report is verified.

## Qualified foundation
CI45155963641 atdb8aa69 passed260 unit/941 integration/148 contract checks, zero skipped, including all previously failed PostgreSQL cases. Deployment skipped. T007/T008 are complete for the small mapped foundation; full commerce/reference assertions and their related/principal mutation tests remain T013. No store or encryption conversion is inferred from these results.

## T013 candidate
All18 original assertion codes now have EF implementations and related-scope tests. Ownership fields cannot be reassigned through tracked writes; the qualified User bulk adapter compares actual before/after identity. Wallet versions cannot regress, including to an older still-matching posting. Deleted entities revalidate existing polymorphic audit references; ordinary updates retain fixed ownership and avoid a global reference scan. Supplier-request-attempt ownership is fixed too. These checks rely on a previously accepted baseline and immutable posting history; explicit adoption retains full baseline verification. Legacy SQL pre-commit validation remains active until store/runtime conversion is complete.
Original SQLite baseline parity covers all28 business tables, every CHECK expression and every original index's columns/order/collation/uniqueness/filter. Extra unique indexes must be logically redundant with the original primary key. Existing501-column/nullability/PK/FK and95-reference/27-trigger tests remain active. PostgreSQL retains all85 reviewed SQL-02 triggers; its new accepted-checkout/coupon fixture exercises the full EF query set. New PG cases await CI.
An observed explicit high-water0 became default-1. Seven nonzero numeric mappings now use their real default as the unset marker, with matching entity defaults; explicit0 is written or rejected by its original constraint, never silently substituted. Existing true-boolean markers remain. Pinned EF scaffolding produced empty migrations and byte-identical snapshots after BOM normalization; unpublished empty scaffolds were removed, and existing migrations/snapshots/baseline hashes remain unchanged.
Final local candidate:63 EF/SQLite checks, zero skipped, build zero warnings/errors. Full-provider CI is pending owner review; T013 remains unchecked.
