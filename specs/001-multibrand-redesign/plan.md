# Implementation Plan: Multi-Brand Commerce Backend

**Feature**: `001-multibrand-redesign` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)  
**Specification repository**: `Farrokhzadeh/telegram-multi-bot-shop`, branch `main`; imported from `Farrokhzadeh/AccAbad-Shop` at `7fcc4a7a35337405dd69195fc6c92474748c4513` (implementation and deployment remain unapproved)  
**Status**: Backend-only architecture plan, suitable for task breakdown after source evidence; no deployment authority.

## Summary

Build a lean, brand-neutral backend serving three independently presented Telegram shops. One shared commerce core controls brand-scoped users/wallets, a shared catalog and inventory, atomic wallet checkout, asynchronous external payment reconciliation, supplier fulfillment, campaigns, and brand-independent operational configuration. Each brand has its own bot token, encryption keyring and state. Administration is exposed via authenticated APIs **only**; no dashboard, frontend, UI framework or website is part of this plan. AI answering is a separately deployable service, outside the commerce codebase's execution path.

The historical `feature/001-csharp-native-aot` branch is a source of proven ideas/probes, not a greenfield codebase to merge wholesale. Old databases and code remain read-only reference inputs until an expressly approved migration rehearsal.

## Technical Context

| Concern | Decision / evidence gate |
|---|---|
| Language | C# / .NET 10 LTS, pinned via SDK config |
| Native publish | Linux x64 Native AOT for workers and backend API, with clean publish+run evidence for every dependency; Linux ARM64 separately qualified |
| API host | ASP.NET Core Minimal APIs, source-generated JSON, explicit request validation and authentication; no MVC, Blazor or server-rendered UI |
| Telegram | Per-brand long-polling consumer initially, or verified webhook mode if existing ingress requires it; a single update consumer per token |
| Persistence | PostgreSQL, Npgsql/direct explicit SQL, migration scripts applied deliberately; avoid experimental EF Native AOT dependency |
| Relational schema | **No JSON/JSONB/EAV columns in v1**; owner-approved 27-table candidate (23-table base + 3 history relations + `product_supplier_sources`); **USD-only wallets keyed by `user_id`**, customer private-DM `users.flow_*` and no redundant delivery `chat_id`; approved typed `discount_revisions` coupon-edit history and `bot_activity_events` customer interaction history; current FX fields on `brands`, accepted rates only on `orders`; pending proof of one-product legacy orders |
| Encryption | Migration-only legacy Fernet/key derivation reader; new authenticated ciphertext with **new per-brand keys for brand-owned data plus a distinct platform shared-inventory key**; AES-256-GCM candidate after golden-vector and Linux AOT tests |
| Jobs | PostgreSQL outbox, leases, idempotency keys and retry/reconcile states; no memory-only durable obligations |
| Administration | Private authenticated, audited admin API; all admins can manage all brands; **no graphical UI** |
| AI | External optional service integration via tightly scoped contract, no AI provider SDK or model implementation in commerce |
| Deployment | Separate bot consumers, core API and jobs as logical hosts; consolidate processes only after fault-isolation and RSS benchmarking |
| Testing | Pure rules, Postgres integration, provider/Telegram simulations, AOT executable smoke, migration reconciliation, staged golden flows |
| Performance | No invented RAM/startup thresholds; measure old fleet vs new under representative load before selecting deployment shape |
| Security | Zero cross-brand customer leakage, brand-scoped reads, per-brand keyring, secret files, audit and strict payment callback verification |

## Constitution Check

The source fleet constitution is imported unchanged in `.specify/memory/constitution.md`. Its legacy physical-fleet/schema assumptions need reconciliation with the owner's approved multi-brand design before implementation; transfer does not ratify new governance or undo those decisions. Continue reviewing the following existing design gates:

