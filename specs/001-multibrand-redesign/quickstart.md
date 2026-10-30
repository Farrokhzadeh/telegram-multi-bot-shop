# Offline Validation Quickstart — Backend Redesign

**This is a rehearsal contract, not an executable deployment recipe.** The specification repository now exists at `Farrokhzadeh/telegram-multi-bot-shop`; host code has not been implemented. Do not run live bank/crypto/Telegram or database migration commands from these examples.

## Prerequisites

1. New isolated repository with the planned C# solution, a .NET 10 SDK and a C/C++ toolchain for Native AOT publish.
2. Disposable PostgreSQL database populated with **synthetic** three-brand, payment and encrypted fixtures, with fake provider/Telegram endpoints.
3. Test-only brand keyrings; never use production bot tokens, keys, customer credentials or payment endpoints in local tests.
4. Implemented test project names/targets corresponding to the plan. Final commands must match generated project files.

## Proposed validation sequence

### 0. Relational schema gate

- Inspect the disposable PostgreSQL `information_schema.columns` after applying all migrations. There must be **zero** `json` or `jsonb` typed columns.
- Verify typed FKs, composite brand/customer ownership, wallet idempotency, inventory-to-fulfillment uniqueness, typed job origins and nullable conversation state assumptions.
- If regional discounts or multiple eligible products/customers require junction tables, stop and amend the reviewed schema instead of encoding collections in text, arrays or JSON.

Expected: the typed physical schema matches the approved table list, and no unsafe table removal was used merely to reduce the count.

### A. Build proof

```bash
dotnet test
dotnet publish src/Platform.BotHost/Platform.BotHost.csproj -c Release -r linux-x64 -p:PublishAot=true
dotnet publish src/Platform.Api/Platform.Api.csproj -c Release -r linux-x64 -p:PublishAot=true
dotnet publish src/Platform.JobHost/Platform.JobHost.csproj -c Release -r linux-x64 -p:PublishAot=true
```

Expected: zero unexpected AOT/trim warnings; each published native executable starts, responds to a health probe or synthetic update, and fails safely without required configuration. Actual commands and warnings must be recorded in CI after projects exist.

### B. Brand and customer isolation

- Create identical Telegram IDs in A, B and C with different **USD** wallets/orders/languages; each brand membership has its own distinct `users.id`.
- Verify `wallets(user_id PK)` and `wallet_entries(user_id FK)` have **no currency columns**, while order/payment currency facts still distinguish USDT from toman. A 100%-free in-stock order succeeds even if a USD-wallet row has not been created yet.
- Verify every outbound private DM resolves `chat_id` from `telegram_deliveries.user_id → users.telegram_id`; confirm `telegram_deliveries` has no `chat_id` and that forced-join checks still use `brand_channels.chat_id`.
- Simulate two overlapping private-chat menus from one user: a stale callback must fail `users.flow_revision` and may not reuse a newer active purchase flow. Group/topic customer commerce is unsupported in v1.
- Exercise all customer actions and bot callbacks with each authenticated brand context.
- Attempt forged or mismatched brand/context IDs; all cross-brand data retrieval and mutations fail.
- Create per-brand encrypted token and required-channel records; normal admin reads reveal only metadata.
- Encrypt unsold shared-stock credentials with a **different platform-inventory test key**, and prove a brand bot's key cannot decrypt it.

Expected: zero foreign-brand data in customer output; admin audit identifies each allowed mutation; an isolated bot consumer cannot open shared stock without the privileged inventory service.

### B2. Meaningful bot activity (table 25 in the 27-table approved candidate)

