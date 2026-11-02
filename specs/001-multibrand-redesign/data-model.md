# Logical model and transaction states

**Reviewed baseline:** 2026-10-30. **Updated:** 2026-10-31 for saved-order-message recovery. **Scope:** 28-table forward documentation design only.
Fields/types/constraints are selected in [schema-contract.md](schema-contract.md); [database-table-review.md](database-table-review.md) is the inventory. [requirements-traceability.md](requirements-traceability.md) maps all approved requirements and success criteria to planned work. Research/Git history records earlier proposals, not extra active alternatives.

The later owner-authorized T008 implemented and qualified the forward schema on disposable data; [SCHEMA_QUALIFICATION.md](../../ops/SCHEMA_QUALIFICATION.md) records that limited evidence. The service flows below remain planned until their individual implementation tasks pass; this logical document is not a full-runtime qualification.

## Identity, configuration and ownership

A users row is one brand membership, unique by brand and Telegram ID. Wallets, orders, coupon claims, support and customer alerts derive ownership from it. Globally unique order ID is the customer/support/admin reference; knowing it authorizes nothing. Admin authentication is independent, globally scoped and revocable. A verified staff Telegram link permits channel exemption, not credential-free backend mutations.

Each brand has its own bot/presentation/retail prices/public support contact, current FX and one active bank destination when enabled. One shared master product/stock pool/source selection serves all brands. Typed protected service configuration holds the single active provider account/merchant account and private staff-chat destination; credentials do not live in product mappings or snapshots.

## Goods, prepaid entitlements and availability

- product_purchase: one product, quantity goods, one fulfillment unit per requested unit. Local-first uses eligible local stock before only the positive selected-supplier remainder; explicit manual units may complete the same product. All goods release together.
- prepaid_units: a paid future-unit source sale issues one matching account-bound N-use 100% coupon, not fake goods or supplier procurement. Source settlement and issuance are idempotent and retain paid provenance.
- local_preorder: a goods-order availability policy. It has no admin availability dates or paid waiting deadline. It does not automatically become supplier-backed preorder.
- New remote need requires a direct verified current provider stock/cost/eligibility read outside the DB transaction. Full eligible local coverage succeeds without supplier access. Recheck source/revision/evidence under the creation/acceptance guards; local stock is not reserved for an unpaid quote.
- Customer listing availability considers eligible local stock plus the selected source. Published unavailable products remain visible as Out of stock; manual publication settings are never overwritten. Later accepted-order blockers are not cleared by catalog recovery.

## Accepted payment and supplier binding

Unpaid bank/crypto: awaiting_payment -> receipt under_review or qualifying crypto confirmation_pending -> paid. A no-evidence invoice expires after its 30-minute window or is replaced atomically by a valid new order for that membership. Receipt/confirmation/accepted money is protected; paid orders do not time out.

Wallet: debit/acceptance and local allocation/remote intent commit together. Zero-due: payment_method=no_payment_required, payment_status=not_required, no fabricated payment/debit, accepted checkout and whole coupon hold/work commit together.

payment_confirmed_at is the single durable accepted/FIFO timestamp, including accepted zero due. Positive-due supplier binding is accepted wallet debit/verified live crypto/approved bank payment; zero-due binding is accepted checkout. Only a positive remote remainder binds/enqueues a request. Record original source/account/product/variant, quantity and accepted supplier-cost evidence in that transaction, then call the provider after commit.

Disabled/missing/unverifiable source after money acceptance preserves the payment/entitlement and known provenance for administrator review; no fabricated runnable request, fallback supplier, automatic compensation or silent rebind.

## Allocation and complete customer release

Every local consumer serializes on the shared product. Older paid local-preorder obligations get their complete quantity first; insufficient head gets zero allocation/reservation and no later local consumer bypasses it. A manually paused head also blocks, even when stock is sufficient; resume keeps original priority.

Immediate local-only checkout allocates its entire quantity/debits/redeems in one transaction or changes nothing. An accepted mixed order may allocate paid local units internally and hold the whole coupon while its remote/manual remainder is acquired. Example: 3 local + 2 selected-supplier units, one complete five-unit manifest; never three customer units now and two later.

Unit kind/source/item identity and supplier attempts remain separate facts. Supplier subsets are encrypted acquired evidence, not completed goods; missing items and unresolved review block every customer reveal route. Bonus items are admin_held inventory evidence, never customer units or automatic shared stock.

