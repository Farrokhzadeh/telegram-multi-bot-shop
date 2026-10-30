# Supplier capability matrix and local-preorder reconciliation

**Reviewed:** 2026-10-29. **Scope:** specification/source review only.
**Evidence:** staging commit `5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d`; no supplier calls, credential access, deployed-image inspection, database queries or live purchase validation in this review.

## Owner-approved behavior

1. A shop product may have several supplier mappings; an administrator selects the active source. No automatic supplier selection, fallback or split procurement.
2. Local stock allocates automatically oldest paid order first only when the complete requested quantity exists; no partial assignment/reservation or smaller-order bypass.
3. Supplier 3-of-5 retains three encrypted internal acquired items but releases zero customer content; the whole paid order requires administrator review. Release only a complete verified set after review clearance.
4. All external refunds and their recordkeeping remain outside AccAbad.

FR-064/065 and SC-033/034 capture the new behavior. The approved physical baseline now contains **27 tables**, including owner-approved `product_supplier_sources` below. Final constraints/configuration remain reviewed before DDL.

## First-release eligible product scope — owner approved (2026-10-30)

Automated supplier procurement supports ready-to-deliver accounts, keys and codes only, with no customer-provided fulfillment inputs. Exclude email activation, buyer-email slot assignment, existing-account upgrades and manual/custom supplier delivery. Normal customer redemption of an already delivered code remains allowed. Ready-to-deliver is a product/input boundary, not a synchronous API-success guarantee; processing/status/export and one-time encrypted content recovery remain necessary.

Validate exact external product/variant requirements at mapping enablement/publication and before purchase. Unknown/unverified requirements fail eligibility. Block known excluded items before payable checkout; a requirement change after accepted payment preserves the original source and full paid obligation for administrator review under FR-066, with no fallback purchase/automatic compensation. Full-order-only release remains FR-067. No new customer-input/activation table, form or generic payload is approved; local/manual scope is unchanged. FR-068 / SC-037.

## Source capability matrix

These statements describe the inspected adapters and their embedded evidence notes, not independently verified current supplier contracts. Source comments about prior live probes are historical claims from that code, not probes performed in this review. Headers, local retry flags and local intent keys alone do not prove remote safety.

| Provider | Product identity / eligibility | Purchase identity and uncertainty | Delivery/recovery | Design consequence |
|---|---|---|---|---|
| [Akunding](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/apps/common/services/suppliers/akunding.py) | External numeric product ID, batched quantity | Sends `X-Idempotency-Key`; network/5xx marked retryable in source | Lookup by remote order; delivered state requires exactly requested item count | Freeze account/product/key. Independently validate provider key scope/lifetime, retries and result recovery. The adapter does not demonstrate partial delivery support. |
| [ProdSeller](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/apps/common/services/suppliers/prodseller.py) | External object ID; only instant delivery, no email activation/custom delivery | Sends `Idempotency-Key`; network/5xx marked retryable | Purchase/lookup parse delivered keys; exact quantity required | Retain eligibility checks and immutable source. Verify remote idempotency before relying on adapter retry flags. |
| [RoboticVN](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/apps/common/services/suppliers/roboticvn.py) | Product plus variant; staging encodes `product:variant` in one string | No documented purchase idempotency; ambiguous POST must not automatically repeat | Separate credential endpoint; completed status alone is insufficient; source notes order history/status values were unobserved | Separate product/variant columns. Persist intent, reconcile original purchase and require verified credential identity before delivery. |
| [Canboso](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/apps/common/services/suppliers/canboso.py) | Plain account/no purchase requirements only; slot/upgrade products excluded | Sends `Idempotency-Key`; key existence does not prove replay returns credentials | Credentials only in purchase response, not order lookup. Source expects requested quantity plus provider bonus when applicable; short results do not finalize delivery | Protect original credential-bearing response immediately. Remote dedupe and credential replay are separate capabilities. Define bonus policy; extra supplier items must not silently become extra customer entitlements. |
| [RapidX](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/apps/common/services/suppliers/rapidx.py) | External product ID; manual delivery excluded; OTP-number API is separate | No documented purchase idempotency; ambiguous POST must not automatically repeat | Completion needs full credentials; source notes order/identity shapes unobserved and catalog currency unspecified | Confirm currency/account and wire formats before enablement. Unknown side effects require reconciliation, never fallback purchase. |

