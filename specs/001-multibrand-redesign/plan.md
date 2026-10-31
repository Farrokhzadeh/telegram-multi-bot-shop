# Implementation Plan: Multi-Brand Commerce Backend

**Feature**: `001-multibrand-redesign` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)  
**Specification repository**: `Farrokhzadeh/telegram-multi-bot-shop`, branch `main`; imported from `Farrokhzadeh/AccAbad-Shop` at `7fcc4a7a35337405dd69195fc6c92474748c4513` (implementation and deployment remain unapproved)  
**Status**: Backend-only architecture plan, suitable for task breakdown after source evidence; no deployment authority.

**Current review:** [consistency-review.md](consistency-review.md). Types/relationships are selected in [schema-contract.md](schema-contract.md); ownership is mapped in [requirements-traceability.md](requirements-traceability.md). Older dated clarification notes below are rationale, not competing field alternatives.

## Summary

Build a lean, brand-neutral backend serving three independently presented Telegram shops. One shared commerce core controls brand-scoped users/wallets, a shared catalog and inventory, atomic wallet checkout, asynchronous external payment reconciliation, supplier fulfillment, campaigns, and brand-independent operational configuration. Each brand has its own bot token, encryption keyring and state. Administration is exposed via authenticated APIs **only**; no dashboard, frontend, UI framework or website is part of this plan. AI answering is a separately deployable service, outside the commerce codebase's execution path.

The historical `feature/001-csharp-native-aot` branch is a source of proven ideas/probes, not a greenfield codebase to merge wholesale. Existing legacy shops remain operational under their existing owners until each brand's separately authorized freeze. Captured source evidence is read-only; review/migration tooling must not alter production sources.

## Technical Context

| Concern | Decision / evidence gate |
|---|---|
| Language | C# / .NET 10 LTS, pinned via SDK config |
| Native publish | Linux x64 Native AOT for workers and backend API, with clean publish+run evidence for every dependency; Linux ARM64 separately qualified |
| API host | ASP.NET Core Minimal APIs, source-generated JSON, explicit request validation and authentication; no MVC, Blazor or server-rendered UI |
| Telegram | Per-brand long-polling consumer initially, or verified webhook mode if existing ingress requires it; a single update consumer per token |
| Persistence | PostgreSQL, Npgsql/direct explicit SQL, migration scripts applied deliberately; avoid experimental EF Native AOT dependency |
| Relational schema | **No JSON/JSONB/EAV columns in v1**; owner-approved 28-table candidate (23-table base + 3 history relations + `product_supplier_sources` + `product_stock_subscriptions`); **USD-only wallets keyed by `user_id`**, customer private-DM `users.flow_*` and no redundant delivery `chat_id`; approved typed `discount_revisions` coupon-edit history and `bot_activity_events` customer interaction history; current FX fields on brands, accepted rates only on orders; pending proof of one-product legacy orders |
| Encryption | Migration-only legacy Fernet/key derivation reader; new authenticated ciphertext with **new per-brand keys for brand-owned data plus a distinct platform shared-inventory key**; AES-256-GCM candidate after golden-vector and Linux AOT tests |
| Jobs | PostgreSQL outbox, leases, idempotency keys and retry/reconcile states; no memory-only durable obligations |
| Administration | Private authenticated, audited admin API; all admins can manage all brands; **no graphical UI** |
| AI | External optional service integration via tightly scoped contract, no AI provider SDK or model implementation in commerce |
| Deployment | Separate bot consumers, core API and jobs as logical hosts; consolidate processes only after fault-isolation and RSS benchmarking |
| Testing | Pure rules, Postgres integration, provider/Telegram simulations, AOT executable smoke, migration reconciliation, staged golden flows |
| Performance | No invented RAM/startup thresholds; measure old fleet vs new under representative load before selecting deployment shape |
| Security | Zero cross-brand customer leakage, brand-scoped reads, per-brand keyring, secret files, audit and strict payment callback verification |

## Constitution Check

Constitution 3.0.0 is active in `.specify/memory/constitution.md`: principles II/III authorize the approved shared target, and principle IV specifies exactly-once business effects, saved-order-message editing and explicit transport-uncertainty recovery. The owner authorized these governance/documentation changes; the 2026-10-31 read-only alignment reanalysis passed and closes E1 for governance alignment only. Continue qualifying the following controls; no implementation or cutover permission is implied:

