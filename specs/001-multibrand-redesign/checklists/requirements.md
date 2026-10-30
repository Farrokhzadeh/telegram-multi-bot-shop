# Specification Quality Checklist: AccAbad Multi-Brand Commerce Redesign

**Purpose**: Validate requirement quality before planning.  
**Created**: 2026-10-26  
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] Business requirements are primary; runtime/framework options are explicitly deferred to planning.
- [x] Scenarios describe customer, administrator and operator outcomes.
- [x] Mandatory scenarios, functional requirements, key entities and success criteria are complete.

## Requirement Completeness

- [x] Original three owner decisions and subsequent approvals are captured; remaining schema/policy and provider gates are explicitly listed below.
- [x] Existing decisions are unambiguous and testable.
- [x] Success criteria are measurable and technology-agnostic.
- [x] Primary and concurrency acceptance scenarios are included.
- [x] Edge cases, exclusions, dependencies and assumptions are recorded.

## Feature Readiness

- [x] Every major capability has a corresponding acceptance scenario or invariant.
- [x] Financial, brand-isolation and migration safety requirements are explicit.
- [x] Ready for architecture planning at the source/offline level; staging and production remain gated on protected source-data and provider evidence.

## Post-Analysis Remediation (2026-10-27)

- [x] Add dedicated brand-scoped human support independent from AI.
- [x] Add durable campaign recipient materialization, sender throttling and restart coverage.
- [x] Define safe default for confirmed supplier failure pending admin resolution.
- [x] Define migration opening balance and non-posting historical ledger strategy.
- [x] Replace stale panel wording with backend administrative API.
- [x] Add pricing/discount/exchange-rate/topup behavior decision gate.
- [x] Add old-key backup retirement evidence gate.
- [x] Add order-reference uniqueness and acceptance sampling tests.
- [x] Consolidate redundant Fernet test fixtures.
- [x] Replace JSON/JSONB/EAV candidate data with explicit relational columns and typed business-state references.
- [x] Merge campaign recipients and outgoing delivery records, with per-batch/per-brand deduplication across languages.
- [x] Preserve distinct ledgers, payment evidence, fulfillment obligations, supplier request uncertainty and human support history.
- [x] Separate shared inventory encryption from brand-specific token/customer encryption scopes.
- [x] Owner confirmed private-DM-only customer flow; use revision-guarded `users.flow_*`, **no `telegram_sessions`** in v1, and no redundant `telegram_deliveries.chat_id`.
- [x] Preserve explicit per-brand-product USDT and toman prices with `auto_price_toman`, while each order records its actual payment currency and payment method.
- [x] Capture one product type per new order, multiple units and per-unit fulfillment intent.
- [x] Retain bounded-use prepaid 100% coupons, goodwill vouchers, 10/50/100% marketing codes, forced automatic best-code application and legacy-rounded USDT/toman pricing.
- [x] Remove the proposed `exchange_rates` history table and pricing-mode enum; preserve only typed current FX state and per-order rate/rounding inputs. **Do not store USDT/toman totals on orders**; actual money persists in payment/ledger evidence.
- [x] Reject the **quantity entry** 11 against a mandatory 100% discount with only 10 uses remaining in both currencies, show max 10 and create **no failed order**, without partial charging.
- [x] Switch `users` to PK `id` plus unique `(brand_id,telegram_id)`; reference customer membership via `orders.user_id` without duplicating brand/Telegram fields, with DB brand guards for product/coupons.
- [x] Capture `payment_due_at` and define 30-minute deadline for unpaid external-payment orders; warn on payment page, edit existing Telegram payment message on expiry, protect timely proof awaiting review/confirmation.
- [x] Permit independently USDT-priced checkout for manual/fixed-toman product listings.
- [x] Preserve brand-encrypted coupon code display for authorized “My Discounts” and notification paths.
- [ ] Verify historical legacy order product cardinality before removing `order_items`.
- [ ] Confirm any exceptional contractual prepaid-expiry terms or discovered historical paid-liability exceptions; **account-bound ownership and one N-use prepaid code per paid source order are approved**. Temporary external-payment coupon hold/release, crypto compensated/no-stock coupon return and **nonextension of expired marketing coupons** are resolved below.
- [x] Owner chose **reject quantity** for all 10%, 50% and 100% mandatory codes when remaining unit uses cannot cover the entire request; no mixed-price order and no failed order on quantity correction.
- [x] Preserve customer wallet **USD** denomination (do not rename to USDT); USDT checkout amount is mapped numerically into USD wallet shortage-credit units under the explicit 1:1 internal accounting rule.
- [x] Owner confirmed **USD-only** customer wallet scope; `wallets` PK `user_id` and `wallet_entries` FK `user_id` with **no currency columns**. Toman is a purchase/payment currency, not a wallet.
- [x] Owner confirmed each order's payment method/currency is immutable. Creating a **valid replacement** automatically **cancels an old `awaiting_payment` order** for the same brand membership and releases its coupon hold; a failed replacement leaves the old order intact, and under-review, confirmation-pending or paid orders are never cancelled automatically.
- [x] Preserve **standalone USD wallet top-ups** from legacy `main` using NOWPayments crypto only; `payments.order_id` XOR `payments.topup_user_id` (brand-member FK) eliminates the need for fake product orders or a new table. Product checkout supports TOMAN card-to-card, crypto with USD-valued quote, or existing USD wallet where permitted. Card funding of wallet is forbidden.
- [x] Owner approved NOWPayments crypto allowlist: **USDT/TRON TRC20 (`USDTTRC20`) and USDT/BNB Smart Chain BEP20 (`USDTBSC`) only** for new wallet funding and order checkout; no TRX native coin, BNB native coin or additional networks. No separate crypto-network lookup table is required.
- [x] Owner approved **one shared NOWPayments merchant account** for all three brands; use a single platform-scoped secret, globally unique provider-payment references and brand resolution from stored payment origin, not duplicated credentials in `brands` or a new provider-account table.
- [x] Owner approved **separate card-to-card receiving bank cards** for the two Iranian brands, **exactly one active per brand**, and **latest successfully saved card immediately replaces the old card for new orders**. Use revision-checked atomic updates, immutable bank-payment destination snapshots, keyed duplicate-card detection and no bank-card table.
- [ ] Inspect real banking-app payment preview for common account-holder names, which remain potentially identifying despite different card numbers.
- [ ] Audit actual NOWPayments hosted checkout/receipt/payee metadata for cross-brand merchant identity disclosure and obtain explicit operator acceptance or approved mitigation before going live.
- [ ] Verify legacy/provider payment denomination and actual provider-network availability before deciding whether `payments.kind/currency` columns are safely redundant. Keep external crypto asset, network code and actual received amount distinct from customer-facing USD store-credit units.
- [ ] Reconcile any unexpected **legacy non-USD wallet liabilities** before importing historical balances into the USD-only target.
- [x] Owner chose **automatic USD-wallet credit** for a fully verified full-value late USDT transfer bound to an expired/replaced original invoice, exactly once in that original brand. Uncertain/partial/excess/mismatched crypto and all late bank money remain staff-reviewed; no old-order reactivation or replacement rebinding.
- [ ] Verify provider-specific evidence timestamp and exact full-payment state to protect on-time unconfirmed crypto transfers and prevent wrongful late-payment credits.
- [x] Owner chose **Option A**: on creating an external-payment order, temporarily hold bounded coupon uses with `discount_uses(status='held')`; count held and redeemed capacity under a locked coupon, release on expiry or replacement cancellation, retain on timely proof/paid bank restock, redeem with successful fulfillment. **No stock reservation or new table.** One `awaiting_payment` order per `users.id` is database-enforced with a partial unique index.
- [x] Owner chose **Option A for crypto-paid/no-stock**: credit the exact frozen discounted USD amount into the same-brand wallet, deliver no product and **return the held coupon use** in one idempotent local transaction. No additional `discounts.redeemed_count` use; replayed provider callbacks do not repeat credit or coupon release.
- [x] Owner chose **Option A for marketing-code expiration after release**: keep original `ends_at`, leave already expired campaigns unusable for new purchases, never auto-replace a code, and preserve a validly held discount on an original under-review/on-time-protected order through fulfillment. Prepaid 100%-off future-unit paid entitlements default to `ends_at IS NULL` while an outstanding liability exists; never silently erase paid value.
- [x] Owner chose **Option A for prepaid-code ownership**: only the original paying customer within the original brand membership can reveal/hold/redeem a newly issued prepaid entitlement. For prepaid `discounts`, require `eligible_user_id`, product and paid source order with database brand/customer guards; other customers cannot redeem leaked code strings. Reconcile legacy codes without verifiable owner instead of silently reassigning them.
- [x] Owner chose **Option A for prepaid issuance**: one account-bound 100%-off coupon for `N` prepaid future units, with `max_total_uses=N`, allowing multiple later orders with variable quantity up to the remaining unit allowance. No advance stock reservation, no separate coupon row per purchased unit, and no partial failed order when requested quantity exceeds available entitlement.
- [x] Owner corrected **off-bot bank-resolution**: the bot cannot infer delivery/refund; administrators handle outcomes themselves. No automatic coupon redemption/release from unknown external events. Paid order/coupon hold remains until an explicit authenticated, audited staff adjustment. **Owner explicitly removed the prior cap-floor and prepaid-liability editing guard**: owner can change coupon percent, total/per-user usage limits, effective used count, expiration/start dates, activation and brand-scoped eligibility, including reducing a limit below redeemed+held or shortening prepaid entitlement validity. Signed `usage_adjustment_units` keeps effective-used overrides separate from historical `redeemed_count`; row locks and `audit_events` preserve attribution without vetoing the requested edit.
- [x] Owner approved adding typed append-only `discount_revisions` and `bot_activity_events` as 24th and 25th **design** tables (2026-10-29); no executable schema or implementation permission.
- [x] Owner selected tracking **all meaningful** private-DM customer bot actions with whitelisted typed context, per-brand membership and replay deduplication; no raw messages, callback payloads, secrets or JSON metadata.
- [ ] Finalize event taxonomy/privacy controls, FR-071 180-day cleanup/read/index/restore design, admin activity query scope and legacy `user_activity.details` reconciliation before DDL; the retention duration is approved below.
- [ ] Prove coupon edit version/actor linkage, immutable snapshots and correct at-edit actual redemption/held context in the reviewed final relational constraints.
- [x] Owner approved `supplier_request_attempts` as table 26, typed immutable evidence per actual supplier submit/lookup/export call; one order-level `supplier_requests` batch may produce several brand-encrypted per-unit `fulfillments`. The default for 3 delivered of 5 requested is two paid/unfulfilled pending explicit administrator review, never automatic re-purchase/compensation.
- [ ] Prove live supplier API idempotency, batch/remote-item identity, replayed exports and legacy `SupplierProcurementAttempt` import without duplicate delivery.
- [x] Owner rejected intentional partial customer delivery. For new orders hold the whole coupon claim until complete allocation/fulfillment redeems it once; FR-067 / SC-036 replace the partial-unit coupon choice. Historical partial-order migration and paid-cancellation policy remain gated.
- [ ] Operator approve remaining column compaction, historical supplier/product compatibility, **archive-only legacy refund preservation** and retained legacy pricing-policy decisions before final DDL.