The adapters generally require complete quantities before returning delivered. This agrees with the owner's latest complete-order delivery correction. Incomplete provider output must still be preserved encrypted internally as acquisition evidence, never exposed as a partial customer order; adapter parsing alone does not prove recovery or complete-manifest access guards.

Any retry after 429, 5xx, malformed success, timeout or disconnect needs provider-specific proof of rejection or deduplicated replay. Never infer that an HTTP status or generic `retryable` flag proves money was not spent.

## Owner-approved supplier-source relation — table 27 (final constraints pending)

The owner approved `product_supplier_sources` as table 27, with candidate fields:

| Field | Meaning |
|---|---|
| `id`, `product_id` | Stable mapping identity and shared shop product |
| `provider_code` | Resolves the provider's one configured account across products/brands; no separate account selector or credential copy on mapping rows |
| `external_product_id`, nullable `external_variant_id` | Provider-native identities as separate typed values |
| `enabled`, `revision`, timestamps | Availability, optimistic concurrency and lifecycle |

Use a uniqueness rule for provider/shop-product/external-product/variant mappings that treats absent variants consistently. One active-source pointer or filtered unique selection is authoritative; whichever is chosen must enforce same product and enabled source, with transactional actor/reason audit. Do not maintain both a pointer and an independently editable active flag.

**Selection scope — owner approved (2026-10-29):** one shared active source per master product, affecting all three brands together. There is no per-brand supplier-source override. If using a pointer, place it on `products`, not `brand_products`; if using a filtered selection, enforce one per `product_id`. Keep the existing same-product, enabled-source and revision/audit guards. Previously bound requests retain their original source identity after a switch; newly bound requests from every brand use the new shared selection. Binding is now owner-approved at accepted payment as described below.

**Account cardinality — owner approved (2026-10-29):** one configured account per supplier is enough, shared across its product mappings and all three brands. No account pools, per-brand accounts or separately selectable account per mapping. Recommend a typed protected provider configuration/secret reference resolved by `provider_code`; the approved mapping does not include a separately selectable `supplier_connection_ref`. Exact config persistence remains under review; this does not approve another account table.

Paid requests still retain the actual original account identity. Same-account key rotation must not change that identity or resend a purchase. Replacing the configured account cannot silently redirect reconciliation of old requests; unavailable original-account access or identity mismatch needs administrator review. Never store tokens in mapping rows, request snapshots, URLs, logs or evidence summaries.

**Binding time — owner approved (2026-10-29):** freeze the current shared source at accepted payment: committed wallet debit, verified crypto accepted for the live order, or administrator-approved bank payment. Unpaid order creation, invoice opening and unapproved receipts do not bind. In one local transaction, serialize source selection against admin edits and commit accepted payment, immutable account/provider/product/variant identity and durable procurement work. No supplier network call occurs inside that transaction. Customer quote terms stay fixed. Replay or a later admin switch never redirects an accepted request.

**Unavailable selected source — owner-approved default:** if the source is disabled, missing or known unable to supply at acceptance, retain accepted payment and a visible paid/unfulfilled administrator-review obligation. Retain the selected identity where known and a typed blocker; do not fabricate usable mapping or runnable procurement when absent. Re-enabling/replacing the shared source does not automatically clear that review gate. A source becoming unavailable after binding also retains its original request identity and paid liability. No silent fallback, repeat purchase, wallet credit or external refund. Known checkout invalidity may be rejected before payment is accepted; the rule never authorizes discarding money already accepted. Existing expired/replaced late-crypto recovery remains wallet recovery, without supplier purchasing. Exact typed blocker/snapshot storage and zero-price supplier entitlements remain pre-DDL review items.