1. **Isolation**: Every customer or order operation has explicit brand context; data layer constraints and negative tests prove no cross-brand access.
2. **Money**: Immediate local wallet checkout debits only with complete atomic stock allocation. Approved local preorders instead commit full payment and durable FIFO obligations without partial allocation; stock assignment waits for the complete quantity; provider callbacks and authorized wallet compensation remain exactly once.
3. **Durability**: Payments, supplier outcomes, inventory allocation and deliveries survive process failure and never rely solely on in-memory queues.
4. **Crypto/key migration**: Legacy decrypt and new encrypt verified record-by-record; preserve old backups/key escrow until no recovery dependency remains.
5. **Controlled cutover**: No live instance, schema or key changes before snapshot, source inventory, restore drill, in-flight reconciliation and owner approval.
6. **Small VPS**: Native AOT is an optimization objective verified by published executable, CPU/RSS/startup measures, and functional checks—not a reason to weaken correctness.

**Current assessment, 2026-10-31**: constitution amendments are applied; the 2026-10-31 alignment reanalysis passed and E1 is closed for governance only. E2–E6 remain stage-specific evidence gates. No implementation, infrastructure or cutover approval is inferred.

## Staging environment — same production server

The owner selected same-server staging on 2026-10-30. [staging-plan.md](staging-plan.md)
defines the proposed separate runtime/database/volumes/test bots/keys, fake integrations,
resource qualification, safe reset/restore and manual promotion sequence. Three test brands
exercise the shared target. The host remains a common failure boundary; staging must stop
independently and host-destructive tests are excluded from this server. Actual resources and
identities remain unverified. No deployment or constitution ratification is implied.

## Project Structure

```text
src/
  Platform.Core/             # Pure commerce, money and state invariants
  Platform.Persistence/      # Npgsql SQL, migrations, outbox and leases
  Platform.Security/         # Brand context, auth, ciphertext/keyring adapters
  Platform.Telegram/         # Transport, callbacks, localization, brand adapter
  Platform.Payments/         # Wallet, crypto, bank receipt state machine
  Platform.Fulfillment/      # Local inventory, supplier ports, delivery
  Platform.Campaigns/        # Broadcast audience/sending contracts
  Platform.Api/              # Admin and internal Minimal API endpoints only
  Platform.BotHost/          # Per-brand Telegram worker entrypoint
  Platform.JobHost/          # Durable jobs and reconciliation entrypoint
  Platform.Migration/        # Offline import/decrypt/re-encrypt/reconcile CLI
tests/
  Platform.UnitTests/
  Platform.IntegrationTests/
  Platform.ContractTests/
  Platform.AotSmoke/
ops/
  migrations/               # Reviewed forward-only deployment DDL
  compose/                  # Staging/prod secret mounts and host definitions
specs/001-multibrand-redesign/
  spec.md plan.md research.md data-model.md quickstart.md contracts/
```

Keep the solution modular but avoid one network service per library. Core/API/bot/jobs are deployable host boundaries; code projects may be combined later to simplify compilation while preserving architecture boundaries. There is **no frontend directory**.

## Design Decisions and Phases

### Phase 0 — Inventory and proof (offline; no traffic)

- Freeze current main/staging source candidates and identify **actual deployed** images, migrations, bot mappings, Telegram callback shapes, encryption prefixes and provider/worker inventories.
- Confirm which old database is master catalog source and whether legacy product/order data differs from the code inventory.
- Prepare synthetic masked fixtures for every legacy encryption representation, currency/precision case and duplicate ID scenario; do not obtain live keys or dumps without authorization.
- Publish and execute .NET 10 x64 Native AOT probes for Npgsql, JSON, Telegram transport, authenticated API, AES-GCM and legacy Fernet reader.
- Draft cost/RSS benchmarking and release acceptance thresholds based on live measurements later.

**Gate**: Offline source inventory and source-only AOT tests pass; actual schema/key compatibility remains a separate pre-staging gate.

### Phase 1 — New schema, crypto and identity

