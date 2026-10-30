# Architecture Research and Decisions — Backend Only

**Date**: 2026-10-26 | **Status**: Design decisions; production evidence gates open.

## ADR-01 — Native AOT hosts, no UI

**Decision**: .NET 10 Native AOT for Telegram workers, background worker and Minimal API backend; libraries use source-generated JSON. No MVC, Razor, Blazor, frontend or web session-dependent design.

**Rationale**: Existing `feature/001-csharp-native-aot` source-only spike proved Npgsql/Fernet/Telegram JSON/AOT feasibility under synthetic conditions. Microsoft's current compatibility matrix supports Minimal APIs with limitations, while MVC and Session are not Native AOT features. See [Microsoft Native AOT guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0).

**Alternatives**: Django parity port rejected because it preserves now-unwanted AI and three separate instance assumptions; all-in-one native process deferred pending restart/failure tests; ASP.NET MVC excluded for this backend-only phase.

## ADR-02 — Direct SQL and PostgreSQL

**Decision**: Npgsql with parameterized direct SQL and explicit row mapping. Business transaction boundaries live in application services plus DB constraints; schema migrations run as separately reviewed operations.

**Rationale**: Clear locking, no runtime reflection, no startup DDL. Microsoft currently labels EF Core Native AOT query precompilation experimental and not suitable for production. See [EF Native AOT documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries).

**Alternatives**: EF Core only if support status and real exact-version proof materially change; no need to make that a gate for v1.

## ADR-03 — Customer identity and brand scopes

**Decision (updated 2026-10-28)**: `users.id` is a surrogate **brand membership** identifier with unique `(brand_id, telegram_id)` and no separate cross-brand global customer entity. The same Telegram person may have unrelated USD wallets and histories across brands. All customer-facing reads are scoped by brand, including media, conversation state and notifications.

**Rationale**: The brands must appear independently operated to customers. A shared internal admin search by Telegram ID is permitted.

**Alternative**: Global profile/brand-memberships rejected on owner's explicit preference.

## ADR-04 — Brand product inheritance

**Decision**: Brand A legacy products seed the new master; brand listing fields independently inherit, explicitly override, or explicitly clear the base. Base edits update only inheriting fields. New active stock starts empty after operator approval.

**Risk**: B/C historical order snapshots and active obligations must be preserved even though their catalogs are not imported as active listings.

## ADR-05 — Local stock and payment

**Decision**: No held stock while checkout/receipt waits. At wallet confirmation, a short Postgres transaction locks wallet and candidate stock, checks invariants, debits and allocates once, and writes outbox. For external payments, money and inventory cannot be one atomic cross-provider transaction: compensate based on durable states after confirmed settlement.

**Alternatives**: In-memory mutex rejected (not process/restart safe); separate remote atomic-service hop rejected as a required dependency; long checkout reservations explicitly rejected by owner.

## ADR-06 — Supplier API uncertainty

**Decision**: Durable external request ID/idempotency key and three-valued outcome (confirmed success, confirmed failure, unknown). Unknown must be queried/reconciled before duplicate request or compensation. Provider-specific policy validated from its real API documentation.

## ADR-07 — Encryption migration

**Revised decision**: Keep old per-brand keyrings and legacy Fernet reader only for migration/rollback. Generate **new versioned per-brand keyrings** for brand-owned secrets (bot tokens, support/personal data), plus a **separate platform-inventory keyring** for credentials in the shared, not-yet-sold inventory pool. All keys are protected outside DB in restricted secret files. Use authenticated ciphertext, e.g. `enc:v2:<key-id>:<nonce+ciphertext+tag>` with authenticated key scope, table, row and field association; precise envelope fixed with golden vectors before coding. For an unsold `inventory_items` record, bind AAD to the platform-inventory scope and item ID, **not** an arbitrary brand ID. Ordinary ledger/order amounts stay typed and queryable under access controls. Never log plaintext.

**Rationale**: New key format can eliminate long-term old-key dependence after verified migration. .NET provides `AesGcm` on Linux; see [Microsoft cross-platform cryptography](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography). Old backups require their original keys until retired.

**Alternative**: Retain Fernet as new format if AES-GCM envelope/testing or security review does not pass; never force conversion without reversible validation.

## ADR-08 — Keys and encrypted bot tokens

**Decision**: Store encrypted bot tokens in database, but never store decrypting keys there. Runtime keys supplied through per-brand read-only Docker secret mounts and a separately restricted shared-inventory secret mount. Only privileged inventory/delivery components may decrypt unsold credentials; a bot token alone cannot decrypt stock. Central admin decryptor gets role-limited audited access. DB `key_id` metadata enables rotation. An operator must protect key backups separately from database backups.

## ADR-09 — Durable jobs and bot ownership

**Decision**: Outbox entries committed with local state changes and leased via Postgres; actual Telegram/provider calls execute after commit. Exactly one active update consumer per bot and one authoritative payment callback handler per brand; status checks reconcile remote side effects before retries.

## ADR-10 — Administrative API only

**Decision**: Private authenticated Minimal API with secure API/session token strategy (exact auth primitive to be threat-modeled), audit trails, CSRF protection if browser-cookie authentication is later introduced, idempotency on mutations and media access checks. No UI components or browser-side implementation.

## ADR-11 — Human support independent from AI

**Decision**: Customer/staff support ticket state, replies, staff notifications and conversation audit are first-class P1 backend functions. Optional AI answering has no ownership of the ticket ledger or human handoff path; a stopped AI service cannot prevent staff action.

## ADR-12 — Campaign delivery and compaction boundaries

**Revised decision (2026-10-27)**: Campaign definition and a materialized recipient/send intent remain distinct. Combine former `campaign_recipients` and `message_deliveries` into a single typed `telegram_deliveries` table: both are independently deduplicated, recoverable outgoing Telegram effects, distinguished by checked origin FKs and unique indexes. `jobs` remains separate because a worker lease is not evidence of a remote send. Cross-brand launches create one `campaigns` row per selected brand/language, with shared `batch_id`. Telegram cannot promise exactly-once external sends following a crash after remote success. The revised 23-table no-JSON candidate and its open assumptions are in [database-table-review.md](database-table-review.md).