The approved candidate now contains **27** tables, including this mapping relation. Protected config persistence and verified transport relationships remain technical review items; unneeded multi-account support and rejected partial-delivery behavior do not justify additional tables.

## Local preorder reconciliation

The [staging preorder handoff](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d/specs/pre-order/handoff.md) describes partial allocation, separate delivery batches, dates and staff cancellation/refund workflows. **Intentional target change:** the owner now requires complete-order customer delivery, retaining local paid FIFO but rejecting staging three-now/two-later allocation and delivery. Preserve genuine historical partial/batch evidence through a reviewed migration compatibility plan; do not copy refund recording or erase previously delivered units.

| Concern | Target requirement / review status |
|---|---|
| Availability versus source | Local preorder is a separate availability policy, not another supplier or prepaid coupon issuance. Do not enable supplier-backed preorder implicitly. |
| Paid queue eligibility | Full accepted payment; durable `payment_confirmed_at`, order-ID tie-breaker. Unpaid quotes/unaccepted receipts do not receive paid priority. Pending provider confirmation is not accepted payment. |
| Shared-stock fairness | One product queue across brands; oldest eligible paid whole order first. All allocators serialize against the queue; if its full quantity is unavailable, smaller/newer orders cannot bypass. Customer views expose only their own details/position. |
| Complete-order allocation | If all requested units exist, assign them atomically. Otherwise assign/reserve none and wait; no intentional partial customer release. Supplier-acquired subsets are protected internal evidence, not allocatable shared inventory. |
| Payment and timeout | Wallet-funded preorders pay in full with durable backlog; crypto/bank paid remainders also remain queued. No immediate-sale shortage credit or unpaid timeout on paid obligations. Immediate sales keep their original policies. |
| Restock and recovery | Commit drain work with inventory addition, then atomically commit full assignment, whole coupon redemption and one complete-manifest delivery intent. Replay cannot debit/allocate/redeem twice. |
| One complete logical delivery | Prepare the whole manifest before exposing any credential/file/link; multiple Telegram messages and resumable transport are allowed, not separate partial business batches. A split-delivery table is not justified for new behavior; verify existing transport links and preserve actual historical batches separately. |
| Expected dates | Owner-approved target: optional nullable estimate; local preorder creation/publication/purchase must work without a date. Staff may set/update/clear it; preserve original quoted date/absence. No estimate-driven paid-order expiry/cancellation/FIFO/coupon/credit change. Staging's mandatory-date rule is not copied. Exact automatic change notifications remain under review. FR-069 / SC-038. |
| Cancellation / queue holds | Staging can block later orders behind pending cancellation. Confirm whether target retains that behavior and how rejection restores priority. No external settlement field or refund workflow may be copied. Internal wallet-credit policy for cancellation has not been newly approved. |
| Coupons | New target orders hold the entire claim while incomplete, then redeem it once with complete allocation/fulfillment. No split-unit usage policy is needed. Do not infer a new paid-cancellation or outside-refund release policy. Preserve legacy partial accounting for review. |
| Historical data | Review paid time/queue ordering, old cancellation states, allocated units and delivery batches using authorized redacted snapshots. Legacy external refunds remain separately verified archives under existing owner decision. |

## Before executable schema or implementation

- Apply the approved shared master-product selection scope; settle singleton protected configuration persistence and typed binding/blocker fields, then verify the 27-table candidate's final constraints. Do not treat table approval as implementation or migration authorization.
- Apply optional-date FR-069; settle cancellation/hold and automatic estimate-change notifications, validate whole-order coupon/manifest guards and reconcile historical partial-delivery evidence.
- Prove batch/event unit membership, provider item dedupe and encrypted response recovery. Canboso's original response may be irrecoverable if the process dies after remote success but before durable capture; a database transaction cannot make that external boundary atomic. Such a gap requires manual reconciliation, not a blind repurchase or invented delivery.
- Verify provider currency, idempotency semantics/key lifetime and result-retrieval shapes with authorized fixtures/provider tests later. Do not treat this matrix as completion evidence.