- Implement one-bot-per-brand `brands` with typed policy and encrypted token columns, `brand_channels`, brand-scoped `users(id PK, UNIQUE(brand_id,telegram_id))` as membership identity, wallets/ledger, catalog/overrides, shared stock, typed immutable order snapshots, payments, supplier requests, campaigns, durable `jobs` and `telegram_deliveries`, admin identities/audit and legacy ID mappings.
- Freeze schema only after validating the owner-approved 28-table **no-JSON** candidate. **USD-only `wallets(user_id PK)` and `wallet_entries(user_id FK)` omit a constant currency**. Each customer has a single revision-guarded private-DM `users.flow_*` context and `telegram_deliveries` derives destination chat ID from `users.telegram_id`. The owner-approved coupon/activity append-only relations are `discount_revisions` (one typed revision for creation/each owner edit, linked to `audit_events`) and `bot_activity_events` (significant bot commands/navigation/checkout/flow outcomes with per-membership replay dedupe, no raw Telegram content). Their event taxonomy, privacy/access controls, cleanup/recovery design and legacy activity mapping remain evidence gates; bot activity duration is approved as 180 days under FR-071. New orders have one product plus quantity, **only `user_id` (no repeated `brand_id`/`telegram_id`)**, immutable `unit_price_usdt`, nullable `unit_price_toman` and applied rate metadata, discount and rounding calculation version, `currency`, `payment_method` and nullable `payment_due_at`. **No stored order totals** (`total_usdt`/`total_toman`), `payable_usd` or pricing-mode enum. Exact requested/settled payment amounts remain in typed `payments.amount_due/amount_received` and immutable USD wallet ledger entries. Verify history before removing `order_items`. Use typed latest FX state on `brands`, with no rate-history table.
- Use DB-enforced brand-match guards for `(orders.user_id,product_id)` against `brand_products`, coupon and support relations. The lean single `user_id` approach needs verified database triggers where composite FK proof is impossible; customer-scoping in app code **alone is insufficient**.
- Define precedence `explicit override -> base product field`, distinguishing inherit, explicitly blank and overridden values.
- New encryptions use versioned per-brand keyrings for brand-owned records and a separately restricted **platform inventory keyring** for shared unsold credentials, all stored outside the database; encrypted token/credential values reside in the DB. No brand bot worker gains blanket shared-inventory decryption access. Legacy keys are only migration inputs and controlled archive/recovery dependencies.
- Offline migrator decrypts source rows in isolated memory, encrypts sensitive target fields with new keys, proves decrypt-and-compare, maps historical IDs, and reconciles balances/obligations. Migration is repeatable and fails closed on missing key or mismatch.

**Gate**: DDL, redacted fixtures, encryption verification and reconciliation report reviewed. Never delete legacy keys merely because migration code returned success.

### Phase 2 — Atomic commerce core

- Implement exact brand/currency quote and historical snapshots, wallet ledger and local-stock allocator in a single transaction. For an immediate local-only (non-preorder) multi-unit wallet checkout, all requested units must allocate or the transaction rejects without debit. Approved local preorders instead accept full payment with durable FIFO waiting until their complete quantity is available; see FR-065.
- Implement prepaid/product-entitlement, goodwill and marketing coupons on typed `discounts` with usage history in `discount_uses`. **Match existing automatic best-discount selection** and product/user eligibility. Disallow partial use of a required discount: 10 remaining 100%-off uses cap the input at 10; entering 11 prompts **quantity correction without creating an order**, in either currency, even if the wallet could pay for the last one. Apply approved FR-055: each verified paid prepaid_units source order issues one account-bound 100%-off coupon with N uses, without local goods allocation or supplier procurement. Historical issuance/ownership exceptions remain an E3 compatibility gate; campaign/goodwill batches retain their separate scope.
- Retain existing hourly Nobitex USDT→rial→toman conversion, 5,000-toman upward rounding, staleness policy and per-listing `auto_price_toman` switch. Persist current rate/fetch metadata in `brands` (refreshed together for Iran brands); freeze accepted rate, unit prices, discount, rounding version and payment method in `orders`, with **computed historical totals** and exact invoice/settlement proof in `payments`. A fixed-toman product can accept its **USD wallet** or USDT crypto when it also has an independent positive USDT selling price.
- For immediate local-only (non-preorder) checkout, lock candidate wallet/stock consistently, allocate the entire requested quantity and debit once, write order and delivery outbox atomically; losing buyers return an out-of-stock rejection without debit. Keep Telegram sends outside transactions.
- Bank receipts do not hold stock. For a local-only shortage, approval retains the paid/unfulfilled obligation waiting indefinitely for restock or explicit audited off-bot resolution. Supplier/mixed orders instead retain their bound procurement/review rules and FR-080 explicit admin decisions; local-preorder cancellation remains FR-070's outside-shop policy.
- Verified live crypto settlement for an immediate local-only product_purchase whose full local allocation fails creates exactly one **USD wallet credit for the frozen eligible USD quote in `payments.amount_due`**, releases every held coupon use and records compensation/notification intent atomically, with zero goods allocation or delivery. Replayed callbacks are inert; network fees/received-token differences do not reprice the quote. Dispatch supplier/mixed orders to bound procurement/review, local preorders to paid FIFO and prepaid_units source sales to their one entitlement; never apply generic shortage credit to these routes. Preserve historical USD wallet denomination and the approved internal numeric 1:1 quote rule.
- Every unpaid external bank/crypto order expires **30 minutes** after creation unless a timely receipt or qualifying crypto payment evidence puts it under review/confirmation. Display warning and deadline in localized payment instructions; schedule a durable, conditional expiry and Telegram **edit of the original payment message** to Failed/Expired. Never expire confirmed money or restock obligations. Apply approved FR-057 late-payment disposition: fully verified full-value late USDT credits the original membership's frozen eligible USD quote once, with no order revival; bank and anomalous/uncertain receipts are staff-reviewed. E2/E3 proof of evidence timing and quote satisfaction remains required.
- Document payment-state transitions and explicitly distinguish internal wallet credit from external crypto refund.