After all goods are verified and holds/review clear, set one immutable delivery_generation/manifest, redeem the whole held coupon and queue stable content parts atomically. Delivery recovery reuses that prepared manifest. Prefer editing the existing order message with a durably saved, validated bot/member/order/message ID. Authorize the complete manifest first; retry the same target and generation after rechecking current order state. Suppress known successful sends. Missing/uneditable anchors and file/multi-part delivery require explicit recovery handling, with no silent new-message fallback. An uncertain new credential-message send pauses automatic resending and remains observable for reconciliation or explicit authorized recovery; preserve the paid obligation and protected access to the same complete manifest. This does not promise exactly-once Telegram receipt or customer read confirmation. A Telegram crash cannot trigger another purchase, debit, allocation or redemption; PostgreSQL does not promise atomic Telegram receipt.

## Financial resolution

- Immediate local-only crypto shortage: zero allocation plus exactly one original frozen net USD wallet credit/full coupon release; this rule is not applied merely because a supplier/mixed order needs remote units.
- Paid bank/local-preorder waiting: retain until full allocation or explicit staff order resolution. Approved local-preorder cancellation uses outside-shop repayment, no wallet reversal/payout record, and returns all held uses once.
- Fully verified full late crypto on expired/replaced invoice: exactly one original membership USD credit, no old-order revival/rebind. Other wrong/excess/partial/uncertain receipts are reviewed.
- Confirmed supplier failure: private staff notice, then explicit reconciled bound-source retry or same-brand terminal wallet credit. Wallet/crypto credit uses original net USD; bank credit uses admin-entered/confirmed USD plus reason. Zero-due cannot manufacture cash credit. Retry/credit/delivery serialize; credited orders never procure/deliver again, original money evidence remains, and held uses release without changing coupon expiry.
- Unknown/in-flight effects reconcile before another possible submit. Request local intent, attempt remote key, provider order identity and scheduler retry count are distinct. Internally retained goods require safe admin disposition; credit is not proof a remote attempt never happened.
- External bank/crypto refunds and their bookkeeping remain entirely outside this backend.

## Coupons and ownership

Automatically choose the best applicable whole-quantity coupon; no opt-out or mixed discounted/full-price quantity. Reject excess quantity before creating an order. New claims fit the current caps under a coupon lock. Existing valid held orders keep their original quote/claim after campaign expiry or later owner edits; fulfillment checks the original claim's integrity, not new-checkout eligibility.

Owner edits may reduce caps/expiry or set effective used count without deleting actual use history. redeemed_count records committed redemptions; signed adjustment records the edit. Revisions and actor/reason audit commit with each creation/edit. Return never renews or replaces an expired campaign. Prepaid issuance is bound to the paid source membership and defaults no expiry; legacy unknown ownership/expiry liabilities require evidence review.

## Customer transport, staff transport and support

Customer commerce/support/campaign/restock sends are DMs derived from users.telegram_id. A separately checked staff_chat recipient is allowed only for metadata case/bonus notices to the trusted private staff chat. Incoming group text/membership is not finance/admin authority; bonus content stays behind authenticated staff retrieval. Each bot shows only its configured public admin contact.

Support ticket creation/reply/assignment/closure does not mutate order/FIFO/coupon/payment. Holds, retries, credits and cancellation are independent audited commands. Every active verified admin bypasses channel checks; admins may disable/re-enable a brand's enforcement while retaining the channel list.

## Retention and recovery

Activity reads/cleanup use original occurred_at with 180 elapsed days. Closed restock subscriptions use original closed_at with 180 elapsed days; active rows remain. Keep cutoff equality, scope restock terminal evidence cleanup explicitly and block/report unresolved dependencies. No broad history cascade or retimestamped replay; business idempotency remains independent of both tables.

Import uses legacy_id_map plus protected immutable external batch manifests, one frozen USD opening posting and nonposting source ledger history. Preserve historical aliases/partial deliveries/date promises/refund archives; never fabricate mappings or discard liabilities for compactness. Actual source compatibility, ciphertext roundtrip, PostgreSQL/AOT failure tests and constitution/release gates remain unproved until their assigned tasks.

## T088 bounded ingress replay metadata checkpoint

Canonical schema-contract.md now selects brands.telegram_update_highwater BIGINT and brands.telegram_update_window fixed 128-byte BYTEA as explicit per-bot processed-update metadata. This fulfills the existing requirement that navigation replay fences survive analytics expiry without another physical relation, a raw archive or repurposed flow fields. The highwater and retained seen bits cannot rewind. Out-of-window IDs and unqualified provider resets fail closed; there is no automatic new epoch. Restore the checkpoint with domain state. T088 remains in progress for complete instrumentation and non-Telegram retention/restore; no staging/production schema is migrated by this fresh-disposable qualification.
