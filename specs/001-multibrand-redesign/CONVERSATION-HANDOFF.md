# Multi-brand redesign — conversation handoff (updated 2026-10-30)

**Repository:** `Farrokhzadeh/telegram-multi-bot-shop`  
**Branch:** `main`  
**Imported source:** `Farrokhzadeh/AccAbad-Shop` at `7fcc4a7a35337405dd69195fc6c92474748c4513`  
**Feature documents:** `specs/001-multibrand-redesign/`  
**Status:** Specification/architecture only. No production migration, implementation, frontend, or deployment authorized.

## Where to continue

Start the next conversation with: “Continue AccAbad multi-brand redesign spec-kit in `Farrokhzadeh/telegram-multi-bot-shop`, branch `main`, from `specs/001-multibrand-redesign/`. Review the handoff, spec, plan, data-model, database-table-review, research, quickstart and tasks. Continue schema clarification; do not implement without review.”

## Accepted architecture

- Three independently branded Telegram stores; one private unified management backend; backend-only scope with C#/.NET Native AOT and PostgreSQL. AI answering is separate and optional.
- Brand-scoped `users` rows, internal `user_id` on orders, one USD-denominated wallet per brand membership. Customer bots use private chats only. No cross-brand customer-facing identity or wallet leakage.
- Shared internal product catalog (first store as active baseline), per-brand presentation overrides with field-by-field inheritance, independent prices, shared local stock; no long-lived stock reservations. Immediate sales use atomic allocation plus wallet debit and fail out-of-stock without debit. Approved local preorders instead retain paid FIFO obligations as clarified below.
- The compact target schema is a **27-table, typed-column/no-JSONB candidate** (original 23 plus approved `discount_revisions`, `bot_activity_events`, `supplier_request_attempts` and `product_supplier_sources`), pending legacy-data and invariant verification. The database gets a brand-neutral name. No exchange-rate history table; store current rate in configuration and the actual applied rate on each relevant order. Preserve legacy pricing/rounding behavior and per-product automatic toman switch.
- One product type per order, possibly multiple units. Order price snapshots are immutable inputs to calculations; no redundant order total fields. USD unit price; nullable toman fields/rate on USD-only orders. Payment method/currency fixed for the life of an order.
- Unpaid order payment window: 30 minutes, with expiry shown and Telegram message edited. One active waiting-for-payment order per user/brand; a valid new order cancels the previous *unpaid/undetected* one, releasing coupon holds. A receipt submitted on time, detected crypto payment, or paid/unfulfilled order is protected. Late verified crypto: credit original brand USD wallet once; late bank payments: manual reconciliation.
- Customer wallet top-ups: NOWPayments crypto only. Product checkout: USD wallet, NOWPayments crypto, or card-to-card as eligible. Crypto **USDT only**, on **TRC20** and **BEP20**; no other assets. Customer-facing wallet denomination is **USD**, despite use of USDT for settlement. One shared NOWPayments merchant account across three brands. Shared provider credentials are protected outside the database or encrypted under a separately stored key.
- Each Iranian brand has a separate bank card, **one active card per brand**; the most recently successfully saved card becomes active. Prior orders retain their original destination snapshot for receipt review.
- Coupons are applied automatically in all relevant currencies using verified legacy pricing/rounding logic. A requested quantity greater than the applicable coupon's available unit allowance is rejected at quantity selection (not as a failed order), without partial discount, stock allocation or charge.
- During unpaid checkout coupon uses are **held**, not consumed; confirmed fulfillment redeems them. Unpaid expiry/cancellation releases them. For immediate (non-preorder) orders, paid crypto without stock credits the discounted paid USD amount to wallet **and releases coupon usage** atomically/idempotently. Campaign coupon expiry is never extended when usage is returned; paid legitimate pending orders preserve their locked discount.
- Prepaid 100-unit entitlement is **one 100%-off coupon with 100 unit uses**, **bound to the original customer**, not transferable between accounts/brands. Prepaid entitlements do not expire automatically by default.
- **Owner coupon control supersedes earlier safeguards:** owner may edit coupon percentage, start/end time, enabled status, max uses, effective used count and remaining allowance after creation, including reducing below redeemed/held numbers. Keep actual redemption rows immutable, store effective adjustment separately (e.g. `discounts.usage_adjustment_units`), and audit owner edits. Existing order snapshots are not rewritten.
- Confirmed bank payment with no stock waits indefinitely for restock or owner-managed outside-bot resolution. The bot cannot infer outside-bot actions. Supplier API uncertain outcomes are reconciled before compensation.
- Migrate the three legacy databases brand-by-brand with all writers stopped for the migrating brand and in-flight payments reconciled; first-store product catalog only for new active catalog. Decrypt legacy sensitive fields using their original per-brand keys, re-encrypt necessary secrets under new versioned keys (brand-owned secrets per-brand, shared inventory under a platform key), verify restore/reconciliation, then retire legacy keys only when backups/rollback no longer depend on them.
- Paid local preorders have no configured availability date/ETA or fulfillment time cap. No date inputs or date-change notifications; actual full stock or explicit authorized resolution controls completion.
- New target orders deliver only the complete requested quantity; supplier subsets stay internal and local preorders wait for full stock. See the latest FR-067 correction below.
- No GUI admin panel in this scope; secure administrative APIs and audit trail only.