**Gate**: 50-client last-unit race, crash injection, duplicate callback, wrong-brand and ledger/reconciliation tests pass in actual disposable PostgreSQL.

### Order credential delivery — owner clarification 2026-10-31

Prefer editing the existing order message with a durably saved, validated bot/member/order/message ID. Authorize the complete manifest first; retry the same target and generation after rechecking current order state. Suppress known successful sends. Missing/uneditable anchors and file/multi-part delivery require explicit recovery handling, with no silent new-message fallback. An uncertain new credential-message send pauses automatic resending and remains observable for reconciliation or explicit authorized recovery; preserve the paid obligation and protected access to the same complete manifest. This does not promise exactly-once Telegram receipt or customer read confirmation. Qualify these cases under T034/T052/T077 before real Telegram activation; existing payment-expiry edits must recheck authoritative state so they cannot overwrite completed delivery.

### Phase 3 — Supplier, Telegram, human support, campaigns and admin APIs

- Supplier adapter receives durable **order-level batch** requests with positive requested quantity, stable idempotency reference and one-to-many per-unit `fulfillments` links; completed supplier submit/status/export operations persist in `supplier_request_attempts`. Timeout/unknown -> reconciliation; an incomplete result remains encrypted internally, releases zero items to the customer and leaves the full paid order under administrator review, never blind duplicate procurement, auto-credit or external refund. Confirmed supplier failure remains paid/unfulfilled until an explicit audited safe retry or same-brand USD credit under approved FR-080; bank-paid credits require an admin-entered/confirmed USD amount. Externally executed refunds and all their records remain entirely manual outside AccAbad; no refund table or API is included.
- Implement three bot identities with isolated updates, **DM-only customer flow state on `users`**, channels for forced-join eligibility, translations, branding and per-bot messaging policies; resolve outgoing private DM destination from `users.telegram_id` rather than a delivery `chat_id` column. No cross-brand content in customer payloads.
- Implement first-class human-support tickets, threaded replies, staff notifications and recovery paths independently of the optional AI process.
- Implement typed customer-interaction events from authenticated Telegram updates and genuine flow transitions, and a paginated brand/user-scoped administrative activity read API. Keep the event log out of the payment/stock transaction source of truth; no secret-bearing raw callback or message storage.
- Persist one `campaigns` row per brand/language for a multi-brand launch, with a shared `batch_id`. Materialize independently eligible memberships into the unified `telegram_deliveries` outgoing-recipient ledger, with typed origins, per-bot throttles and retry/reconciliation.
- Admin API supports brands/bot token update, channels, products/overrides, inventory, wallets/audited adjustments, orders, receipts, fulfillment, customers, campaigns, audits, and operational health. Define OpenAPI/route contract and object-level auth; no GUI.
- External AI service contract is optional/read-only for customer/shop context, with customer-scoped authorization and human handoff. AI downtime does not block commerce.

**Gate**: Bot, admin, provider, rate-limit, secret-redaction, permission and restart simulations pass as Native AOT executables.