- In separate Brand A/B/C private-DM fixtures, execute start, menu/navigation, product view, button action, quantity selection, checkout progression, support entry, a real flow timeout/abandonment transition and an actionable error. Expect one typed `bot_activity_events` entry per **logical** event, linked only to the originating `users.id`.
- Replay Telegram updates and the timeout worker 100 times; deterministic event keys prevent duplicates while separate legitimate actions remain distinct. Wrong-brand `order_id` or `support_ticket_id` event references fail at the PostgreSQL boundary. Query by time, brand, membership and action through authorized pagination; customer access to another brand's timeline is forbidden.
- Prove no raw DM message body, callback data, coupon code, credential, receipt, provider secret or JSON payload persists; domain orders/ledgers are still authoritative. Verify FR-071's 180-day operational retention and legacy `user_activity` archival/reconciliation plan without deleting protected source evidence.

Expected: accurate brand-isolated behavioral history, no duplicate replay spam, and no private message capture or money-source ambiguity.

### B3. 180-day bot activity retention (FR-071 / SC-040; future checks)

- Freeze trusted UTC time and insert events just newer than, exactly at and just older than the 180-day cutoff. Read filtering and cleanup agree; retain equality and delete only older activity.
- Interrupt/replay bounded cleanup while new events arrive. No unexpired/foreign-purpose records are removed; monitor cleanup lag/errors without copying event contents.
- Verify users, orders, payments, ledgers, coupon revisions, support, admin audit and delivery evidence remain unchanged. Financial/domain replay tests still pass after old analytics rows are deleted.
- Restore/import expired target activity or replay an old logical event: preserve original time and apply the cutoff before exposure, without a new timestamp or indefinite raw-event archive. Protected source archives/backups remain outside this cleanup.
- Review indexes, least-privilege delete scope, scheduling/monitoring and backup restore behavior before implementation. These are future validation cases, not executed database deletion.

### C. Atomic wallet checkout

- Set one available local credential, 50 buyers with sufficient balance, and fire 50 simultaneous requests.
- Crash/restart a delivery worker after database commit but before Telegram send; replay same checkout ID and delivery job.

Expected: one allocation, one debit and one order; other wallets unchanged; durable delivery recovers without a duplicate charge.

- Request 3 units when only 2 units remain, with a wallet balance sufficient for all 3.
- Confirm that no stock is assigned, no wallet is debited and any presented coupon remains unused.

Expected: a multi-unit wallet purchase commits **all requested units or none**, and all per-unit fulfillment records match the order quantity.

### C2. Automatic coupons and current-source FX