## ADR-14 — Typed relational schema, no JSON columns

**Decision (2026-10-27)**: No `json`/`jsonb`, EAV configuration table or untyped job payload in the initial PostgreSQL schema. Brand policy, payment rules, quotes, order item display history, provider verification, job origins, audiences and audit metadata use explicit columns, constrained FKs and reviewed migrations. Provider-native evidence can be stored separately as an immutable protected asset with a digest, but typed verified facts must drive money and fulfillment decisions.

**Tradeoff**: A real product/discount feature with arbitrary many-to-many relations may need a dedicated relation and raise the table count. Do not reduce tables by flattening the ledger, fulfillment, support history or supplier idempotency. **Owner confirmed** private-DM-only customer flows, so revision-guarded `users.flow_*` fields replace a separate Telegram session table in v1. **Owner confirmed** USD-only customer wallets, so `wallets` is keyed solely by membership `user_id` and both wallet/ledger rows omit repeated currency columns; source non-USD balances still require explicit migration reconciliation if encountered.

## ADR-15 — Single-product orders, prepaid codes and per-listing FX (owner clarification 2026-10-27)

**Decision**: New orders contain one product type and `quantity >= 1`; all brand-facing product/price/rate snapshots live on `orders`, with `fulfillments(order_id,unit_no)` tracking each allocated or still-owed unit. Omit `order_items` only if the historical legacy source confirms that no required multi-product order history is lost.

**Decision**: Preserve `discounts` + `discount_uses` for (a) prepaid bulk units using **one code with many remaining uses** (format confirmed in ADR-25), (b) auditable goodwill/replacement vouchers and (c) 10/50/100% marketing codes. Issuance has optional batch/source-order provenance, capped uses and product/brand/user restrictions. The bot automatically chooses the eligible discount; there is no customer selection/opt-out. Do not confuse voucher compensation with actual cash refund or FR-019 crypto **USD-wallet** credit. A prepaid coupon is a future obligation **without advance stock allocation**.

**Superseded decision**: The owner explicitly rejects an `exchange_rates` history table. Instead, keep latest accepted source rate/observation time and refresh health as typed fields on `brands`, with order-specific rates on `orders`. The separate `pricing_mode` enum is removed; the `auto_price_toman` boolean and explicit USDT/toman product prices mirror current Django behavior.

**Unresolved high-risk details**: prepaid source payment and liability closure at redemption; provider-specific crypto evidence timing; and historical one-product order proof. Temporary external-payment coupon claims and one awaiting-payment order per membership were resolved in ADR-21; **crypto-paid no-stock coupon return in ADR-22; restored-coupon expiration in ADR-23; prepaid account binding in ADR-24; single multi-use prepaid code in ADR-25; strict 10/50/100% quantity validation, verified late-money compensation and owner-managed coupon edits in ADR-26–28**. Fixed-toman listings explicitly may accept independently priced USDT payment.

## ADR-16 — Verified legacy quote/discount algorithm and deliberate stricter use cap

**Observed in `Farrokhzadeh/AccAbad-Shop` `main`, 2026-10-27**:

- `apps/store/models.py` has `Product.price`, `Product.price_irr`, `Product.auto_price`, and one `Discount` with `pct_off`, `max_uses`, `current_uses`, optional `product` and `assigned_user`.
- `apps/common/services/pricing.py` reads the Nobitex `USDT/RLS` latest market price, divides by 10 to obtain toman, rounds **up to a 5,000-toman step**, refreshes hourly, and rejects stale automatic pricing; disabling `auto_price` keeps a manually configured toman value.
- `apps/common/services/discounts.py` computes USD and toman totals, automatically selects the best eligible **single code**, and for automatic toman products recalculates and rounds the discounted total using the raw rate. Critically, it currently uses `applied_qty = min(quantity,remaining_uses)`, silently allowing partial coverage.
- `apps/bot/database.py::automatic_quote` chooses that best discount automatically. `apps/common/services/checkout.py::purchase_item` recalculates under lock and consumes the selected uses at commit.

**New owner mandate**: Preserve automatic best-discount selection, dual-currency calculation, the raw-rate conversion/rounding sequence, forced application, and the per-product auto/manual price switch. Deliberately replace partial-use behavior with **quantity-entry validation**: **10 remaining 100% uses cap the selectable/requested quantity to 10**, in either currency, and requesting 11 prompts correction with **no order created**, not a "failed order". Enforce this again inside the final stock/wallet/coupon transaction, not just at menu preview.

**Quote schema (revised owner decision)**: `orders` uses `user_id`, `unit_price_usdt`, nullable `unit_price_toman`, `exchange_rate_toman`, source/fetch metadata, `currency`, `payment_method`, `quantity`, immutable discount/rounding inputs and `price_calculation_version`, plus nullable `payment_due_at`; **no `total_usdt`, `total_toman`, `payable_usd`, `pricing_mode`, `brand_id` or `telegram_id` in orders**. Compute display totals reproducibly with the historic frozen calculation version. For **actual money movements**, preserve `payments.amount_due`, `payments.amount_received`, payment currency and signed wallet ledger amounts. **Existing wallets stay in USD**, while external crypto quote amounts may be in USDT; shortage credits map numerically 1:1 into USD accounting units as an explicit internal rule, never an implicit token-exchange-rate calculation.

## ADR-17 — Membership FK, 30-minute unpaid invoice expiry and editable Telegram payment message

**Decision (owner 2026-10-27)**: Make `users.id` a surrogate PK uniquely identifying one `(brand_id,telegram_id)` membership; use `orders.user_id` without storing `orders.brand_id`/`orders.telegram_id`. Resolve brand/bot identity by joining the user table. This saves repeated columns but requires a **database brand-consistency trigger** to verify the order's product listing and any coupon against `users.brand_id`; the app alone is not enough. Payments derive membership via `order_id`. Brand-aware campaign recipients retain checked `(brand_id,user_id)` references where useful for indexing/FKs.