### Approved source selection and local preorder review (2026-10-29)

- One product supports many supplier sources; administrator selects one shared active source per master product for all three brands, with no per-brand override. One account per supplier is owner-approved for v1 across products and brands; no account pools or per-mapping credential copies. Recommend one protected provider configuration/secret reference outside product mapping rows, preserving actual account identity on paid requests. Before implementation finalize configuration persistence, checked product-level selection and immutable request snapshots; no account table is justified solely by unneeded multi-account support. Binding is owner-approved at accepted payment (committed wallet debit, verified accepted crypto, or admin-approved bank payment). Disabled/unavailable source preserves accepted payment and the paid/unfulfilled obligation for admin review, with no runnable purchase or automatic fallback/credit. Commit local payment/binding/work atomically, then contact the supplier outside the transaction. No automatic provider fallback or multi-provider split purchasing; approved local-first remainder procurement is FR-078. `product_supplier_sources` is now owner-approved as table 27. Final schema constraints and protected provider configuration still require review; no executable DDL is approved.
- Local preorders use shared-product FIFO by accepted paid time/order-ID tie-breaker, with automatic restock draining, no newer/smaller-order bypass, no partial allocation and recoverable delivery of a full verified order manifest. All checkout/restock allocators must honor the same per-product queue/lock. Paid preorders do not expire with unpaid invoices or trigger immediate-order no-stock credits.
- Keep supplier shortfalls under administrator review; a supplier batch is not a local preorder. Preorder availability is separate from source type. Whole-order coupon redemption applies under FR-067. Exact approved admin hold/cancellation state representation under FR-079, complete-manifest guards, historical partial-order compatibility and provider evidence remain pre-DDL gates.
- Read [supplier-provider-matrix.md](contracts/supplier-provider-matrix.md) for staging source evidence and the proposed physical revision. These are design requirements and future tasks only; no implementation, migration or deployment is authorized.

### Phase 4 — Migration rehearsal and rollout (requires separate approval)

1. Snapshot and verify backup/restore for each source database and original keyring, with auditable protected access.
2. Import Brand A catalog only; start active stock empty, retaining old order snapshots and paid/unfulfilled obligations from all brands.
3. For **each** brand: close sales, block new checkout and inbound handlers, stop bot + web/admin writes + jobs + callbacks, reconcile pending provider events and deliveries, freeze snapshot, import/re-encrypt, compare balances/orders, test target under staging-only secrets, cut over single writer and verify.
4. Ensure products still sold by legacy brands do not share allocatable stock records with migrated brands; either no active cross-fleet overlap or explicit stock segregation during staggered rollout.
5. Retire original keys from active service only after all records and rollback material are verified; retain protected old key recovery copies as long as old encrypted snapshots remain.
6. Canary with the first production brand only after recorded operator signoff; later brands require independent signoff, same proven immutable binary and rehearsed rollback.

**Gate**: No unapproved production deployment; failed balance, ciphertext, stock, provider, isolation, or restore checks are hard stops.

## Contract / Decision Records

- [Research and alternatives](research.md)
- [Logical data model and state machines](data-model.md)
- [Backend API/worker contracts](contracts/backend-contracts.md)
- [Validation quickstart](quickstart.md)
- [Compact target table inventory for owner review](database-table-review.md)
- [28-table normalization and redundant-column audit](normalization-audit.md)

## Deferred / Out of Scope

- Any admin dashboard/UI, frontend components, template rendering, HTMX/Vue/Blazor, or public website.
- Recreating the full AI answering implementation within this platform.
- Automatic product mapping from legacy brands B/C.
- Cross-brand wallet pooling/transfers, auto-expiring manual paid orders, automatic refunds for uncertain supplier requests.
- Destructive removal of legacy database/backups or old key escrow until recovery dependencies are eliminated.

## Complexity Tracking

No accepted constitution exception. The planned three deployable host types address token ownership, transaction processing and durable jobs. Verify consolidation only after representative measurements; do not preemptively split into a remote microservice for each domain.

## Approved supplier product scope (2026-10-30)

The first release automates only ready-to-deliver accounts, keys and codes. No email activation, buyer-email slot assignment, existing-account upgrades, customer-input collection or manual/custom supplier delivery. This does not change separately approved local/manual product types or ordinary customer redemption of a delivered key.