- Simulate one advance purchase of 100 future units and verify it issues **exactly one** account-bound 100%-off code with `max_total_uses=100`, `max_uses_per_user=100`, product/brand ownership, and a paid-source reference. Redeem 1, then 9, then 20 units in later orders; expect precisely 70 unspent/unheld uses and 30 recorded redemptions, **without 100 coupon rows or advance stock allocation**. Invalid 71-unit request returns max 70 without creating an order.
- Verify the assigned user's “My Discounts” view and notification can reveal the code using the correct brand key, while another brand, an ineligible user and ordinary admin list output cannot read its plaintext.
- Issue a **prepaid** 100%-off entitlement to a specific Brand A member from that person's paid product order. Try using their revealed code text as another Brand A customer and as the same Telegram ID in Brand B. Both must fail without consuming the paying owner's remaining quota. Attempt cross-brand/other-customer `eligible_user_id` and `source_order_id` pairings directly at the database layer: reject. An imported paid legacy code without verified original owner must produce a migration exception requiring reconciliation rather than guessing an owner.
- Recreate the current automatic best-code selection; a customer cannot manually disable or skip an eligible required coupon.
- With a 100%-off code at **10 remaining uses**, enter quantity **11** in both currencies: show **"Maximum quantity 10"** and remain at quantity selection, with **no failed order created**, stock allocation, code consumption or wallet debit. Then request **10** with enough stock: apply exactly 10 uses atomically.
- Redeem the same code concurrently from two requests whose combined quantities exceed the remaining uses; only allowed uses can commit.
- Create an unpaid 50%-discounted bank/crypto order: it gets `discount_uses(status='held')` and reduces available coupon uses, **without reserving any inventory**. Create a competing order for the last available discounted unit: it cannot overclaim. Expire the first without proof: the claim changes to `released` and capacity becomes available. Submit an on-time receipt on another order and let review run beyond 30 minutes: its claim remains held; successful fulfillment finalizes `redeemed` exactly once.
- Redeem a valid 100%-off code with a zero or absent wallet balance; successful in-stock fulfillment must not require a wallet debit.
- Run 10% and 50% campaigns and validate use limits, separate from prepaid code provenance.
- With a 10%-off campaign limited to 10 eligible units, enter quantity 11 and verify a correction prompt **maximum 10** with no mixed-price checkout, new order, hold or money movement. Repeat with 50% and 100% codes. Change a promotion's cap through the authenticated owner backend and prove the next quote uses the latest valid capacity without altering existing frozen orders. Then deliberately reduce cap **below redeemed + held**, confirm the owner edit succeeds with a full before/after audit, new available capacity clamps to zero, and already-held orders remain intact.
- Quote two brand listings of the same master product: one with `auto_price_toman=true` using fresh Nobitex USDT/RLS ÷ 10, and one with `auto_price_toman=false` using explicit toman price. Verify hourly refresh, stale-rate fail-closed, and **ceiling to 5,000 toman after discount**.
- Confirm `orders.user_id`, `unit_price_usdt`, `currency`, `payment_method`, and nullable toman unit/rate fields. There must be **no `total_usdt`/`total_toman` or repeated `brand_id`/`telegram_id` on orders**. A USDT-only checkout is valid with toman/FX fields NULL. Historical displayed totals must recalculate identically using the saved calculation version; `payments.amount_due/received` and immutable wallet ledger must agree with actual financial movements.
- Check that disabling `auto_price_toman` freezes manual toman prices **without removing USD wallet or USDT crypto payment** if that listing has a positive independent `price_usdt`.
- Attempt to create an order for a product listed only in Brand B using Brand A's `users.id`; the database must reject the brand mismatch even if the application fails to filter the listing. Campaign/support/coupon cross-brand FK checks must fail likewise.

Expected: no phantom/partial coupon redemption, no cross-brand coupon use or hidden cash-refund claim; legacy rounded quote parity and reproducible typed order snapshots **without** an `exchange_rates` table.

### C3. Coupon revision audit (table 24 in the 27-table approved candidate)

- Issue a coupon and verify its initial `discount_revisions` snapshot. Apply authenticated owner changes to percent-off, caps, effective used, activation/validity and eligibility after some units are genuinely redeemed and others held; verify one new immutable version and one linked `audit_events` actor/time/reason record per edit.
- Check prior and next revisions reconstruct actual before/after edited settings while current `discounts.redeemed_count`, accepted order price snapshots and historical `discount_uses` are never rewritten. Normal redemption must not create a fake owner-edit revision.
- Race an edit against held-claim creation/redemption and inject an abort between coupon update and revision insert; the transaction commits all facts once or rolls back all, with no gaps or split-brain revisions.

Expected: typed immutable owner-edit history with accurate at-edit redeemed/held context, no JSON/EAV audit data and no change to owner override authority.

### D. Externally paid checkout and manual review

- Verify a crypto payment, exhaust stock before allocation, replay the verified provider event 100 times.
- Apply a 50% code to a $10 USDT-quoted order, pay the $5 invoice through approved NOWPayments USDT network and exhaust local stock before the verified settlement allocates it. Verify **exactly one $5 USD wallet credit**, all units unfulfilled, `discount_uses.status='released'`, unchanged `discounts.redeemed_count` and available coupon use restored. Replay the callback 100 times; no repeated credit or coupon transition. If the campaign expiry passed before compensation, **preserve `ends_at` unchanged, do not automatically issue replacement goodwill, and explain that the returned use cannot be used for a new order** (owner-approved Option A).
- Hold a campaign coupon at 11:55 PM with a valid bank receipt or crypto evidence before the payment deadline; advance past its midnight expiration and fulfill at 12:05 AM. Verify that the originally held discounted order can complete despite expiry, while a brand-new order cannot claim that code. Run the equivalent no-stock compensation: returned campaign capacity stays expired; a separately paid 100%-off future-unit code with `ends_at IS NULL` remains available after release.
- Review a bank receipt after stock has been exhausted; later manually resolve it.