**Decision**: Unpaid external bank/crypto orders expire at `created_at + 30 minutes`; customer-facing payment instructions warn about 30 minutes and show the deadline. A durable job conditionally changes `awaiting_payment` to `expired` and queues a Telegram **edit** to the original payment message (Failed/Expired). An on-time receipt or on-time qualified crypto evidence moves the order into `under_review`/`confirmation_pending` and protects it from payment-window expiry even if review/confirmations finish much later. No wallet/free order needs this timer. Money actually arriving late is always recorded and reconciled and never silently discarded or automatically used to resurrect an expired order.

**Open**: Definitive provider-specific evidence time for crypto (observed versus confirmed) and late settled payment disposition. Temporary coupon-use claims and automatic replacement cancellation are resolved in ADR-21; crypto-paid out-of-stock coupon return in ADR-22; **no automatic coupon-expiration extension** in ADR-23. **USD-only customer wallets and private-DM-only customer interactions are resolved**; legacy migration must stop and reconcile if an old source reveals genuine non-USD wallet liabilities.

## ADR-18 — Fixed order payment method; standalone wallet top-ups still exist

**Owner decision (2026-10-28), amended by ADR-21**: An order's `payment_method` and `currency` are immutable. Switching from bank/card to crypto or USD wallet creates a **new order**. The subsequent approved rule is that a successfully created new order **cancels** any earlier same-membership `awaiting_payment` order immediately instead of waiting 30 minutes; an old order with timely receipt or crypto evidence is protected from this cancellation. Money received for the old order is still reconciled under its original payment reference, not moved or assumed to fund the new order.

**Verified from `Farrokhzadeh/AccAbad-Shop` `main` (2026-10-28)**: `apps/payments/models.py::PaymentAttempt` distinguishes `wallet_topup` from `direct_checkout` and permits its `order` FK to be NULL. `apps/payments/services.py::create_payment_attempt` creates USD **wallet funding** attempts without a product checkout. This is a real modeling exception, not theoretical.

**Original gap, now resolved below**: The planned `payments(order_id NOT NULL)` proposal was incomplete because legacy wallet funding may have no product order. Removing `payments.kind/currency` was unsafe until a typed wallet-funding origin and provider-denomination audit existed. Do not model top-ups as fake product orders; the now-approved exclusive origin fits the 23-table candidate.

**Owner clarification (2026-10-28), resolving the origin design**: Wallet top-ups remain, and **crypto via NOWPayments is their only customer funding route**. Orders may be paid with TOMAN card-to-card, NOWPayments crypto using a USD-valued quote, or an already funded USD store wallet, where brand/product policy allows. Customer-facing wallet values are labeled USD; separate payment facts preserve the actual network asset and quantity. A wallet holds **internal store credit**, not a segregated on-chain crypto balance. Add `payments.topup_user_id` FK → `users.id`, make `payments.order_id` nullable, and enforce `CHECK (num_nonnulls(order_id,topup_user_id)=1)`. A funding request can exist before a `wallets` row. On verified settlement, create/credit USD wallet and ledger exactly once. No new table. Keep `payments.kind/currency` provisionally for legacy evidence; revisit after confirming origin-derived method, quotes and migration fixtures. **Open**: verify exact gateway IPN signature/status and funding-invoice expiry separately from 30-minute **order** expiry. NOWPayments documentation distinguishes USD `price_currency` from crypto `pay_currency` (which can vary).