Validate the exact product/variant at mapping publication and immediately before procurement using verified provider capabilities; unverified requirements are not eligible. Block known excluded items before payment. A post-payment eligibility change preserves the bound paid obligation for administrator review under FR-066, with no silent fallback/purchase/compensation. Provider processing/status/export can still be asynchronous; complete-order-only release remains mandatory.

FR-068 / SC-037 and existing future T057/T094 cover this policy. No extra physical table or implementation is added. The 28-table candidate remains subject to final constraints/provider/migration proof.

## Preorders have no configured availability date or fulfillment cap (owner correction 2026-10-30)

The owner clarified that administrators will not set preorder times. Remove the earlier nullable estimate/quoted-date fields and date editing/display-change/notification plans from the target design; no ETA, promised delivery date or maximum paid waiting duration is configured.

Paid local preorders remain in oldest-paid full-order FIFO indefinitely until complete stock or an explicit authorized admin resolution. Time alone cannot change payment, queue, coupon or financial state. Existing technical timestamps and the separate 30-minute unpaid external-payment window remain; that window never expires a confirmed-paid preorder. Support-based cancellation requests retain their non-mutation rule.

Preserve/reconcile real historical legacy date promises separately under FR-027, without copying legacy date configuration into the new target. FR-069 / SC-038 supersede the optional-date interpretation; no new relation or implementation.

## Paid-preorder cancellation requests use support (owner approval 2026-10-30)

Reuse existing support tickets/messages and their checked `order_id`; staff decide/respond manually. No dedicated cancellation button/endpoint/state machine/table is added. Ticket operations and message text have no authority to pause/reorder FIFO, cancel an order, release coupons or move money. A paid waiting order stays eligible under existing rules until a separate authenticated/audited admin order-state action.

Any actual resolution must serialize with allocation and retain payment/history. Do not treat support closure or an outside staff action as settlement evidence; no external refund details/API are introduced. For an approved paid local-preorder cancellation, the administrator handles repayment outside the shop. The cancellation creates no wallet credit or reversal of the original wallet debit, no payout execution and no external payout amount/reference/receipt/status record. Keep original accepted payments, debits and obligation history; audit the authorized order-state action with actor/reason, without asserting or proving repayment completion. Support activity and unknown outside actions remain non-authoritative. All still-held uses are released atomically and exactly once with approved cancellation under ADR-44, without changing coupon terms or expiry. Exact admin cancellation state/hold transitions remain under technical review. This does not alter independently approved late-crypto or immediate-sale shortage wallet compensation. FR-070 / SC-039; 28 tables unchanged. Existing future T025/T026/T046/T096 carry the review.

## Nonfinancial bot activity retention (owner approval 2026-10-30)

Operational `bot_activity_events` use a fixed rolling 180-day elapsed-time window from original trusted UTC `occurred_at`. Filter reads and remove expired rows using the same strict older-than cutoff; retain equality. Keep unexpired events append-only; allow the dedicated maintenance role's bounded, restart-safe expiry deletion. Reuse existing job/maintenance infrastructure and a suitable timestamp/key index; no retention-history or archive table is added.

Cleanup cannot cascade into financial/order/support/admin-audit or other domain history. Those records, source archives and backups have separate retention decisions. Do not silently keep expired raw events in a new archive. Restored/imported target activity must respect the cutoff before reads resume; preserve original event time and independent business idempotency. Cleanup schedule/lag monitoring, permissions and backup restoration details remain technical review items, not live authorization.

FR-071 / SC-040 extend future T087–T089. Table count remains 28, with no current data collection/deletion or implementation.

## Supplier bonus items — admin disposition (owner approval 2026-10-30)

If a supplier actually provides verified bonus accounts, keys or codes beyond the customer's ordered quantity, route those extras to authorized administrators through the existing admin facilities with protected content access. The customer receives the complete ordered quantity only; bonuses are excluded from every customer manifest, download, history and delivery path. Retain bonus content encrypted and attributable to its original supplier request/account/product and item identity; deduplicate purchase/export replays so the same item cannot become both a paid customer unit and an admin bonus. Do not increase order quantity, customer fulfillment rows, coupon usage or payment, automatically add bonuses to saleable shared inventory, or buy extra units to obtain a bonus. Do not expose credentials in logs, activity events, ordinary audit text or unprotected notices. Ambiguous extras or unprovable item identity require admin review rather than invented bonus attribution. This policy is conditional, not a claim that any current supplier is offering bonuses; incomplete customer quantities still follow the whole-order administrator-review rule.