Expected: precisely one **USD wallet credit numerically matching the frozen USDT `payments.amount_due`** under the approved 1:1 accounting convention; receipt remains paid/unfulfilled indefinitely unless explicitly resolved with audit evidence. Existing USD wallets keep their denomination in the new platform.

### D2. 30-minute unpaid invoice and Telegram edit

- Create a pending bank/crypto order. Confirm both the payment page and Telegram payment message warn clearly that the order/payment is **valid for 30 minutes**, with the exact expiry time.
- Advance time to minute 30 without a receipt or qualifying crypto payment evidence. Run expiry worker twice, including after restart; expect exactly one transition `awaiting_payment → expired` and an idempotent edit attempt targeting the **original message ID**. Simulate Telegram edit rejection: record a retry/fallback, but do not revert expiry.
- Submit a receipt at minute 29; review it at hour 2. Confirm payment can be approved and wait for restock forever. Observe qualifying crypto transfer at minute 29 and final confirmations at minute 45; do not expire it merely for confirmation latency.
- Simulate late real money after order expiry; keep an immutable payment record and route to the explicit owner-approved reconciliation path. Do not silently forfeit or re-fulfill after expiry. Simulate a boundary race between expiry job and webhook/receipt to prove only one valid state wins.
- Settle a correctly bound, full-value TRC20/BEP20 USDT payment *after* the 30-minute expiry or after a later order replaced it. Verify a single **USD-wallet credit for the original brand membership**, no revive/rebind/stock allocation/coupon redemption and correct customer notice; replay event 100 times. Route underpayment, overpayment, wrong-network and uncertain provider evidence to staff, as well as all late bank transfers. Test the boundary race between timely evidence and expiry.
- Simulate a confirmed-paid bank order pending restock while an administrator refunds or delivers it entirely **outside AccAbad**. No external refund record or API is expected. Without an explicit order-state update, the bot must not infer that event and the paid obligation/coupon hold remain. If staff choose to record an outcome in the admin backend, log actor/reason and the exact resulting coupon state. Raise a clear operator warning about otherwise indefinite holds. As owner, change a prepaid coupon's 100-unit cap to 5 despite 30 redeemed and 10 held: **allow it**, keep old holds and prior order snapshots unchanged, show zero new availability and audit the change. Set its **logical used count** from 30 to 0 using `usage_adjustment_units=-30` without deleting any actual `discount_uses` history; edit percent-off and `ends_at`/`starts_at`, verify only new quotes use the new terms. No automatic cash refund is triggered by an owner coupon edit.
- Create a bank order, then successfully create a crypto order for the **same brand membership**. Verify the original `awaiting_payment` order is **cancelled immediately**, its held discount capacity is released, its immutable method/quote/provider ref preserved, and its original Telegram payment message is edited to **Cancelled — replaced** (not Expired); the new order has a fresh 30-minute deadline. An attempted new order with invalid quantity or quote must **not cancel the original**. Simulate late real bank/crypto money to the cancelled invoice: reconcile against the **original** order, never the replacement.
- Race a new order against a receipt submitted at minute 29 or an on-time crypto observation; once the old order reaches `under_review` or `confirmation_pending`, it is **not automatically cancelled** by replacement and its coupon hold remains. A paid order awaiting restock is likewise protected. Attempt two simultaneous checkout creations for the same membership: a partial unique index must prevent two `awaiting_payment` orders; the losing request must not strand/corrupt a coupon hold. Repeat with the same Telegram ID in a different brand: membership isolation permits its own pending order.
- Create a legitimate **NOWPayments crypto-funded USD wallet top-up** with no product order (as supported in legacy `main`); verify `payments.order_id IS NULL`, `payments.topup_user_id` references the right membership and `num_nonnulls(order_id,topup_user_id)=1`. Settle the same verified webhook repeatedly: exactly one USD wallet/ledger credit. Confirm topup attempt creation can work before the wallet row exists without bypassing brand/zero-balance eligibility guards.
- Try to top up by card-to-card, or to pay an external **USD** order by card-to-card: reject both. Create a product order paid by **TOMAN card-to-card**, another by **NOWPayments crypto with a USD-valued quote**, and another by an **existing USD wallet**. Confirm each has a single immutable method, and the payment record retains actual cryptocurrency/network quantity separately from its USD quote. Check that **only `USDTTRC20` or `USDTBSC`** can be chosen for new NOWPayments invoices; an invoice selected as TRC20 cannot be fulfilled by a BEP20 callback (or vice versa), nor by native TRX, BNB or any other asset. Validate codes against live provider availability and verify invoice expiry; don't assume a wallet top-up shares the order's 30-minute deadline.
- Start simultaneous NOWPayments invoices from all three brands through the **same merchant account**. Confirm each has a globally unique provider payment reference and a distinct internal `payments` origin; re-send one brand's signed provider callback with a forged brand/description field and verify it cannot credit, fulfill or expose any other brand's record. Inspect provider-hosted payment pages/receipts for a shared merchant name, payee identifier or unrelated brand label; record the operator's privacy acceptance or mitigation before launch.
- Configure **different receiving bank cards** for the two Iranian brands; confirm each brand displays only its own card on TOMAN bank checkout and that the international brand need not have one. Attempt assigning the same card to both brands: reject via keyed fingerprint constraint. Create an unpaid bank order, save a replacement card and confirm **the last successful admin save is the sole active card for new orders**, with no list of active cards. A simultaneous stale admin save must fail its revision check. The old order still shows its **original destination**, original 30-minute deadline and receipt review snapshot; new orders show the replacement. Inspect banking-app holder-name display as a public brand-isolation risk.