## Source selection and local-preorder review (2026-10-29)

- [x] Record multiple supplier mappings per product, administrator-selected active source, and no automatic choice/fallback/split procurement.
- [x] Record automatic local-preorder oldest-paid-first allocation; complete-order waits keep priority, with no partial stock assignment/reservation or smaller-order bypass, separate from supplier incomplete-acquisition review.
- [x] Add FR-064/065, SC-033/034, US9 and future unchecked T093–T096; distinguish preorder backlog from immediate-sale failure/compensation.
- [x] Add pinned five-provider staging matrix with variant identity, idempotency and purchase-only credential recovery gates. No live validation claimed.
- [x] Owner confirmed one master-product active supplier selection shared across all three brands; no per-brand override. Existing bound requests retain their original supplier snapshot.
- [x] Owner approved source binding at accepted wallet/crypto/bank payment and retention of accepted payment plus paid/unfulfilled admin review when the selected source is disabled/unavailable; no silent fallback, automatic credit or lost obligation. FR-066 / SC-035 cover replay/race and unpaid-order cases.
- [x] Owner confirmed one configured account per provider across products/brands; no account pools or per-mapping credentials/account selection. Retain original account identity on paid requests and block silent account replacement during reconciliation.
- [ ] Finalize singleton provider configuration persistence and snapshot/blocker fields; finalize approved table 27 `product_supplier_sources` constraints. Approved physical baseline is 27.
- [ ] Finalize explicit admin preorder cancellation outcome/hold and optional-date notification policy (request channel resolved as support under FR-070), full-manifest access/transport guards and historical partial-delivery compatibility before executable schema or implementation.
- [ ] After implementation authorization, validate paid FIFO across brands, serialized allocation, complete-order/multi-message restart recovery and unchanged immediate-sale/supplier safety using approved fixtures.