Extend future supplier tasks T053/T054/T056/T058 with protected admin routing, typed bonus/customer attribution, recovery/deduplication and SC-041 checks after schema review and implementation authorization. No live supplier call, administrative delivery, application code or migration is authorized here.

## Fully coupon-covered supplier checkout — owner approved (2026-10-30)

When a valid automatically selected 100%-off coupon covers every requested unit of an eligible supplier product, checkout automatically accepts the zero-due order and schedules purchase from the selected supplier without a separate administrator approval. Revalidate membership/brand/product eligibility, whole-quantity coupon capacity and the selected eligible source in one serialized local checkout transaction; commit the full coupon claim as `held`, `no_payment_required` acceptance, immutable source/account/product/variant revision and durable supplier procurement work together. This committed acceptance is the source-binding point for zero-due supplier orders. No customer wallet balance is required, no wallet debit/credit or fabricated external payment/invoice is created, and no unpaid-payment timer applies. Call the supplier only after commit using the original request/intent and existing provider-specific uncertainty rules. Hold the whole coupon claim while procurement is pending/unknown/incomplete; redeem it exactly once with complete-order fulfillment, not merely on checkout or supplier submission. Invalid coupon/quantity/source before commit leaves no accepted order, hold or procurement work. A source/eligibility failure discovered after acceptance retains the accepted order, held entitlement and original binding in administrator review without fallback, automatic repurchase or compensation; zero-due acceptance is not an actual monetary payment. Existing full-order delivery, verified-admin-only bonus routing, owner coupon-edit rights and local/immediate-sale rules remain unchanged.

Use existing checkout, coupon, supplier-request and jobs mechanisms. Extend future T056/T058/T094 after technical review; the policy adds no separate admin approval stage or supplier purchase path. No implementation or live procurement is authorized.

## Supplier cost increase guard — owner approval (2026-10-30)

Automatic supplier procurement may absorb a cost increase of at most 10% inclusive against the immutable same-source cost at order acceptance; above that, retain the accepted order/claim for admin review. Customer pricing stays fixed, and 100%-off orders use the same guard. No rolling baseline, fallback purchase or automatic compensation.

Freeze supplier cost independently from the customer quote at acceptance, then check the 110% ceiling before remote procurement. Before automatic purchase, prove the provider's charged amount cannot exceed the allowed ceiling, using a documented binding quote, maximum-charge parameter or equivalent verified provider contract. A catalog read followed by an uncapped purchase is insufficient proof. Without that capability/evidence, route to admin review; do not claim that this policy proves current adapters enforce a cost cap. Provider calls stay outside the acceptance transaction. If a purchase may already have happened, reconcile the original attempt rather than retrying or treating a later price check as proof of no charge. Extend future T054/T056/T057/T058/T094 after technical review; the 28-table candidate and implementation authorization are unchanged.

## Supplier listings stay visible as out of stock — owner correction (2026-10-30)

An otherwise published supplier listing remains visible as Out of stock while its selected source cannot supply it; block new checkout without fallback, automatic hiding or effects on accepted orders.

Treat source availability as catalog purchase eligibility rather than an automatic publish/unpublish command. Extend existing future T021/T023/T032/T094; no new table, fallback source or supplier-preorder feature is introduced.

## Opt-in one-time restock alerts — owner approval (2026-10-30)

Restock alerts are voluntary, one-time and brand-scoped; existing jobs/delivery machinery handles notices, without reservation, automatic purchase or interference with paid FIFO. Explicit opt-in is separate from activity history and ordinary campaign consent.