1. **Isolation**: Every customer or order operation has explicit brand context; data layer constraints and negative tests prove no cross-brand access.
2. **Money**: Immediate local wallet checkout debits only with complete atomic stock allocation. Approved local preorders instead commit full payment and durable FIFO obligations without partial allocation; stock assignment waits for the complete quantity; provider callbacks and authorized wallet compensation remain exactly once.
3. **Durability**: Payments, supplier outcomes, inventory allocation and deliveries survive process failure and never rely solely on in-memory queues.
4. **Crypto/key migration**: Legacy decrypt and new encrypt verified record-by-record; preserve old backups/key escrow until no recovery dependency remains.
5. **Controlled cutover**: No live instance, schema or key changes before snapshot, source inventory, restore drill, in-flight reconciliation and owner approval.
6. **Small VPS**: Native AOT is an optimization objective verified by published executable, CPU/RSS/startup measures, and functional checks—not a reason to weaken correctness.

**Assessment after repository transfer**: feature design constraints remain documented; legacy-constitution alignment, live-data evidence and operational release gates remain OPEN. No implementation or cutover approval is inferred.

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
- Freeze schema only after validating the owner-approved 27-table **no-JSON** candidate. **USD-only `wallets(user_id PK)` and `wallet_entries(user_id FK)` omit a constant currency**. Each customer has a single revision-guarded private-DM `users.flow_*` context and `telegram_deliveries` derives destination chat ID from `users.telegram_id`. The owner-approved coupon/activity append-only relations are `discount_revisions` (one typed revision for creation/each owner edit, linked to `audit_events`) and `bot_activity_events` (significant bot commands/navigation/checkout/flow outcomes with per-membership replay dedupe, no raw Telegram content). Their event taxonomy, privacy/access controls, cleanup/recovery design and legacy activity mapping remain evidence gates; bot activity duration is approved as 180 days under FR-071. New orders have one product plus quantity, **only `user_id` (no repeated `brand_id`/`telegram_id`)**, immutable `unit_price_usdt`, nullable `unit_price_toman` and applied rate metadata, discount and rounding calculation version, `currency`, `payment_method` and nullable `payment_due_at`. **No stored order totals** (`total_usdt`/`total_toman`), `payable_usd` or pricing-mode enum. Exact requested/settled payment amounts remain in typed `payments.amount_due/amount_received` and immutable USD wallet ledger entries. Verify history before removing `order_items`. Use typed latest FX state on `brands`, with no rate-history table.
- Use DB-enforced brand-match guards for `(orders.user_id,product_id)` against `brand_products`, coupon and support relations. The lean single `user_id` approach needs verified database triggers where composite FK proof is impossible; customer-scoping in app code **alone is insufficient**.
- Define precedence `explicit override -> base product field`, distinguishing inherit, explicitly blank and overridden values.
- New encryptions use versioned per-brand keyrings for brand-owned records and a separately restricted **platform inventory keyring** for shared unsold credentials, all stored outside the database; encrypted token/credential values reside in the DB. No brand bot worker gains blanket shared-inventory decryption access. Legacy keys are only migration inputs and controlled archive/recovery dependencies.
- Offline migrator decrypts source rows in isolated memory, encrypts sensitive target fields with new keys, proves decrypt-and-compare, maps historical IDs, and reconciles balances/obligations. Migration is repeatable and fails closed on missing key or mismatch.

**Gate**: DDL, redacted fixtures, encryption verification and reconciliation report reviewed. Never delete legacy keys merely because migration code returned success.

### Phase 2 — Atomic commerce core

