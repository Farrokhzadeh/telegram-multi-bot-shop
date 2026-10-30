# Database table review — current 28-table contract

**Revised:** 2026-10-30. **Status:** documentation design selected; no DDL/application/deployment approval.
**Source:** owner-approved FR-001–084 and SC-001–053.
**Authoritative field contract:** [schema-contract.md](schema-contract.md). The earlier alternatives and exploratory column removals are preserved in Git history and [research.md](research.md), not concurrent design choices.

## Approved physical inventory

The original 23 relations plus discount_revisions, bot_activity_events, supplier_request_attempts, product_supplier_sources and product_stock_subscriptions give exactly 28 tables. No supplier-account pool, refunds, delivery-batch, exchange-rate-history, separate public-reference, migration-checkpoint or schema-history table is silently added.

| # | Table | Current design |
|---:|---|---|
| 1 | **`brands`** | Selected fields, types, keys and guards in schema-contract section 1; no alternate runtime authority. |
| 2 | **`brand_channels`** | Selected fields, types, keys and guards in schema-contract section 2; no alternate runtime authority. |
| 3 | **`users`** | Selected fields, types, keys and guards in schema-contract section 3; no alternate runtime authority. |
| 4 | **`admins`** | Selected fields, types, keys and guards in schema-contract section 4; no alternate runtime authority. |
| 5 | **`admin_sessions`** | Selected fields, types, keys and guards in schema-contract section 5; no alternate runtime authority. |
| 6 | **`products`** | Selected fields, types, keys and guards in schema-contract section 6; no alternate runtime authority. |
| 7 | **`brand_products`** | Selected fields, types, keys and guards in schema-contract section 7; no alternate runtime authority. |
| 8 | **`inventory_items`** | Selected fields, types, keys and guards in schema-contract section 8; no alternate runtime authority. |
| 9 | **`wallets`** | Selected fields, types, keys and guards in schema-contract section 9; no alternate runtime authority. |
| 10 | **`wallet_entries`** | Selected fields, types, keys and guards in schema-contract section 10; no alternate runtime authority. |
| 11 | **`orders`** | Selected fields, types, keys and guards in schema-contract section 11; no alternate runtime authority. |
| 12 | **`payments`** | Selected fields, types, keys and guards in schema-contract section 12; no alternate runtime authority. |
| 13 | **`fulfillments`** | Selected fields, types, keys and guards in schema-contract section 13; no alternate runtime authority. |
| 14 | **`supplier_requests`** | Selected fields, types, keys and guards in schema-contract section 14; no alternate runtime authority. |
| 15 | **`jobs`** | Selected fields, types, keys and guards in schema-contract section 15; no alternate runtime authority. |
| 16 | **`campaigns`** | Selected fields, types, keys and guards in schema-contract section 16; no alternate runtime authority. |
| 17 | **`telegram_deliveries`** | Selected fields, types, keys and guards in schema-contract section 17; no alternate runtime authority. |
| 18 | **`support_tickets`** | Selected fields, types, keys and guards in schema-contract section 18; no alternate runtime authority. |
| 19 | **`support_messages`** | Selected fields, types, keys and guards in schema-contract section 19; no alternate runtime authority. |
| 20 | **`legacy_id_map`** | Selected fields, types, keys and guards in schema-contract section 20; no alternate runtime authority. |
| 21 | **`audit_events`** | Selected fields, types, keys and guards in schema-contract section 21; no alternate runtime authority. |
| 22 | **`discounts`** | Selected fields, types, keys and guards in schema-contract section 22; no alternate runtime authority. |
| 23 | **`discount_uses`** | Selected fields, types, keys and guards in schema-contract section 23; no alternate runtime authority. |
| 24 | **`discount_revisions`** | Selected fields, types, keys and guards in schema-contract section 24; no alternate runtime authority. |
| 25 | **`bot_activity_events`** | Selected fields, types, keys and guards in schema-contract section 25; no alternate runtime authority. |
| 26 | **`supplier_request_attempts`** | Selected fields, types, keys and guards in schema-contract section 26; no alternate runtime authority. |
| 27 | **`product_supplier_sources`** | Selected fields, types, keys and guards in schema-contract section 27; no alternate runtime authority. |
| 28 | **`product_stock_subscriptions`** | Selected fields, types, keys and guards in schema-contract section 28; no alternate runtime authority. |