**Owner crypto-network clarification (2026-10-28)**: Only USDT on TRON/TRC20 and USDT on BNB Smart Chain/BEP20 are accepted for both direct order payments and wallet top-ups. NOWPayments officially lists network-specific provider codes `USDTTRC20` and `USDTBSC` (https://nowpayments.io/supported-coins). Use a two-value allowlist, verify the exact provider `pay_currency` on invoice creation and settlement, and recheck live provider availability before displaying either option. `TRX` is the separate native TRON coin and **is not accepted**. Do not add a networks table or a generic crypto asset registry for these two fixed choices. Preserve any other historical legacy asset code only as migration evidence, not as a new-checkout option.

## ADR-19 — One shared NOWPayments merchant account, three isolated brand ledgers

**Owner decision (2026-10-28)**: All three Telegram brands use a **single NOWPayments merchant account**. Configure one platform-scoped provider credential/IPN secret set through restricted runtime secret mounts (not per-brand encrypted `brands` columns and not a new provider-account table). All three brands share this provider's availability/outage blast radius, key rotation and settlement reconciliation; bot tokens and individual brand encryption keys remain separate. Link each external payment through globally unique internal request/reference IDs to one `payments.id`; `payments.order_id` or `payments.topup_user_id` gives the authoritative membership/brand. A signed provider callback is evidence of a payment, **not permission to select the brand from invoice description or untrusted request data**. Apply idempotency on provider payment identity across the shared account. Generate brand-specific invoice descriptions with no cross-brand names.

**Risk and release gate**: A shared provider account does **not guarantee independent public merchant identities**. Before customer rollout, inspect sanitized live/sandbox provider-hosted invoice pages, transaction receipts, payee/merchant labels, payout/network presentation and support references for possible shared ownership disclosure. If the gateway exposes a common identity, seek owner acceptance or adjust only the permitted display flow; do not imply that software-level tenant scoping can hide provider-controlled branding. Do not repurpose the per-brand encryption keys to encrypt the shared provider's platform-wide secret.

## ADR-20 — Distinct Iranian receiving bank cards with frozen payment destinations

**Owner decision (2026-10-28)**: The two Iranian brands use **different card-to-card receiving cards**, independent of the shared NOWPayments processor. **Exactly one card per brand is active, and the newest successfully saved card immediately replaces its predecessor for new checkout instructions.** Store typed encrypted card number and recipient holder name, key ID, update time and keyed duplicate-detection fingerprint on `brands`. Apply atomic, revision-checked admin updates; `updated_at` is metadata, not an algorithm for choosing among historical rows. The international brand may omit these values. No `bank_cards` table or active-card history list is needed.

**Historical safety**: When a customer creates a TOMAN bank order, create immutable, brand-encrypted `payments` snapshot columns from the selected bank destination. **Re-encrypt each snapshot with the payment row/field's own authenticated associated data; blindly copying `brands` ciphertext is invalid under row-bound authenticated encryption.** Changing `brands` receiving card never rewrites a pending order's payment instructions or the destination against which its receipt must be verified. An old pending order retains its original 30-minute deadline and timely payment evidence; card replacement alone does not cancel or re-price it. A database brand-scope check and **platform-keyed**, normalized card fingerprint (stable across brand keys) prevent assigning one card to both active Iranian brands. Do not store customers' source card PAN/CVV or transfer screenshots in plaintext log fields. **Public-separation limitation**: banking apps may still show the same account-holder name even when receiving card numbers differ; confirm this in a controlled test before rollout.

## ADR-21 — Temporary coupon claims and one replaceable unpaid order per membership

**Owner decision (2026-10-28)**: Choose **temporary coupon use holds (option A)** for externally paid bank/crypto orders; do not reserve stock. At successful order creation, atomically claim all required discount uses in `discount_uses(status='held')` against the locked coupon and its cap. `discounts.redeemed_count` counts only completed redemptions; currently held quantities also reduce allocatable use capacity. If the order expires unpaid or is replaced, mark the claim `released` in the same transactional status change; keep its historical row for auditing. With on-time submitted receipt or eligible crypto evidence, keep the claim through `under_review`/`confirmation_pending`, even beyond 30 minutes. Accepted paid bank orders awaiting restock also keep it until final fulfillment or explicit admin resolution. Successfully fulfilled orders transition `held → redeemed` and commit the redeemed counter exactly once. Immediate 100%-free and USD-wallet purchases redeem atomically with stock and debit (if any).

**Owner decision**: At most **one `awaiting_payment` order per `users.id` brand membership**, enforced by a partial unique database index. A successfully created new order **immediately cancels** an earlier truly awaiting-payment order for that same membership; the old method, amount and provider reference are not rewritten, its coupon claim is released, and the old Telegram message is edited to **Cancelled — replaced**. Validation failures leave the previous order intact. New order creation and cancellation/hold changes must share one transaction with a membership lock and duplicate-request suppression. A signed receipt under review, qualifying on-time crypto evidence awaiting confirmation, or any paid/restock obligation **must not** be automatically cancelled or timed out, regardless of new purchases; those states are excluded from the partial index. Real bank/crypto funds received on an already cancelled invoice remain an auditable reconciliation obligation, not funding for the replacement.

**Risk**: One waiting order does not stop a bad actor from repeatedly submitting apparent receipts to move many orders into long-running `under_review` and hold campaign uses. Apply anti-abuse limits and staff verification; never discard a potentially real payment to enforce a cosmetic one-order constraint. **Account binding is approved in ADR-24 and one-code multi-use issuance in ADR-25.**

## ADR-22 — Return coupon uses on verified crypto paid, no stock

**Owner decision (2026-10-28): Option A**. For an immediate (non-preorder) order, if NOWPayments verifies payment but all-or-nothing inventory allocation fails due to unavailable local stock, issue **exactly one USD wallet credit numerically equal to that order's frozen discounted amount due**, allocate/deliver **zero units**, and **release rather than redeem every coupon use held by that undelivered order**. Lock payment, coupon, wallet and stock in the approved deterministic order; commit wallet ledger credit, compensated/unfulfilled order state, `discount_uses(status='released')` and a durable customer notification intent in the same database transaction. A callback replay cannot issue another credit or release another coupon. The customer can reuse the returned discount use if the coupon remains independently eligible; no phantom fulfillment, code consumption or on-chain-refund claim. The **existing expiration timestamp** is never extended by this transition, as confirmed in ADR-23.

**Risk**: A time-limited marketing code may have expired by the time the crypto payment settles and stock is found missing. Releasing its usage count is still correct accounting but may not make it usable. **Resolved in ADR-23: do not extend validity or issue automatic replacement goodwill**; do not silently mutate the shared campaign `ends_at` or other customers' terms.

## ADR-23 — Never extend expired marketing coupons on restored usage

**Owner decision (2026-10-28): Option A**. A released `discount_uses` hold restores remaining numeric capacity but **does not modify** the coupon's original `ends_at`, toggle `active`, or auto-create a replacement. If the time-limited promotion expired, its restored uses are historical/available-capacity facts but cannot be selected for a new order. Notify the buyer that the original campaign ended; do not misleadingly promise a usable coupon. Hold created while the coupon was valid remains enforceable on the **original protected order** after expiry if the bank receipt or verified crypto evidence was timely: fulfillment redeems that already-committed entitlement, not a new coupon use.

**Prepaid paid-value exception**: 100% future-unit entitlements are obligations from an earlier paid source; v1 issuance defaults to `discounts.ends_at IS NULL` and cannot silently expire or be forfeited as a promotion. Explicit expiry terms for prepaid product credits, if ever introduced, require a separately reviewed product contract, customer-facing warning and auditable discharge/settlement of any outstanding paid obligation. **Single multi-use issuance approved in ADR-25.** Staff may issue goodwill separately through its existing audited workflow, but refund/no-stock compensation must never generate goodwill automatically.

## ADR-24 — Account-bound prepaid coupons, not bearer vouchers

**Owner decision (2026-10-28): Option A**. A prepaid future-product code may only be redeemed by its original paying customer **within the same brand**. Persist that identity as `discounts.eligible_user_id` FK to `users.id`, which identifies a brand membership, not a shared cross-brand profile. For **new prepaid issuance only**, require non-null `eligible_user_id`, `product_id` and paid `source_order_id`; the source order's `user_id`, brand and product entitlement MUST match. Typed database checks/controlled issuance must prevent cross-brand source and membership mismatches. Check the logged-in member during code reveal, selection, temporary holding and final fulfillment: text possession never transfers ownership. Public promotion/goodwill scopes remain independently defined; `eligible_user_id` must remain nullable in the shared `discounts` table for genuinely public campaigns.

**Migration risk**: Some historical codes may not have a recorded eligible member or may already have been shared. Never silently confiscate customer-paid obligations or choose a new owner by guesswork. Report these as unresolved pre-cutover migrations and require a documented reconciliation/transition policy. **Single 100-use bound code is now the approved new-issuance format** (ADR-25); retain legitimate old individual codes in historical evidence, and add no owner-transfer table.

## ADR-25 — One account-bound prepaid code with N unit uses

**Owner decision (2026-10-28): Option A**. When a customer prepays for `N` future units of one product, issue **one** brand- and customer-bound 100%-off code with `discounts.max_total_uses=N`, `max_uses_per_user=N`, `redeemed_count=0`, linked to the paid source order and exact product. The same code supports multiple separate later orders for varying unit quantities. Each `discount_uses.units_covered` records how many units were actually redeemed on that order. Remaining claimable units = `max_total_uses - redeemed_count - currently held units`. For immediate redemption, require a coupon-row lock and all-or-nothing fulfillment; whole-order coupon redemption also applies to local preorders under the later FR-067 correction; a request above remaining capacity is rejected at quantity input, never partially fulfilled. Prepayment does **not reserve inventory**. The code remains auditable after exhaustion, and cannot be transferred. This compact issuance pattern does not prohibit independently scoped campaign/goodwill batches or force-lossy migration of legacy individually issued codes.

## ADR-26 — Strict full-quantity coupon caps for every percentage

**Owner choice in clarification form (2026-10-28)**: Reject the requested quantity, not the order, when the best automatic 10%, 50% or 100% discount lacks enough remaining permitted unit uses. Tell the customer the maximum acceptable quantity. Do not mix full-priced and discounted units in one order, and do not skip a mandatory selected coupon. Recheck under the serialized coupon lock across currently held and redeemed uses; this changes the original source's ability to apply a coupon to fewer units than requested and requires parity/intentional-delta tests.

## ADR-27 — Auto-credit fully verified late USDT, staff-review exceptions

**Owner choice in clarification form (2026-10-28)**: A fully verified NOWPayments payment arriving against an expired or replacement-cancelled product invoice becomes a single **USD wallet credit for the original brand's membership** based on the original frozen eligible USD quote. The old order remains expired/cancelled and unfulfilled; no stock allocation, coupon redemption, invoice rebinding or payment to a replacement order. Record payment/ledger linkage and notify the customer. Idempotent under repeated callbacks. Underpayment, overpayment, wrong network, provider disputes or uncertain received amount stay for staff reconciliation; late TOMAN bank transfers also remain staff-reviewed. Verify provider-specific evidence and boundary timing before accepting implementation.

## ADR-28 — Off-bot bank resolution is administrator-controlled; coupon unit caps editable

**Owner correction (2026-10-28), subsequently expanded**: An off-platform bank refund or delivery is **not observable to the bot**. It must not infer coupon redemption/release; only actual authenticated staff updates change held/restock states. The business owner can edit coupon **total/per-user limits, percent-off, effective used count, validity dates, activation and permitted eligibility** even when previously redeemed/held units exceed new limits or prepaid paid units remain outstanding. **No cap-floor, settlement-proof or owner-approval prerequisite** may block that owner override. A reduction can overcommit a code; show `available=max(0,limit - effective_used - held)` to prevent new claims while existing holds/historical orders remain unchanged. Preserve actual redemption history separately from manual logical usage: `discounts.redeemed_count` continues to count real redemptions while a typed signed `usage_adjustment_units` supports owner-controlled effective counts (`max(0,redeemed_count + usage_adjustment_units)`). Use row locking, authentication and an `audit_events` record of before/after, reason and actor; this is accountability, not a business veto. Owner edits can reduce customer-visible prepaid entitlements without automatic refund, but the paid original-source financial history must not be falsified or deleted. New quotes follow edited coupon terms; accepted orders keep frozen discounts. **Superseded audit shape (2026-10-29):** the owner approved a dedicated typed `discount_revisions` table for this edit history; the underlying owner override permissions are unchanged.

## ADR-29 — Dedicated typed coupon revisions (owner approval 2026-10-29)

**Decision**: Add `discount_revisions` as a distinct immutable one-to-many coupon *settings* history. Each coupon is created with revision 1 and each authenticated owner edit advances `discounts.revision`, appends its entire typed post-edit state and links to an `audit_events` actor/time/reason row in the same locked transaction. Preserve true `redeemed_count` and capture the edit-time redeemed/held context; ordinary coupon use is tracked in `discount_uses` and does not generate an owner-edit revision. Never rewrite original order pricing. A narrow 24th table is preferable to filling every generic audit event with sparse coupon old/new columns or inventing JSON/EAV changes.

## ADR-30 — Customer bot interaction ledger (owner approval 2026-10-29)

**Decision**: Add `bot_activity_events` as a separate, append-only, brand-member `users.id` keyed event stream for **all meaningful** commands, menu/navigation actions, product views, button actions, quantity/checkout stages, support entry, genuine abandonment transitions and significant errors. Typed optional refs to product/order/ticket must match the membership's brand; recorded Telegram update/flow numbers are evidence, not trusted tenant selectors. Use deterministic idempotency keys for repeated Telegram updates or system events; never store raw DM text, callback payload, credentials, receipts, customer coupon codes or arbitrary JSON metadata. Keep `users.flow_*` as current state, `telegram_deliveries` as outgoing transport evidence and domain money/order tables as source of truth. Confirm event taxonomy, pagination/cleanup indexes, privacy controls, the approved 180-day retention implementation (ADR-41) and source `user_activity` migration before DDL. **At this ADR's approval, the design candidate had 25 physical tables** (original 23 plus these two); ADR-31 subsequently approved a 26th supplier-attempt table. External refunds stay outside AccAbad by later owner decision.

## ADR-31 — Supplier request attempts and partial-batch review (owner approval 2026-10-29)

**Decision**: Extend the approved 25-table candidate with one separate **`supplier_request_attempts`** relation (physical table **26**). Persist a typed, redacted, uniquely sequenced attempt **before** a possible submit/status-lookup/export API call; finalize its typed outcome or retain `unknown` after a crash because local code cannot prove whether the remote call occurred; `jobs.attempts` is merely a local worker scheduling counter. Completed remote result evidence must be immutable, with no provider-secret or JSON payload. Link each `supplier_requests` to one original `orders.id` and `requested_quantity`, rather than to a single fulfillment; link the many individually verified, brand-key-encrypted supplier outputs back through same-order `fulfillments.supplier_request_id` and stable remote-unit dedupe.

**Historical default, superseded by ADR-35:** this earlier pass permitted three delivered and two pending. The latest owner correction disallows customer partial delivery: retain three verified items encrypted internally, release zero, and keep the whole paid order under administrator review. Table 26, attempt history, source reconciliation and no blind re-purchase remain approved. Coupon claims remain whole; no selective unit redemption is required for new target orders.

## ADR-13 — Supplier failure defaults and migration accounting

**Decision**: A supplier's confirmed failure after money is received creates `paid_unfulfilled_requires_admin_resolution` by default; do not silently choose wallet credit versus external refund without the provider/payment contract. Historical import uses a verified frozen opening balance plus non-posting history (or full ledger replay from a verified opening entry), never both as posting movements.

## Evidence still needed

- Verify actual deployed Django commit/images/schema and source role/key mapping; old branch documents synthetic proof but not live compatibility.
- Confirm supplier request idempotency, webhook signatures, paid-callback lifecycle and cryptographic token format.
- Confirm exact USDT quote rounding vs received token quantity for USD-wallet compensation tests. Preserve legacy USD wallet denomination unchanged.
- Measure actual VPS CPU/RSS/startup and AOT publish costs before choosing process consolidation or promised service levels.
- Confirm protected restore drill and old ciphertext/key coverage before any live cutover.


## Owner decision — no operational external-refund recording (2026-10-29)

The owner rejected the proposed unified `refunds` table. Administrators execute **and record** bank/crypto, supplier and preorder refunds wholly outside AccAbad; the backend will have no refund API, settlement-proof field, outgoing-payment subtype, or external-refund reporting. The bot cannot observe or infer such transfers. A staff change to an `orders` state, when deliberately made, is audited solely as a state transition, not as proof a refund occurred. Keep the already approved **internal USD-wallet credits** for verified crypto/no-stock and late settlement in `wallet_entries`; these are not outgoing refunds.

The old Django supplier/preorder refund rows are historical evidence; preserve verified read-only source exports/backups and decide archival access/retention and the resulting complete-history exception before migration. No operational refund table is to be created for importing them. At the time of this refund-exclusion decision, the baseline was **25 candidate tables**; ADR-31 subsequently approved `supplier_request_attempts` as table **26**.

## ADR-32 — Multiple supplier mappings with administrator selection (owner approval 2026-10-29)

**Decision:** One shop product supports several supplier mappings; an administrator selects the active source. No automatic selection, failover or split procurement. Retain distinct account/provider, external product and optional variant identities; RoboticVN's composite staging code is adapter encoding, not a normalized target key. Freeze request source identity before remote purchase and retain it through configuration changes/retries.

**Owner-approved relation (superseding the initial recommendation):** `product_supplier_sources` is table 27 under ADR-37; final constraint/config design remains reviewed. Owner subsequently confirmed one shared master-product active source for all three brands, with no per-brand override. The owner subsequently confirmed one configured account per provider across products/brands; see ADR-36. Finalize its protected configuration persistence and one checked product-level selection without adding multi-account machinery. Source-binding time is subsequently approved at accepted payment in ADR-34; unpaid orders are not bound. The latest approved baseline is 27, including the source-mapping relation. Local intent keys and transmitted idempotency headers do not prove provider deduplication or credential replay.

**Evidence:** [supplier-provider-matrix.md](contracts/supplier-provider-matrix.md) pins five staging adapters and the preorder handoff. Staging has real adapter code; ADR-31's earlier main-only/mock-only observation does not describe staging. This review made no live supplier call or account verification.

## ADR-33 — Automatic paid local-preorder FIFO (owner approval 2026-10-29)

**Decision, updated by ADR-35:** local restocking serves oldest paid orders first only when each order's complete quantity is available; no partial allocation/reservation or smaller-order bypass. The shared-product queue spans brands while customer data stays isolated. Use durable accepted payment time with order-ID tie-breaker; unpaid quotes receive no paid-queue priority. Serialize all stock allocators for that product and commit inventory/drain work durably.

**Consequences:** A paid local preorder may wait for stock; immediate-sale stock rejection and FR-019 crypto shortage credit do not apply to its intentional backlog. Paid remainders do not expire with unpaid invoices. Supplier 3-of-5 remains administrator-reviewed. Local preorder unit obligations are separate from an advance purchase issuing an N-use prepaid coupon.

**Open:** remaining admin cancellation state/hold transition design (request channel resolved as support in ADR-40), complete-manifest access/transport guards, historical partial-order compatibility and exact physical source/blocker fields. Source binding at accepted payment is resolved in ADR-34. Reuse existing orders/units/jobs/transport where sufficient; a dedicated batch relation needs evidence. Staging external-refund recording stays excluded. Review only.

## ADR-34 — Bind supplier at accepted payment; retain unavailable-source paid obligations (owner approval 2026-10-29)

The owner chose accepted payment as the source-binding point and explicitly approved retaining payment plus administrator review if the selected source is disabled or cannot supply. This supersedes the earlier order-creation recommendation.

For live supplier product orders, binding occurs at committed wallet debit, verified accepted crypto or approved bank payment; unpaid checkout and receipt submission do not bind. Freeze the shared product's current source/account/product/variant revision with paid state and durable procurement work locally, then call the provider outside the transaction. Keep customer quote terms unchanged. Replay/reconciliation retains the original binding despite later global source switches.

If no usable selected source exists, preserve accepted payment and paid/unfulfilled status with typed blocker/known-source evidence and no runnable procurement. Do not silently fall back, automatically credit/refund, invent a source or erase a paid obligation. Later source repair does not itself clear administrator review. Source-binding concurrency, blocker field placement and zero-price supplier settlement remain implementation-design gates. Existing expired/late crypto wallet recovery does not become supplier procurement. FR-066 / SC-035; specification only.

## ADR-35 — Complete-order delivery replaces partial customer fulfillment (owner correction 2026-10-29)

The owner clarified: a customer requesting five items must receive the complete five-item order, not three now and two later. This supersedes earlier partial-delivery examples in ADR-31/33 and the proposed per-unit coupon-choice question.

- Local FIFO keeps paid priority but waits for the head order's entire quantity; no partial allocation, reservation or newer/smaller-order bypass. Allocate all units atomically and prepare one full order manifest.
- A supplier may still return an incomplete response. Persist acquired content encrypted internally with per-unit identity/evidence, release zero through customer-facing paths and retain the whole paid order for administrator review. Do not automatically purchase again, change suppliers or compensate.
- Keep the entire coupon claim held until complete allocation/fulfillment redeems it once. Whole-order expiry/previously approved compensation rules remain; later ADR-44 approves full still-held-claim release with actual local-preorder cancellation.
- One logical full-order delivery may require several Telegram messages/files. Prepare the full manifest first and resume interrupted transport without re-purchasing/redeeming; remote receipt cannot be made atomic by PostgreSQL.
- Keep per-unit evidence and supplier attempts. A new split-delivery batch relation is not justified by new-order behavior; preserve genuine legacy partial/batch history through reviewed migration compatibility, not deletion or duplicate redelivery.
- Updated FR-063/065 and added FR-067 / SC-036; the baseline at this earlier decision was 26 tables, subsequently extended to 27 under ADR-37. Specification only.

## ADR-36 — One configured account per supplier (owner approval 2026-10-29)

The owner confirmed one account per supplier is enough. Apply one platform-wide configured account per provider, reused by every mapping and all three brands; no same-provider account pools, per-brand accounts or per-mapping credential copies in v1.

Recommend resolving each approved `product_supplier_sources.provider_code` through a typed protected provider configuration/secret reference. A separately selectable `supplier_connection_ref` on every mapping is unnecessary under this cardinality and is removed from the current mapping proposal. Exact configuration persistence remains a technical review item; this decision neither creates a supplier-account table nor proves no configuration relation will ever be needed.

Paid supplier requests keep the actual original account identity with provider/product/variant evidence. Same-account API-key rotation must not alter that identity. Account replacement must never redirect an old purchase/reconciliation to a different account; unresolved identity or unavailable original access requires admin review, without automatic purchasing/compensation. No secrets are copied into snapshots/logs.

FR-064 and SC-033 plus existing future T093/T094 validation carry the clarification. Counts stay 67 requirements, 36 success criteria, 96 unchecked tasks; 27 approved baseline tables after the subsequent ADR-37 source-mapping approval. Specification only.

## ADR-37 — Product supplier sources approved as table 27 (owner approval 2026-10-29)

The owner explicitly approved `product_supplier_sources` as physical design table **27**. This is the fourth addition to the 23-table base, after `discount_revisions` (24), `bot_activity_events` (25) and `supplier_request_attempts` (26).

The typed relation links a master product to provider/external-product/optional-variant identities, with enabled/revision/timestamp fields and null-aware uniqueness. The provider resolves its one configured account; credentials and an independent account selector do not belong on each mapping. One active selection is shared across all three brands. Accepted payment snapshots the source ID/revision and actual provider/account/product/variant; later edits must not redirect old requests.

This resolves the pending table-addition decision, not the final DDL. Review same-product active-selection guards, referenced mapping lifecycle, source revisions/snapshot provenance, provider eligibility and protected configuration persistence before implementation. No supplier-account pool, refund or split-delivery relation is approved. Complete-order fulfillment and administrator review of blocked/incomplete supplier orders stay unchanged.

The approved count is now **27**; 67 functional requirements, 36 success criteria and 96 unchecked future tasks remain. Documentation only.

## ADR-38 — Ready-to-deliver supplier items only (owner approval 2026-10-30)

The owner selected ready-to-deliver accounts, keys and codes for first-release automated supplier products. Do not collect customer email, existing-account credentials, OTPs or other buyer fulfillment inputs. Exclude input-collecting activation/slot/upgrade workflows and manual/custom supplier delivery. Normal redemption/use of an already delivered code is allowed; this decision does not remove separately approved local/manual products.

This aligns with the inspected staging adapters' restricted subsets, but adapter code alone is not current provider proof. Verify exact product/variant requirements before mapping publication and before purchase. Unverified/excluded items cannot enter new payable API checkout. A post-payment eligibility change keeps accepted payment and original source in administrator review under FR-066; do not purchase from another source or compensate automatically. Provider processing/status/export may be asynchronous, and full-order manifest gating remains under FR-067.

No customer-input, activation or upgrade table is added; the approved candidate stays 27. Added FR-068 / SC-037, updated existing future T057/T094 and validation coverage. Current totals: 68 functional requirements, 37 success criteria, 96 tasks; T001 repository setup complete, 95 unchecked. Specification only.

## ADR-39 — Historical optional-date interpretation, superseded by ADR-42

The earlier “allow preorders without a date” answer was interpreted as allowing optional admin estimates and nullable product/order date fields. The owner subsequently clarified that admins will not define preorder times at all. ADR-42 and revised FR-069 / SC-038 replace that interpretation. No date fields or date-change workflows were implemented; prior documentation remains in Git history.

## ADR-40 — Paid-preorder cancellation requests through support (owner approval 2026-10-30)

The owner selected existing support tickets for customers requesting cancellation of a paid local preorder waiting for stock. Reuse existing checked order-linked support conversation and manual staff response. No dedicated cancellation button, endpoint, table or automatic request-state workflow is authorized.

Opening/closing/replying to a ticket does not pause/reorder FIFO, cancel an order, release coupons, move money or prove an outside payout. A separate authenticated/audited admin order-state action is required for actual resolution, serialized against allocation and preserving paid history. At this channel-only decision, payment disposition remained open; ADR-43 subsequently fixes outside-shop repayment without cancellation wallet credit. ADR-44 subsequently approves whole-claim release with cancellation; exact cancellation state/hold transitions remain under technical review. Existing exclusion of external refund execution/recordkeeping remains.

FR-070 / SC-039 and future T025/T026/T046/T096 carry the review. Current counts: 27 tables, 70 requirements, 39 success criteria, 96 tasks; T001 setup complete, 95 unchecked. Specification only.

## ADR-41 — Nonfinancial bot activity retained 180 days (owner approval 2026-10-30)

The owner selected 180 days for nonfinancial `bot_activity_events`. Interpret this as a rolling 180 elapsed days from original trusted UTC event time, not six calendar months. Reads and cleanup share `occurred_at < now_utc - 180 days` as the expired predicate; retain equality. Unexpired records remain append-only. Only privileged bounded/resumable retention maintenance deletes expired rows; no live deletion or collection is authorized now.

Money, orders, support, coupon revisions, admin audit and other domain evidence keep separate policies and cannot be affected by activity deletion. Do not silently archive raw events forever or retimestamp/recreate an expired replay. Business idempotency remains independent. Protected source archives/backups are not purged by this job; target restore/import must apply the cutoff before activity exposure. Technical permissions, scheduling/backlog monitoring, indexes and backup recovery remain reviewed.

FR-071 / SC-040 and existing future T087–T089 cover the choice. No new table: 27 approved candidate tables, 71 requirements, 40 success criteria, 96 tasks; T001 setup complete, 95 unchecked. Specification only.

## ADR-42 — No preorder date settings or paid fulfillment time cap (owner correction 2026-10-30)

The owner clarified: preorders have no time cap and admins will not define preorder times. Remove the earlier optional availability-date/ETA/quoted-date snapshot design and date-change notification question; do not offer admin/customer date configuration or paid-preorder expiry jobs.

Confirmed-paid local preorders wait indefinitely for the complete quantity or an explicit authorized staff resolution. Elapsed time does not cancel/reorder them, release coupon holds or move money. Keep technical created/paid timestamps for audit/oldest-paid FIFO and the existing 30-minute unpaid external invoice window; it is not a paid fulfillment deadline. Existing support request non-mutation and complete-order-only release remain.

Historical source date promises stay protected and need migration reconciliation; the target does not inherit active legacy date settings. Revised existing FR-069 / SC-038 and future T095/T096, without renumbering or adding a table. Current counts stay 27 tables, 71 requirements, 40 success criteria, 96 tasks; T001 setup complete and 95 unchecked. Documentation only.

## ADR-43 — Admin handles approved preorder cancellation repayment outside the shop (owner approval 2026-10-30)

For an approved paid local-preorder cancellation, the administrator handles repayment outside the shop. The cancellation creates no wallet credit or reversal of the original wallet debit, no payout execution and no external payout amount/reference/receipt/status record. Keep original accepted payments, debits and obligation history; audit the authorized order-state action with actor/reason, without asserting or proving repayment completion. Support activity and unknown outside actions remain non-authoritative. All still-held uses are released atomically and exactly once with approved cancellation under ADR-44, without changing coupon terms or expiry. Exact admin cancellation state/hold transitions remain under technical review. This does not alter independently approved late-crypto or immediate-sale shortage wallet compensation. FR-070 / SC-039; 27 tables unchanged.

This resolved payment disposition after approval; ADR-44 subsequently approves full coupon release with actual cancellation. Automatic cancellation and a queue hold on support contact remain excluded. Revised existing FR-070 / SC-039 and future T046/T096; no new table or financial/refund workflow. Counts remain 27 approved candidate tables, 71 requirements, 40 success criteria and 96 tasks; only T001 setup is complete. Specification only; no money moved, code implemented or migrations applied.

## ADR-44 — Return all held coupon uses with approved preorder cancellation (owner approval 2026-10-30)

Approving cancellation of a paid local preorder MUST atomically release **all** of that order's still-held coupon uses (`held → released`) exactly once with the authorized cancellation and actor/reason audit. No separate coupon action or proof of outside repayment is required. Preserve the claim/history and original coupon expiry, activation, eligibility, limits, actual redeemed count and usage-adjustment values; do not create a replacement coupon, renew an expired campaign or increase its cap. Returned capacity is usable only under the coupon's current ordinary eligibility and limits. Serialize cancellation with complete-order allocation/fulfillment: only one outcome commits; a stale/replayed cancellation cannot release already redeemed uses or permit post-cancellation allocation. Support activity alone still releases nothing. Repayment remains outside the shop with no cancellation wallet credit or payout records.

This resolves the coupon disposition after an actual authorized cancellation. Reuse existing `discount_uses`, order/audit state and durable work; no new table or split-unit accounting. Exact state representation and concurrency design remain technical review. Revised existing FR-070 / SC-039 and future T046/T096. Counts stay 27 tables, 71 requirements, 40 success criteria and 96 tasks; only T001 setup is complete. Specification only; no implementation, migrations, coupon mutation or repayment performed.

## ADR-45 — Actual verified supplier bonus items go to admins (owner approval 2026-10-30)

The owner chose: send any bonus items to the admin if they actually occur. If a supplier actually provides verified bonus accounts, keys or codes beyond the customer's ordered quantity, route those extras to authorized administrators through the existing admin facilities with protected content access. The customer receives the complete ordered quantity only; bonuses are excluded from every customer manifest, download, history and delivery path. Retain bonus content encrypted and attributable to its original supplier request/account/product and item identity; deduplicate purchase/export replays so the same item cannot become both a paid customer unit and an admin bonus. Do not increase order quantity, customer fulfillment rows, coupon usage or payment, automatically add bonuses to saleable shared inventory, or buy extra units to obtain a bonus. Do not expose credentials in logs, activity events, ordinary audit text or unprotected notices. Ambiguous extras or unprovable item identity require admin review rather than invented bonus attribution. This policy is conditional, not a claim that any current supplier is offering bonuses; incomplete customer quantities still follow the whole-order administrator-review rule.

The pinned Canboso source anticipates bonus quantity via `finalQuantity`; no actual current offer or live bonus purchase was established. The policy does not enable extra procurement or change any customer quote. Exact typed item disposition, protected admin retrieval/notice integration and recovery remain technical review; 27 candidate tables unchanged. Added FR-072 / SC-041 and extended existing future T053/T054/T056/T058. Current totals: 72 requirements, 41 success criteria, 96 tasks; only T001 setup complete. Specification only; no live items accessed or sent.