- Implement exact brand/currency quote and historical snapshots, wallet ledger and local-stock allocator in a single transaction. For an immediate (non-preorder) multi-unit wallet checkout, all requested units must allocate or the transaction rejects without debit. Approved local preorders instead accept full payment with durable FIFO waiting until their complete quantity is available; see FR-065.
- Implement prepaid/product-entitlement, goodwill and marketing coupons on typed `discounts` with usage history in `discount_uses`. **Match existing automatic best-discount selection** and product/user eligibility. Disallow partial use of a required discount: 10 remaining 100%-off uses cap the input at 10; entering 11 prompts **quantity correction without creating an order**, in either currency, even if the wallet could pay for the last one. Distinguish one code with 100 uses from 100 unique codes until owner selects issuance policy.
- Retain existing hourly Nobitex USDT→rial→toman conversion, 5,000-toman upward rounding, staleness policy and per-listing `auto_price_toman` switch. Persist current rate/fetch metadata in `brands` (refreshed together for Iran brands); freeze accepted rate, unit prices, discount, rounding version and payment method in `orders`, with **computed historical totals** and exact invoice/settlement proof in `payments`. A fixed-toman product can accept its **USD wallet** or USDT crypto when it also has an independent positive USDT selling price.
- For immediate (non-preorder) checkout, lock candidate wallet/stock consistently, allocate one eligible item and debit once, write order and delivery outbox atomically; losing buyers return an out-of-stock rejection without debit. Keep Telegram sends outside transactions.
- Bank receipts do not hold stock. Admin approval checks stock and may leave a confirmed paid/unfulfilled order waiting indefinitely for restock or explicit audited off-bot settlement.
- Verified crypto payment for an immediate (non-preorder) order after stock exhaustion creates exactly one **USD wallet credit numerically equal to the frozen USDT quote in `payments.amount_due`**, under the explicit 1:1 internal accounting rule, and no credential allocation; repeated webhook/callback is inert. Preserve historical USD wallet denomination; do not rename wallets to USDT.
- Every unpaid external bank/crypto order expires **30 minutes** after creation unless a timely receipt or qualifying crypto payment evidence puts it under review/confirmation. Display warning and deadline in localized payment instructions; schedule a durable, conditional expiry and Telegram **edit of the original payment message** to Failed/Expired. Never expire confirmed money or restock obligations. Late-real-money handling remains an explicitly tested staff/compensation policy gate.
- Document payment-state transitions and explicitly distinguish internal wallet credit from external crypto refund.

**Gate**: 50-client last-unit race, crash injection, duplicate callback, wrong-brand and ledger/reconciliation tests pass in actual disposable PostgreSQL.

### Phase 3 — Supplier, Telegram, human support, campaigns and admin APIs

- Supplier adapter receives durable **order-level batch** requests with positive requested quantity, stable idempotency reference and one-to-many per-unit `fulfillments` links; completed supplier submit/status/export operations persist in `supplier_request_attempts`. Timeout/unknown -> reconciliation; an incomplete result remains encrypted internally, releases zero items to the customer and leaves the full paid order under administrator review, never blind duplicate procurement, auto-credit or external refund. Confirmed supplier failure remains paid/unfulfilled requiring an explicit audited order-state decision until any internal-wallet compensation policy is approved. Externally executed refunds and all their records remain entirely manual outside AccAbad; no refund table or API is included.
- Implement three bot identities with isolated updates, **DM-only customer flow state on `users`**, channels for forced-join eligibility, translations, branding and per-bot messaging policies; resolve outgoing private DM destination from `users.telegram_id` rather than a delivery `chat_id` column. No cross-brand content in customer payloads.
- Implement first-class human-support tickets, threaded replies, staff notifications and recovery paths independently of the optional AI process.
- Implement typed customer-interaction events from authenticated Telegram updates and genuine flow transitions, and a paginated brand/user-scoped administrative activity read API. Keep the event log out of the payment/stock transaction source of truth; no secret-bearing raw callback or message storage.
- Persist one `campaigns` row per brand/language for a multi-brand launch, with a shared `batch_id`. Materialize independently eligible memberships into the unified `telegram_deliveries` outgoing-recipient ledger, with typed origins, per-bot throttles and retry/reconciliation.
- Admin API supports brands/bot token update, channels, products/overrides, inventory, wallets/audited adjustments, orders, receipts, fulfillment, customers, campaigns, audits, and operational health. Define OpenAPI/route contract and object-level auth; no GUI.
- External AI service contract is optional/read-only for customer/shop context, with customer-scoped authorization and human handoff. AI downtime does not block commerce.

**Gate**: Bot, admin, provider, rate-limit, secret-redaction, permission and restart simulations pass as Native AOT executables.

### Approved source selection and local preorder review (2026-10-29)