## Selected relationships and fields

- products.active_supplier_source_id is the only selection authority, checked against the same product. Mapping disable may leave that pointer selected-but-unavailable; selecting a new pointer requires enabled source. No per-brand source selector or duplicate selected flag.
- Protected typed service configuration owns one active account per provider and the shared NOWPayments account. Paid requests snapshot non-secret actual account identity; configuration/key changes never redirect them.
- One payment_confirmed_at is the accepted/FIFO clock; do not keep a competing paid_at. Zero-due acceptance is distinct from real money.
- orders.order_kind distinguishes goods purchase from one paid N-use prepaid entitlement issuance. It does not change the one-product/positive-quantity rule or create fake stock rows.
- Keep fulfillments.kind and actual unit provenance for local-first mixed orders. A positive remote remainder creates a supplier request; zero remote need creates none. Incomplete local preorder heads still allocate zero, while accepted mixed local units may remain allocated internally.
- Fulfillments plus immutable order delivery_generation prove complete manifest membership without a new batch table. Telegram part rows resume that same full prepared content.
- Typed source observations, accepted cost/currency/quantity baseline, explicit request-bound override ceiling and attempt-level remote keys distinguish cached reads, accepted procurement and real calls.
- Supplier extras use admin_held inventory records with request/item provenance, never available stock or additional customer fulfillments. Cross-table capture guards prevent duplicate/customer-plus-bonus identity.
- USD ledger has checked order/payment/request/import origins and exactly-once posting identity. Bank supplier-failure credit amount is admin input. Payment method, quote denomination and received asset/network are separate fields; legacy kind evidence is preserved until verified.
- Customer Telegram targets derive from membership; staff-only notices have their own recipient kind and trusted configured staff_chat_id. Public support contacts do not grant admin rights.
- orders.id is the sole new public reference. Source aliases are preserved through legacy_id_map.source_key_kind, not an extra new-order public_ref.
- Closed stock subscriptions use closed_at retention; activity uses occurred_at. Cleanup coordinates only eligible terminal restock evidence and never cascades generic business history.

## Integrity and preservation

Schema CHECKs validate one-row tuples; cross-row member/brand, same-order, same-product, full-manifest, coupon and mixed-unit invariants use explicit FKs plus static constraint triggers/restricted mutation functions. The final PostgreSQL implementation must prove these; this file is not evidence of an executed constraint.

No json/jsonb, EAV business settings, untrusted tenant selectors, floating monetary arithmetic or automatic daemon migrations. Per-brand ciphertext and the restricted platform inventory key remain separate. New snapshots are re-encrypted with their own authenticated context, not copied between rows.

## Compaction outcomes

| Decision | Selected target |
|---|---|
| Order public reference | Remove public_ref for new target; preserve legacy aliases |
| Wallet scope | USD-only membership wallet; keep exact posting evidence, version and balance_after |
| Ledger source | Checked typed FKs; preserve source provenance before retiring old source fields |
| Payment purpose/method/currency | Derive funding origin from XOR; keep method, quote_currency and network amount distinct; retain legacy_kind evidence |
| Discount-use user | Derive from order; keep explicit saved_currency until source proof |
| Unit kind / campaign language / delivery action | Keep: each has a real behavioral distinction |
| Source pointer vs source selected flag | Pointer only |
| Attempt summary counters | Derive remote summaries from attempt rows; jobs attempts are scheduler data |
| Supplier account/checkpoint/schema-history additions | No unapproved physical table; typed protected config and external operational manifests |

## Evidence before freeze or execution

See [consistency-review.md](consistency-review.md), gates E1–E6. In particular, legacy multi-product orders, non-USD liabilities, monetary precision and actual provider item/cost/recovery contracts may reveal compatibility exceptions. Do not distort those facts to force a 28-table import. Preserve protected sources and stop affected migration/adapter work until reviewed.