Expected: customer-visible **Cancelled — replaced** on valid replacement and **Failed/Expired** on genuine timeout, temporary coupon capacity returned exactly once, no premature cancellation of on-time payments, no missing late money or repeated wallet compensation.

### E. Supplier and broadcast

- Simulate supplier success, failure and timeout-after-success. For five requested/three acquired, persist three encrypted internal units, release zero customer content and retain the whole paid order under admin review with all coupon uses held. Export/status/restart replays cannot buy again or create partial delivery. Persist pre-call attempt intent and sanitized terminal/unknown evidence with per-unit dedupe and brand-key boundaries. Release only a complete verified five-unit manifest after any review clearance.
- Send one campaign batch across three eligible bots for a Telegram ID in all three memberships; include multiple language variants in the international brand, block one bot, simulate Telegram throttling and restart worker.

Expected: uncertain supplier outcome stays pending reconciliation; incomplete 3-of-5 acquisition leaves the whole paid order pending review with zero customer release, no repeated supplier purchase, no duplicate *local obligation*, and no unapproved compensation. Campaign records remain separate per brand; no user gets two recipients in the same brand/batch due to competing language variants. Telegram remote sends are best-effort reconciled after ambiguous responses.

- Verify supplier-attempt historical mappings and sanitized evidence. Five held coupon uses stay held with three acquired items and zero customer fulfillment; full verified fulfillment redeems five once. Preserve actual historical partial deliveries separately; do not erase evidence or redeliver previously delivered units to force the new policy.

### E1. Source selection and local-preorder FIFO (FR-064/FR-065; future checks)