- One product supports many supplier sources; administrator selects one shared active source per master product for all three brands, with no per-brand override. One account per supplier is owner-approved for v1 across products and brands; no account pools or per-mapping credential copies. Recommend one protected provider configuration/secret reference outside product mapping rows, preserving actual account identity on paid requests. Before implementation finalize configuration persistence, checked product-level selection and immutable request snapshots; no account table is justified solely by unneeded multi-account support. Binding is owner-approved at accepted payment (committed wallet debit, verified accepted crypto, or admin-approved bank payment). Disabled/unavailable source preserves accepted payment and the paid/unfulfilled obligation for admin review, with no runnable purchase or automatic fallback/credit. Commit local payment/binding/work atomically, then contact the supplier outside the transaction. No automatic provider fallback or split purchasing. `product_supplier_sources` is now owner-approved as table 27. Final schema constraints and protected provider configuration still require review; no executable DDL is approved.
- Local preorders use shared-product FIFO by accepted paid time/order-ID tie-breaker, with automatic restock draining, no newer/smaller-order bypass, no partial allocation and recoverable delivery of a full verified order manifest. All checkout/restock allocators must honor the same per-product queue/lock. Paid preorders do not expire with unpaid invoices or trigger immediate-order no-stock credits.
- Keep supplier shortfalls under administrator review; a supplier batch is not a local preorder. Preorder availability is separate from source type. Whole-order coupon redemption applies under FR-067. Explicit admin cancellation outcome/hold and optional-estimate notification policy, complete-manifest guards, historical partial-order compatibility and provider evidence remain pre-DDL gates.
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
- [27-table normalization and redundant-column audit](normalization-audit.md)

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

FR-068 / SC-037 and existing future T057/T094 cover this policy. No extra physical table or implementation is added. The 27-table candidate remains subject to final constraints/provider/migration proof.

## Optional local-preorder availability estimates (owner approval 2026-10-30)

A local preorder may be created/published/purchased without a date. Use a nullable typed date field on the shared product and nullable immutable quoted-date evidence where applicable; no mandatory-date check, fake default or date-history relation. Customer views distinguish an optional estimate from a guaranteed delivery date and show a localized no-estimate state when absent. Staff may update/clear an estimate without rewriting accepted order evidence.

Missing/passed/edited dates do not affect paid FIFO priority, complete-order allocation, coupon holds or financial/order state. Actual full stock and existing review rules control fulfillment; the 30-minute unpaid payment window is unchanged. Date-change notifications and explicit admin paid-cancellation outcomes/holds remain reviewed separately; the request channel is existing support under FR-070. FR-069 / SC-038, future T095/T096; 27-table count unchanged. Specification only.

## Paid-preorder cancellation requests use support (owner approval 2026-10-30)

Reuse existing support tickets/messages and their checked `order_id`; staff decide/respond manually. No dedicated cancellation button/endpoint/state machine/table is added. Ticket operations and message text have no authority to pause/reorder FIFO, cancel an order, release coupons or move money. A paid waiting order stays eligible under existing rules until a separate authenticated/audited admin order-state action.

Any actual resolution must serialize with allocation and retain payment/history. Do not treat support closure or an outside staff action as settlement evidence; no external refund details/API are introduced. Final admin cancellation outcome/hold and paid coupon/internal-wallet disposition remain reviewed before implementation. FR-070 / SC-039 reuse future T025/T026/T046/T096; 27 tables unchanged.

## Nonfinancial bot activity retention (owner approval 2026-10-30)

Operational `bot_activity_events` use a fixed rolling 180-day elapsed-time window from original trusted UTC `occurred_at`. Filter reads and remove expired rows using the same strict older-than cutoff; retain equality. Keep unexpired events append-only; allow the dedicated maintenance role's bounded, restart-safe expiry deletion. Reuse existing job/maintenance infrastructure and a suitable timestamp/key index; no retention-history or archive table is added.

Cleanup cannot cascade into financial/order/support/admin-audit or other domain history. Those records, source archives and backups have separate retention decisions. Do not silently keep expired raw events in a new archive. Restored/imported target activity must respect the cutoff before reads resume; preserve original event time and independent business idempotency. Cleanup schedule/lag monitoring, permissions and backup restoration details remain technical review items, not live authorization.

FR-071 / SC-040 extend future T087–T089. Table count remains 27, with no current data collection/deletion or implementation.