## Notes

- The specification is now hosted on `main` in `Farrokhzadeh/telegram-multi-bot-shop`, imported from `Farrokhzadeh/AccAbad-Shop` commit `7fcc4a7a35337405dd69195fc6c92474748c4513`. The repository transfer is approved; backend implementation, migration DDL and runnable host code remain uncreated and unapproved. Original source anchors are retained for evidence.
- The owner resolved the original three critical questions: indefinite manual pending-restock with explicit admin off-bot resolution, frozen quote-value **USD wallet** compensation, and fully paused brand-by-brand cutover. Planning must still validate current live-deployment contracts against protected evidence.

## Complete-order correction (2026-10-29)

- [x] Record owner requirement: full requested quantity together, across local, supplier and preorder orders; no intentional partial customer fulfillment.
- [x] Rewrite supplier 3-of-5 as encrypted internal acquisition with zero customer release and whole paid order under admin review; preserve attempt/unit evidence and no automatic purchase/compensation.
- [x] Rewrite local FIFO to wait for the complete head-order quantity; preserve strict priority with no partial reservation or smaller-order bypass.
- [x] Add FR-067 / SC-036 and revise FR-063/065, SC-031/034 and future unchecked T092/T095/T096; the then-current 26-table baseline is subsequently extended to 27 by the source-mapping approval below.
- [ ] Prove complete-manifest access across credential/file/history routes, whole-claim coupon transition and resumable transport after implementation is authorized.
- [ ] Reconcile actual historical partial deliveries/batches without deleting evidence, changing settled history or redelivering old units.