- Create an unpaid order with A selected, switch to B, then accept payment: bind to B. Opening an invoice or submitting an unapproved bank receipt does not bind. Verify committed wallet debit, accepted on-time crypto and admin-approved bank payment; 100 repeated callbacks after switching to C retain B, original pricing and one purchase intent. Disable/remove the selected source or mark supply unavailable at acceptance: preserve accepted payment and visible paid/unfulfilled admin review, no runnable purchase, fallback, credit or external refund. Later re-enablement alone cannot clear review. Race source edits against payment binding and verify one coherent source revision; late expired/replaced crypto retains wallet recovery without procurement.
- Verify approved table 27 `product_supplier_sources`: same-product FK/selection guards, controlled provider codes, separate product/optional-variant identities, null-aware duplicate rejection and revision-safe edits. A paid request keeps its original source ID/revision and actual account/product/variant snapshots after a mapping edit; referenced history cannot be silently deleted or redirected.
- Map several products to one provider and verify they use its one configured account across all brands, without credential copies, per-brand/account overrides or provider account pools. Rotate the credential for that same account: paid requests retain their original account identity. If config points to another account or original-account access is unavailable, old-request reconciliation must stop for admin review rather than use the replacement account. Do not put tokens in mappings/snapshots/evidence.
- Configure two product sources with separate product/variant identities. Select A, bind a request, switch to B, then retry/reconcile the old request: it stays on A's original account/product snapshot; only newly bound work uses B. Bind equivalent requests in all three brands, switch the shared product source, and verify newly bound work in all three uses B while already bound requests retain A. Reject per-brand source overrides and cross-product source selection. Race admin edits and verify revision checks, one product-level active authority and actor/reason audit. No automatic failover after unknown/partial results.
- With zero stock, pay preorder A for five and B for two in another brand. Add three: neither allocates/releases anything; no subset reservation or B bypass. Restart/replay and add three: A atomically allocates all five, B waits with only one item available. Add one: B gets both. Race all allocators/payment/restock; no duplicate debit, item, coupon transition or partial manifest.
- Test wallet, verified crypto and accepted bank preorder payment: full payment persists once and absent units remain queued, without immediate-order shortage credit. Paid remainders survive the unpaid timer. Queue fairness spans brands; customer data does not.
- Prepare a complete order manifest before any customer credential/file/link release; exercise all access paths and interrupt/resume multi-message Telegram transport. No partial business batch, duplicate logical fulfillment or coupon redemption; remote sends retain the existing best-effort duplicate limitation.
- Re-run immediate-sale all-or-nothing wallet and crypto compensation scenarios. Supplier 3-of-5 still waits for admin review, outside local FIFO.
- Gate implementation on source/account/blocker field design, whole-order coupon/manifest guards, cancellation/hold/date policy, historical partial-order compatibility and exact transport links. No staging external-refund workflow is imported.

These are future checks, not live staging results. The approved candidate contains 27 tables, including `product_supplier_sources`; its exact constraints/config and any further transport relations still need review. No new split-delivery table is implied.

### E1b. Supplier product eligibility (FR-068 / SC-037; future checks)

- Enable/publish verified ready-to-deliver account/key/code mappings with separate exact product/variant identity. Unverified requirements must not default to eligible.
- Reject email activation, buyer-email slot assignment, existing-account upgrades and manual/custom supplier delivery before creating a payable checkout, invoice or wallet debit. No customer fulfillment-input prompts/payloads are created.
- After accepting payment for an eligible item, simulate a provider requirement change before purchase: retain original source/payment and full paid obligation for admin review, with no purchase/fallback/automatic compensation. Retry/replay never duplicates or loses that obligation.
- Allow ordinary redemption instructions for delivered codes and asynchronous provider processing/status/export. Keep full-order manifest release, one-time credential capture and source account isolation unchanged.
- Reconcile any historical excluded product obligations explicitly; do not silently reclassify old customer orders or discard source evidence. No new operational input/activation table is implied.

These are specified future checks, not executed live-provider results.

### E1c. No configured preorder date or paid time cap (FR-069 / SC-038; future checks)