Reuse the existing availability pipeline, brand-localized bot controls, jobs/outbox and throttled sender; add no campaign or monetary workflow. **`product_stock_subscriptions` is owner-approved table 28 (ADR-50)**; exact constraints/lifecycle remain pre-DDL review. Candidate typed fields: `id`, `user_id` FK to the brand membership, `product_id` FK, `created_at`, nullable `triggered_at`, nullable `cancelled_at`, nullable `closed_at`; exact timestamp checks remain review. A partial unique `(user_id,product_id)` where `closed_at IS NULL` enforces one active waiting/queued opt-in; repeated callbacks are idempotent, and a fresh opt-in after terminal completion gets a distinct subscription identity. `closed_at` closes consent after a terminal notification/cancellation outcome; delivery send status remains in its existing table. Validate the product's listing against the membership's brand without redundant Telegram identity. Use checked nullable `product_stock_subscription_id` origin FKs on existing `jobs`/`telegram_deliveries`, unique logical delivery per subscription and same-member/brand guards. Delivery state remains authoritative in `telegram_deliveries`, not duplicated as a second editable send-status field. Exact active/queued cancellation semantics, job integration and retention require final review. No JSON/EAV, arrays of subscriber IDs, raw message/credential storage or coupling to the 180-day activity purge. Both the feature and physical addition are approved; the approved inventory is 28, with final DDL/implementation still deferred. Extend existing future T015/T021/T022/T023/T032/T049/T051/T052; implementation remains deferred.

## Mandatory supplier refresh before new order creation — owner clarification (2026-10-30)

Direct refresh is mandatory before new order creation when supplier units are required (FR-078 permits full local-only coverage without supplier calls), separate from scheduled availability and later binding/procurement checks. Before creating each new order that actually needs supplier units, fetch current product/variant stock, supplier cost/currency and purchase eligibility directly from the selected supplier's API. Do not rely solely on a scheduled refresh, listing cache or old restock notification. Check that the response matches the current shared selected source/account/product/variant revision and can cover the required supplier remainder after eligible local coverage under FR-078; no remote purchase occurs during refresh. Parse/retain only validated typed evidence and update checked availability/cost observations without exposing provider secrets. Perform network reads outside database transactions; the final creation transaction must verify the same selection/revision/evidence or retry the read/reject creation if it changed. A failed, rate-limited, malformed, mismatched or unverifiable refresh blocks new order creation with retry guidance and no new invoice, wallet debit, coupon hold, zero-due acceptance or procurement work; do not invent zero stock or silently fall back to stale success/another supplier. Fresh data does not reserve supplier stock, guarantee later supply or independently overwrite brand retail prices. Unpaid order creation still does not bind the supplier: positive-due binding remains accepted payment, while 100%-off binding remains committed accepted checkout. Revalidate current selected source/cost/eligibility at binding and immediately before procurement under FR-066/068/073/074; refresh-before-create does not replace those guards. Existing order/quote/payment history is immutable; duplicate creation requests return their original result without reprice/rebind. Money accepted later despite a source/stock change is recorded and retained for administrator review with the paid obligation, never discarded or silently switched. Update future T031/T033/T057/T094 validation after technical review; no live supplier calls or implementation are authorized.

Example: an order for five units with three eligible local units requires verified supplier coverage for two units, not five. Recompute the split at acceptance under FR-078; full eligible local coverage requires no supplier call, and customer release still waits for the complete five-unit manifest.

## Remaining business clarifications approved (2026-10-30)

- Use eligible local stock first; mix local, selected-supplier and explicitly administered manual units while releasing one complete order. Full local coverage works without supplier access; refresh/revalidate only an actual supplier remainder (FR-078).
- Admin pause/resume preserves a paid local preorder's priority; a paused head blocks later local allocations until explicit resume/cancellation (FR-079).
- Confirmed supplier failure alerts staff and offers explicit reconciled retry or same-brand wallet credit. Bank credit requires admin-entered/confirmed USD plus reason; wallet/crypto credit uses original net USD. No automatic credit/retry or external payout record (FR-080).
- Verified active staff personally bypass channel checks and may disable enforcement per brand, preserving channel lists. Approximately 4–6 staff is an expected team size, not a hard cap; one customer-facing admin contact is configured per bot (FR-081/082).
- Private shared staff-chat notices use a separate trusted recipient kind, metadata and protected case/bonus access. Customers stay in membership DMs, and group text/membership grants no financial authority (FR-082).
- Use orders.id directly, without new public_ref; preserve genuine historical aliases through reviewed migration evidence (FR-083).
- Retain closed restock records 180 elapsed days from closed_at; retain cutoff equality and all active subscriptions. Coordinate only expired terminal restock evidence, with no financial/domain cascade or paid-preorder expiry (FR-084).

Review mixed-allocation snapshots, hold/concurrency state, typed resolution-ledger origin, staff/contact linkage, staff-recipient XOR and scoped cleanup before DDL. The approved count remains 28. No code, migration, live notification or money action is authorized.