## Product supplier source relation approval (2026-10-29)

- [x] Owner approved `product_supplier_sources` as physical design table 27; add inventory row, key entity, logical relation and updated task/validation coverage.
- [x] Preserve shared master-product selection, one provider account, accepted-payment immutable request provenance, unavailable-source review and complete-order delivery.
- [ ] Finalize null-aware mapping uniqueness, same-product active selection, referenced mapping lifecycle, paid source-revision/snapshot constraints and protected provider configuration before DDL.
- [ ] Verify mapping/selection/payment concurrency and historical source provenance after implementation is authorized; all implementation tasks remain unchecked.

## Supplier product scope approval (2026-10-30)

- [x] Record ready-to-deliver accounts/keys/codes only for first-release automated supplier products, without customer-provided fulfillment inputs.
- [x] Exclude input-collecting activation/slot/upgrade and manual/custom supplier delivery; preserve ordinary delivered-code use, asynchronous processing and complete-order release.
- [x] Add FR-068 / SC-037 and existing future T057/T094 coverage; 27-table count unchanged, only repository setup T001 complete.
- [ ] Verify exact provider product/variant eligibility, publication/pre-purchase guards and post-payment type-change admin review using authorized fixtures before implementing adapters.
- [ ] Reconcile historical excluded product obligations without silently dropping or reclassifying source evidence.

## Optional local-preorder date approval (2026-10-30)

- [x] Owner approved local preorders without dates, valid NULL estimates and no invented delivery promise.
- [x] Add FR-069 / SC-038 and future T095/T096 coverage: nullable create/publish/checkout, immutable quote evidence, authorized update/clear and no date-driven paid-state/queue/coupon/money transition.
- [x] Keep unpaid payment deadline separate and 27-table count unchanged.
- [ ] Specify automatic estimate-change notification behavior and actual admin paid-cancellation outcome/hold policy before implementation; request channel is support under FR-070.
- [ ] After implementation is authorized, validate nullable fields and missing/passed/edited/cleared estimates using synthetic fixtures.

## Support-based cancellation request approval (2026-10-30)

- [x] Owner chose existing support tickets for paid queued preorder cancellation requests; no dedicated button/endpoint/table or automatic queue hold.
- [x] Add FR-070 / SC-039 and future T025/T026/T046/T096: same-owner/brand order links, support-operation non-mutation, manual staff response and audited actual admin actions serialized with allocation.
- [x] Keep paid history and external-refund exclusion; no automatic coupon/wallet/stock state changes from ticket text/status. Table count remains 27.
- [ ] Settle actual admin cancellation outcome/hold and coupon/internal-wallet policy separately; request-channel approval does not settle paid liabilities.
- [ ] After implementation authorization, verify support replay/isolation and explicit admin-resolution/allocation races.

## 180-day activity retention approval (2026-10-30)

- [x] Owner chose 180 elapsed days for nonfinancial `bot_activity_events`, from original trusted UTC event time; financial/order/support/admin-audit history remains under separate policies.
- [x] Add FR-071 / SC-040 and future T087–T089: shared read/cleanup cutoff, boundary equality retained, bounded restart-safe delete role, no domain cascade/indefinite raw archive/replay retimestamping.
- [x] Preserve separate protected source/backups retention and require cutoff before target restore/import exposure; no live deletion or table-count change.
- [ ] Finalize taxonomy/privacy, cutoff index, permissions, scheduling/backlog monitoring and backup restoration details before implementation.
- [ ] After implementation authorization, validate expiry boundaries, cleanup/replay/concurrency recovery, domain-history isolation and independent financial idempotency.