- Create/publish/quote/pay a local preorder without ETA/date/duration inputs. Verify target product/admin/customer contracts and schema do not expose the removed date/snapshot fields or create date-change/fulfillment-expiry jobs.
- Advance the clock well beyond usual waits: paid order, FIFO priority, full coupon hold and financial history remain; no automatic expiry/cancellation/reorder/credit/refund.
- Add enough stock to complete the oldest order: fulfill normally under the existing full-quantity FIFO rule. Support ticket activity alone cannot alter it.
- Verify created/paid audit times remain available, and an unpaid external invoice still follows the separate 30-minute payment window without affecting a confirmed-paid preorder.
- Preserve/reconcile any genuine source promised dates without erasing source evidence or reintroducing active date settings. These are future checks, not live tests.

### E1d. Preorder cancellation requests via support (FR-070 / SC-039; future checks)

- Open an existing order-linked ticket for the customer's paid queued preorder. Reject foreign-member/brand order links with no information leak.
- Create/reply/assign/close/reopen the ticket and replay updates: payment, FIFO priority, coupon holds, balance and stock stay unchanged; no cancellation-specific button/endpoint/request state or automatic queue pause is created.
- Confirm normal complete-order fulfillment remains eligible while support discussion is open. Support text/status cannot authorize financial/order transitions.
- For a separately approved admin resolution, verify actor/reason audit, retained paid history and serialization with allocation so stale workers cannot allocate afterward. Do not invent cancellation settlement/coupon rules or require external payout evidence.
- Keep final admin cancellation outcome/hold and coupon/internal-wallet policy as separate gates before implementation. These are future checks, not live test results.

### E2. Human support without AI

- Disable or disconnect the entire AI service.
- From each brand open a support ticket, attach a test media reference, reply as staff, and restart the notification worker between message creation and delivery.
- Attempt to fetch the ticket through another brand's authenticated context.

Expected: the human ticket remains actionable, one durable staff/customer notification is recorded, no cross-brand details leak, and AI downtime does not delay purchase or support.

### F. Encryption migration rehearsal

- Use synthetic legacy keys and sample Django-format ciphertext, including historical prefixes/previous-key attempts.
- Import repeatedly, re-encrypt sensitive fields with new test keys, decrypt and compare plaintext only in test assertions, verify mapping and counts.
- Deliberately use wrong/missing key and corrupt ciphertext.

Expected: exact round-trip, no plaintext logs, failed unsafe imports, no second balance credit after rerun. Old-key recovery remains possible for protected old snapshots.

### F2. Opening balances and legacy feature retention

- Import the same synthetic historical ledger and source opening balances twice; verify no posting duplication.
- Examine real approved source-schema evidence for single-product order shape before omitting `order_items`; preserve any exceptional historical multi-product orders losslessly.
- Exercise each operator-approved retained discount, exchange-rate staleness/rounding, zero-wallet topup policy and regional payment rule.

Expected: every wallet equals its frozen source balance, an incomplete ledger causes explicit reconciliation failure, and price/payment rules match the approved behavior matrix.

### G. Stage rehearsal (only after operator authorization)

- Confirm immutable release identity, backup and restore result.
- Stop **all** writers for one brand and reconcile inflight callbacks and deliveries.
- Empty/disable active source stock for that brand deliberately; ensure no remaining legacy brand may allocate new-platform stock.
- Compare historical balances, paid obligations, order snapshots, encrypted-field migration and callback ownership.
- Test failure/rollback in isolated staging before any production canary.

Expected: no split brain, no orphan paid order, no key loss, and approved data reconciliation report. Failure is a deployment stop.

## Evidence to archive with the release

- Commit/image digests, target platform, publish warnings, test counts, synthetic fixture hashes, redacted migration reconciliation, key-version inventory without values, provider callback evidence, baseline vs new RSS/startup measurements, signed approval and rollback observation.

## Not included

- Admin web UI/screenshots, frontend performance tests, public website deployment or AI model/provider implementation.