## Important follow-up checks

1. Inspect live repository documents for any residual contradictions: older notes may still call coupon edits restricted, wallet currency USDT, or payments able to switch methods; the decisions above take precedence.
2. Review `wallet_entries` optional fields (`balance_after`, `source_type/source_ref`, `is_posting`) for normalization while preserving audit and migration correctness.
3. Confirm source-data snapshots and crypto callback details with provider tests before DDL/cutover.
4. Cross-check `tasks.md` coverage after the last coupon-owner-edit and card-replacement clarifications; keep schema compact without sacrificing constraints.

**Not performed:** Code changes, deployment, production database migration, key access, or deletion of old backups.


## Subsequent schema-review pass (2026-10-28)

A second, **pending-only** review was added to [normalization-audit.md](normalization-audit.md#second-pass-integrity-and-compaction-gate-2026-10-28) after comparing the proposed inventory with current `main` Django product, supplier, payment, wallet and coupon models. **No DDL, production code, migration, provider change or accepted table-removal decision resulted. The approved 23-table candidate is unchanged.**

Before freezing physical tables, decide how to retain typed **before/after coupon-edit audit history** (a narrow 24th `discount_revisions` table is recommended versus sparse typed coupon snapshot columns in `audit_events`); prove supplier batch-to-many-unit results and encrypted delivered content, legacy preorder/refund and product-variant fields, and any real IRR wallet liabilities. Normalize `wallet_entries` source/idempotency links only with typed origins and posting-history proof, and keep `payments.kind/currency` until old USD invoice versus USDT network and TOMAN order evidence is reconciled. The new audit lists conditional, low-risk column removals afterward. **A compact table count is not an override for missing financial/audit relations.**

## Owner-approved history additions (2026-10-29)

- **25-table design direction approved** (not executable DDL): the 23-table base plus `discount_revisions` for immutable typed coupon issuance/edit settings, and `bot_activity_events` for meaningful private-DM customer commands, navigation, product views, button actions, quantity/checkout flow, genuine abandonment and actionable errors. General `audit_events` still owns privileged actor/time/reason; money, orders and redemptions remain authoritative in their own tables.
- Coupon revisions must link to an `audit_events` row, snapshot all editable state plus real redeemed/held context at edit time, and commit with `discounts.revision` atomically. Owner edit authority remains unrestricted under FR-058; existing orders and redemption history never change.
- Activity events use `users.id` brand membership, optional typed brand-checked product/order/support references, a controlled event taxonomy, deterministic replay-dedupe keys and no raw DM/callback/private payment/credential content. Legacy Django `user_activity` exists; reconcile its free-form details carefully. The owner subsequently approved 180-day operational retention under FR-071 below; privacy/event-code and cleanup/recovery design remain reviewed before DDL.
- **Open compaction gates remain:** supplier batch-to-many-unit encrypted outputs, product/service/preorder historical evidence, source refund **archive-only** preservation, source IRR liabilities, wallet ledger reference normalization, payment quote denomination, and tenant constraints. The 25-table count is a candidate, not a hard limit if a real independent business relation proves necessary.
- Updated `spec.md`, `plan.md`, `data-model.md`, `database-table-review.md`, `normalization-audit.md`, `research.md`, `contracts/backend-contracts.md`, `quickstart.md` and `tasks.md` to carry the approved design direction and future (unchecked) T086–T089 work. **No code, DDL, production migration, deployment or live logging performed.**

## Owner decision — external refunds fully off-platform (2026-10-29)

- The owner explicitly rejected a `refunds` table and any in-application record of administrator-executed bank/crypto/supplier/preorder refunds. Staff handle both the transfer and its records externally; do not create refund API endpoints, refund status fields, payment-out rows, external refund references, or refund-reporting workflows in the new operational schema. The approved candidate remains **25 tables**; the suggested `refunds` table was never approved.
- Preserve the distinction between external manual refunds and **internal USD-wallet credits** already approved for verified crypto/no-stock and late-payment recovery. These internal wallet movements remain durable `wallet_entries` and are not an external refund workflow.
- Off-platform administrator activity is not observable to the bot: an order awaiting restock stays an outstanding paid obligation until an explicitly authorized order-state action occurs. Audit any order-state edit itself, but do **not** record an external payout or infer one from the edit.
- Existing legacy supplier/preorder refund rows contain historical financial evidence. They must not be silently discarded: retain authenticated read-only legacy exports/backups for independent migration/accounting verification and obtain explicit archive/retention signoff before cutover. **Do not** introduce an operational refund table merely to import those rows. Whether external archives satisfy the earlier complete-history requirement remains a migration gate.
- Subsequent approval added supplier attempt history as **table 26**; see the new supplier decision section below. Bot-activity duration is now 180 days; exact cleanup/privacy/restore design remains open. Review only; no implementation authorized.

## Owner-approved supplier attempts and incomplete-unit review (2026-10-29; delivery policy corrected below)

- The schema candidate now has **26** tables: the original 23, `discount_revisions` (24), `bot_activity_events` (25) and the newly approved **`supplier_request_attempts` (26)**. This table stores per-call typed submit/status-lookup/export outcome evidence; it is separate from worker `jobs` scheduling retries. No new refund relation or external refund API is authorized.
- A `supplier_requests` row is an **order-level supplier batch** with `order_id`, `requested_quantity`, stable local intent key (remote idempotency only when proven) and a status including `partial_pending_admin`; it no longer targets one `fulfillment_id`. Every returned unit maps to a same-order `fulfillments.supplier_request_id`, with brand-key-encrypted delivered content and safe remote-unit deduplication. A one-call/five-unit purchase may create three encrypted acquired unit rows; under the latest correction these remain internal, zero items are customer-delivered and the whole order awaits review.
- **Current corrected default for incomplete supplier acquisition:** retain acquired units encrypted internally, release zero customer items and keep the whole paid order under administrator review until a complete set and review clearance. No automatic purchase, credit, fallback or outside refund. Earlier three-now/two-later delivery is superseded.
- Still unresolved before DDL: deployed provider support for stable idempotency and item IDs, how to deterministically reconcile replayed/partial exports, legacy supplier attempt/import fidelity, and historical partial-order compatibility and complete-manifest access guards. New target coupon claims stay whole until complete fulfillment. Bot-event duration is 180 days; its cleanup/restore design, IRR liabilities and other column normalization remain separate gates.
- Spec-kit documents only; no code, migration, live API calls, supplier purchasing, refunds or deployment performed.

## Owner-approved source selection and local-preorder FIFO (2026-10-29)

**Latest decisions:** one product supports **several supplier mappings; administrator selects the active source**. Local restocking allocates **automatically, oldest paid order first**. Added FR-064/065, SC-033/034, US9 and unchecked future T093–T096.

- No automatic supplier choice/fallback/split. Freeze provider/account/external product/optional variant on a request before remote purchase; later active-source edits never redirect old retries/reconciliation. Local intent keys do not prove provider idempotency. Source selection is shared at master-product scope across all three brands, with no per-brand override; binding time is approved at accepted payment as clarified in the latest decision below.
- `product_supplier_sources` is now approved as table 27, superseding this section's earlier pending recommendation. Protected provider configuration persistence and verified transport relationships remain under review; no account pool or split-delivery table is justified by the current new-order behavior.
- Local preorders are fully paid order/unit obligations, separate from prepaid coupon issuance. Shared-product FIFO uses accepted paid time plus order-ID tie-breaker; queued orders retain priority and allocate only when their complete quantity exists, without partial assignment/reservation or smaller-order bypass. Restocking commits durable drain work. Payment/allocation recovery never repeats debit or item assignment. Intentional preorder shortage does not cause immediate-sale rejection, crypto shortage credit or unpaid timeout.
- Supplier partial/unknown quantities still await admin review; they are not automatic local preorder backlog.
- [Provider and preorder reconciliation](contracts/supplier-provider-matrix.md) pins all five staging adapters and the preorder handoff at `5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d`. Source review only; no live staging/provider verification. Canboso purchase-only credentials and RoboticVN/RapidX uncertain purchases remain recovery gates.
- **Next review:** proposed source-mapping relation, singleton provider configuration persistence and blocker field design, then remaining preorder cancellation state/hold and coupon rules and historical partial-order compatibility. External refunds and records stay off-platform. No code/DDL/migration/deployment or live supplier calls. All 96 implementation tasks remain unchecked.

## Owner decision — one active supplier for all brands (2026-10-29)

- Changing a product's active supplier affects **all three brands together**. Keep one authoritative master-product selection, with no per-brand supplier override.
- Several supplier mappings remain available. An authenticated, audited admin selection affects newly bound requests; already bound requests retain their original source/account/product/variant snapshot for retry and reconciliation.
- Physical design still proposes one checked `products` pointer or one filtered unique selection per `product_id`, not duplicate per-brand settings. One account per supplier is now approved; protected configuration persistence remains open; the lifecycle binding point is now accepted payment, per the later decision below. Current baseline is **27**, including the now-approved `product_supplier_sources`.
- Updated FR-064 / SC-033 and corresponding model/contract/backlog/validation notes. Counts remain 65 requirements, 34 success criteria, 96 unchecked tasks. Specification only; no code, DDL, migration, deployment or live supplier calls.

## Owner decision — accepted-payment supplier binding and unavailable-source review (2026-10-29)

- Source binding is **at accepted payment**, superseding the earlier order-creation recommendation: committed wallet debit, verified crypto accepted for the live order, or staff-approved bank payment. Invoice opening and unapproved receipt submission do not bind.
- Bind the then-current shared master-product source/account/product/variant with paid state and durable procurement intent locally; call the supplier outside the transaction. Keep customer quote terms fixed and never rebind on replay or later source switches.
- Owner explicitly approved: **if the selected source is disabled or cannot supply at confirmation, retain payment and paid/unfulfilled liability for administrator review; no silent switch or lost obligation**. Missing selections follow the same review default, retaining known source evidence without fabricating a source/runnable request. No automatic wallet credit, external refund or purchase. Re-enablement alone does not clear staff review.
- Added FR-066 / SC-035 and updated the model/contracts/future T093–T094 validation. At this earlier decision: **66 requirements, 35 success criteria, 96 unchecked tasks; 26 baseline tables**, subsequently extended by the approvals below. The then-proposed `product_supplier_sources` is subsequently approved as table 27 in the latest decision below. No implementation/DDL/migration/deployment or live API calls.
- **Partial coupon question superseded:** the owner requires complete-order delivery under the latest FR-067 clarification below. Keep whole claims held until complete fulfillment. Remaining preorder cancellation state/hold and coupon rules, protected singleton provider configuration persistence and exact blocker/transport relations remain open.

## Latest owner correction — deliver the complete order, never an intentional subset (2026-10-29)

**Authoritative correction:** five requested items must be fulfilled as one complete five-item order. This replaces earlier supplier/preorder partial-delivery examples and the suggested per-unit coupon choice.

- Local preorder FIFO stays oldest-paid-first but assigns no stock until the head order's complete quantity exists. No partial reservation or smaller/newer-order bypass. Full assignment, whole coupon redemption and delivery intent are atomic.
- Supplier 3-of-5 is **incomplete acquisition**, not partial customer fulfillment: persist three encrypted internal items, release zero customer credentials/files/links and retain the whole paid order for admin review. No automatic new purchase, source switch, compensation or external-refund record.
- Hold the full coupon claim until complete allocation/fulfillment redeems it once. No new per-unit coupon-splitting relation is needed. Paid-cancellation state/hold and coupon rules remain separately open; outside-shop repayment is approved in ADR-43; do not infer outside refunds or release from unobserved staff actions.
- Prepare one full order manifest before customer access; resumable multi-message Telegram sending is transport recovery, not permission for three-now/two-later business fulfillment. Preserve per-unit identity and attempt evidence.
- Do not erase historical staging partial deliveries or redeliver old units. A migration compatibility plan for actual old partial/batch history remains required.
- Updated FR-063/065, added FR-067 / SC-036 and revised US5/US9 plus future T092/T095/T096. **At this earlier decision: 67 requirements, 36 success criteria, 96 unchecked tasks and 26 baseline tables**, with source mapping subsequently approved as table 27 in the latest decision below. No code/DDL/migration/deployment or live supplier calls.

## Latest owner clarification — one account per supplier (2026-10-29)

- Owner confirmed **one account per supplier is enough**. Reuse one configured account per provider across all product mappings and all three brands. No multiple-account pools, per-brand supplier accounts or independent per-mapping account selection.
- Approved `product_supplier_sources` candidate fields are `id`, `product_id`, `provider_code`, `external_product_id`, nullable `external_variant_id`, `enabled`, `revision`, timestamps; the provider resolves one protected configuration. Remove the separately selectable `supplier_connection_ref` from this proposal, not from running code/schema.
- Recommend typed provider configuration with external protected credential references; exact persistence remains under review. No additional supplier-account table is authorized merely for multi-account support.
- Keep actual account identity on paid-request snapshots. Same-account key rotation is allowed without rebinding; account replacement cannot silently redirect old requests. Missing original-account access requires administrator review, with no automatic purchase/credit.
- FR-064 / SC-033 and future T093/T094 checks updated. **67 requirements, 36 success criteria, 96 unchecked tasks; baseline now 27 approved tables** after the source-mapping approval below. No implementation/DDL/migration/deployment or live supplier calls.
- **Next review:** final constraints and protected provider configuration, FR-068 provider eligibility evidence, then preorder cancellation state/hold and coupon rules. The source-mapping relation/table count decision is resolved below.

## Latest owner approval — product_supplier_sources is table 27 (2026-10-29)

- Owner explicitly approved **`product_supplier_sources` as table 27**. Current approved schema candidate: **27 tables** (original 23 plus coupon revisions, bot activity, supplier attempts and product/supplier mappings).
- Candidate fields: `id`, `product_id` FK, controlled `provider_code`, `external_product_id`, nullable separate `external_variant_id`, `enabled`, `revision`, timestamps. Enforce duplicate/no-variant identity and one same-product active selection shared across all brands. One configured account per provider, no per-mapping credentials/account selector.
- Paid requests retain source-ID/revision provenance plus immutable actual provider/account/product/variant snapshots. New catalog edits never redirect an old request. Disabled/unavailable source keeps the paid order under admin review; complete-order-only delivery/coupon policy is unchanged.
- This supersedes all earlier pending-table/count notes. Exact FK/uniqueness/selection representation, provider config persistence and proof of the approved ready-to-deliver eligibility boundary remain reviewed before DDL.
- Updated 12 specification files, including physical inventory row 27, FR-064/SC-033, audit/contract/validation and future T093/T094 coverage. Counts remain **67 requirements, 36 success criteria, 96 unchecked tasks**. No code/DDL/migration/deployment/live API calls or new refund/account-pool/split-delivery relation.

## Repository transfer — current continuation location (2026-10-29)

- Current specification repository: **`Farrokhzadeh/telegram-multi-bot-shop`**, branch **`main`**. Imported on 2026-10-29 from `Farrokhzadeh/AccAbad-Shop`, branch `feature/001-csharp-native-aot`, commit [`7fcc4a7`](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).
- Continue the existing feature directory `specs/001-multibrand-redesign/`; do not generate a second feature or reset approved decisions. `.specify/feature.json` selects this feature for the imported PowerShell tooling.
- Approved candidate remains **27 tables**, with 67 requirements, 36 success criteria and 96 planned tasks. Repository setup T001 is complete under the transfer authorization; the remaining 95 tasks are unchecked and application implementation remains deferred. One account per supplier, shared product-level selection across brands, accepted-payment binding, blocked-source admin review and complete-order-only delivery remain unchanged.
- The 12 feature documents and existing spec-kit templates/scripts/skills were transferred. Legacy Django code, application spikes, deployment workflows, runtime configuration, databases and credentials were not transferred or executed. Historical source references still point to the original repository.
- Original fleet constitution is retained unchanged as imported source governance. Its old physical-fleet/schema assumptions need reconciliation with the accepted multi-brand design before implementation; importing it does not revoke the owner's approved decisions or ratify a new deployment policy.
- The user authorized this repository transfer, not implementation, migrations or deployment. The original repository remains the legacy source reference; `door-bell` was not modified.

## Latest owner decision — supplier items ready to deliver only (2026-10-30)

- First-release automated supplier products are **ready-to-deliver accounts, keys and codes only**, requiring no customer-supplied email, existing-account credentials, OTPs or other fulfillment inputs. Exclude activation/slot/upgrade flows requiring such inputs and manual/custom supplier delivery.
- Normal customer redemption/use of an already delivered code is allowed. Provider processing/status/export may remain asynchronous; no synchronous-success guarantee is implied. Complete-order-only customer release, one supplier account and accepted-payment source binding remain unchanged.
- Verify exact product/variant eligibility at mapping publication and pre-purchase; unknown/excluded requirements block payable API checkout. If discovered after payment, retain original source and paid/unfulfilled admin review under FR-066; no fallback purchase or automatic compensation.
- Added **FR-068 / SC-037** and updated existing future T057/T094 coverage. **27 tables, 68 requirements, 37 success criteria, 96 tasks: T001 setup complete, 95 unchecked.** No implementation/DDL/migration/deployment/live provider calls or new input/activation table.
- **Next business clarification:** remaining local-preorder cancellation state/hold and coupon rules, bot activity cleanup/privacy design (180-day duration approved under FR-071). The no-date/no-paid-time-cap correction is authoritative below. Final provider proof, wallet/constraint/migration review and legacy-constitution alignment remain open.

## Historical optional-date interpretation — superseded by the latest correction below

The earlier “without a date” answer was interpreted as optional admin estimates. The owner has clarified no preorder dates/times will be defined and no paid fulfillment time cap exists. Revised FR-069 / SC-038 and the latest correction below remove the proposed nullable product/order date fields and date-change notification question. No implementation or migration occurred.

## Latest owner decision — cancellation requests through existing support (2026-10-30)

- Customers requesting cancellation of a paid local preorder waiting for stock use **existing support tickets**, linked to their same-member/brand order. Staff decide/respond manually using existing support/admin facilities.
- No dedicated cancellation button, endpoint, table or automatic queue hold/request state. Ticket creation/replies/assignment/closure/reopening and message text do not cancel/pause/reorder an order, release coupons, move money or prove an external refund.
- An actual order resolution requires a separate authorized actor/reason-audited admin action under reviewed outcome rules, serialized against allocation and retaining paid history. The support-channel choice alone did not decide payment disposition; the later ADR-43 approval below fixes outside-shop repayment without cancellation wallet credit. Coupon and exact admin state/hold rules remain separate.
- Added **FR-070 / SC-039**, updated future T025/T026/T046/T096. **27 tables, 70 requirements, 39 success criteria, 96 tasks; T001 setup complete, 95 unchecked.**
- No application code/DDL/migration/deployment/live calls. Remaining business choices: remaining admin cancellation state/hold and coupon rules, and bot activity cleanup/privacy implementation details (duration is 180 days under FR-071); final constraints/provider/migration proof and legacy-constitution alignment still need review.

## Latest owner decision — 180-day nonfinancial bot activity retention (2026-10-30)

- Owner selected **180 days** for operational `bot_activity_events`. Use rolling elapsed days from original trusted UTC `occurred_at`; reads/cleanup expire strictly older rows and retain the exact cutoff boundary.
- Unexpired activity is append-only. A privileged bounded/restart-safe maintenance path deletes expired activity, using existing jobs/indexes; no new table or automatic indefinite raw-event archive.
- Activity expiry never deletes/cascades into financial, order, coupon/revision, support, delivery or admin-audit history, and business idempotency is independent. These records and source/backups have separate retention policies.
- Do not reintroduce expired activity by retimestamping replays; apply the cutoff before target restored/imported activity is exposed. This does not authorize deleting protected source archives/backups or purging live data.
- Added **FR-071 / SC-040**, updated future T087–T089. **27 tables, 71 requirements, 40 success criteria, 96 tasks; T001 setup complete, 95 unchecked.**
- Remaining review: event taxonomy/privacy and technical cleanup/restore behavior; remaining admin cancellation state/hold and coupon rules; final wallet/FK/provider/migration proof and imported-constitution alignment. No code/DDL/migration/deployment/live collection/deletion.

## Latest owner correction — preorders have no dates or paid fulfillment time cap (2026-10-30)

- Owner clarified that **admins will not define times for preorders and there is no paid fulfillment time cap**. No availability estimate/date/ETA, promised date, maximum paid wait or date-change notifications. Remove the earlier product estimate and quoted-date snapshot proposals; do not implement their inputs/jobs.
- Confirmed-paid orders stay in oldest-paid complete-order FIFO indefinitely until full stock or an explicit authorized admin resolution. Time alone cannot cancel/reorder them, release coupon holds or move money. Support requests still do not mutate orders.
- Keep technical created/paid timestamps and the separate previously approved 30-minute unpaid external invoice window. That payment window is not a time cap on paid preorder fulfillment.
- Preserve/reconcile genuine legacy date promises as source historical evidence under FR-027; do not silently erase them or expose new target date configuration.
- Revised existing **FR-069 / SC-038**, updated model/inventory/contracts/review/future T095/T096 and recorded ADR-42. **27 tables, 71 requirements, 40 success criteria, 96 tasks; T001 setup complete, 95 unchecked.**
- No code/DDL/migration/deployment/live calls. The date-change notification question is removed. Remaining review covers remaining admin cancellation state/hold and coupon rules, event taxonomy/privacy/cleanup details, final wallet/FK/provider/migration proof and inherited-constitution alignment.

## Latest owner decision — approved preorder cancellation repayment stays outside the shop (2026-10-30)

- For an approved paid local-preorder cancellation, the administrator handles repayment outside the shop. The cancellation creates no wallet credit or reversal of the original wallet debit, no payout execution and no external payout amount/reference/receipt/status record. Keep original accepted payments, debits and obligation history; audit the authorized order-state action with actor/reason, without asserting or proving repayment completion. Support activity and unknown outside actions remain non-authoritative. Held-coupon disposition and exact admin cancellation state/hold transitions still require separate review. This does not alter independently approved late-crypto or immediate-sale shortage wallet compensation. FR-070 / SC-039; 27 tables unchanged.
- This revises existing FR-070 / SC-039 and future T046/T096. No automatic cancellation, support-triggered hold or coupon release is approved. Next business clarification: disposition of the whole held coupon claim when an admin actually approves cancellation.
- **27 tables, 71 requirements, 40 success criteria, 96 tasks; T001 setup complete, 95 unchecked.** No application code/DDL/migration/deployment/live money or provider actions.
