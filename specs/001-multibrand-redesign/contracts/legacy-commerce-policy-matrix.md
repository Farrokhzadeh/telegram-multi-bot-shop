# Legacy commerce policy matrix

Reviewed 2026-11-01. Source `Farrokhzadeh/AccAbad-Shop/main` resolved to `66371a23a1c8149dc5bdf89a0355aa689d454dba` (commit date 2026-10-26). This is repository-code evidence, not deployed behavior or historical data verification. It supplements the earlier T006 snapshot; it does not replace protected E2/E3 evidence. No configuration values, keys, database rows or external services were accessed.

The retained/changed decisions below implement the already approved specification, particularly FR-034/046/047/055/057/058/060 and the canonical schema. They introduce no new business policy. T030–T036 must use this matrix for implementation and synthetic parity fixtures.

| Topic | Verified source behavior | Approved target behavior |
|---|---|---|
| Automatic coupon choice | [`best_discount`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/discounts.py#L101) considers active product/global and assigned-user/global codes, ordered by code. It minimizes the tuple `(total_usd,total_irr)`; equal totals retain the first code. | Retain automatic best eligible selection without customer opt-out; apply brand-membership/product scope and stable tie-breaking. |
| One code, bounded uses | [`calculate_discount`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/discounts.py#L58) computes `remaining=max_uses-current_uses`; source `consume_discount` caps the updated counter. | One code with quantity-based coverage. Real redeemed history stays immutable; effective used is `max(0,redeemed_count+usage_adjustment_units)`. Subtract temporarily held units and clamp availability to zero. Serialize reservations/owner edits; no artificial owner cap floor. |
| Quantity above coupon allowance | Source discounts `min(qty,remaining)` units and charges the rest normally, including 10%, 50% and 100% codes. | Intentional change: validate whole quantity before order creation; return `quantity_exceeds_discount_limit` plus maximum. Never partially discount a quantity or silently bypass its applicable cap. |
| Eligibility | [`discount_applies`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/discounts.py#L44) enforces active flag, optional product and assigned user. | Retain optional scope for promotion/goodwill; prepaid codes require the paid source order, same brand membership and product, and are nontransferable. One N-use code per fully paid future-unit purchase; campaign/goodwill batch generation is separate. |
| USD and toman calculations | Source computes both totals. Discounted USD and manual toman totals use `Decimal.quantize(0.01)` with the source default half-even context; automatic toman is recalculated from the discounted USD total and raw FX. | Preserve source calculation order in a versioned quote policy. Do not infer cent rounding from the unrelated input validator's explicit half-up rule. Store exact typed snapshots and avoid binary floating point. |
| Input precision | [`parse_money`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/validators.py#L15) rejects nonfinite amounts, bounds values and normally rejects more than two decimal places; explicitly permitted rounding uses half-up. | Keep validated/versioned quote-input semantics separate from lossless NUMERIC storage and exact .NET mapping. Never truncate historical higher precision during import. |
| FX source and units | [`parse_nobitex_rate`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/pricing.py#L22) requires status ok, open `usdt-rls` market, finite latest RLS in [10,000,1,000,000,000], then divides by 10 to obtain toman. | Retain USDT/RLS as USD proxy and RLS/10 conversion. Parse synthetic fixtures only in the current batch; provider compatibility remains E2. |
| Ceiling and refresh | [`rounded_rate`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/pricing.py#L42) ceilings the converted total to a multiple of 5,000 toman. Refresh is hourly, with a two-minute source refresh lease and per-hour job dedupe. | Preserve ceiling-to-5,000 and hourly refresh. Use typed current brand FX state and fenced durable jobs; retain accepted FX only on orders. No historical rate table or network call inside a DB transaction. |
| Staleness and failure | [`assert_price_available`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/pricing.py#L105) rejects missing/stale automatic FX when enabled; equality at cutoff is fresh. Source has a bounded migrated-price bootstrap exception. Refresh failure keeps last valid prices. | Qualify configured maximum age and current FX enablement; no silent stale quote or blanket migration exception. Explicit import compatibility remains E3; old accepted orders retain their quote. |
| Manual price opt-out | [`prepare_product`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/pricing.py#L95) clears rate linkage for `auto_price=False`; manual toman prices bypass automatic FX staleness. | Retain per-brand listing `auto_price_toman` and explicit toman price. Refresh cannot overwrite opted-out prices or reprice old orders. |
| Wallet denomination | [`purchase_item`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/checkout.py#L39) distinguishes source USD and IRR balances; source IRR-labelled product prices conventionally mean toman. | USD-only wallet per brand membership; internally frozen USDT-to-USD credit mapping. Show USD to customers. No IRR/TOMAN operational wallet. Preserve any source non-USD liability through E3 review, never relabel or discard it. |
| Funding eligibility | [`eligible`/`require_funding`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/wallet_funding.py#L16) require a finite strictly positive existing balance to initiate funding. Settlement/legitimate credits do not use this initiation gate. Source also permits bank funding via `authorize_bank` without a target product. | Preserve independent brand topup policy and zero-balance eligibility controls. Intentional change: new wallet funding is crypto-only. Bank/card funds settle an existing TOMAN order, never a wallet topup. |
| Standalone topup origin | [`PaymentAttempt`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/payments/models.py#L115) explicitly declares `kind='wallet_topup'`; `order` is nullable. New attempts retain user/expected amount/USD even with no product/order. | Preserve typed funding origin as `payments.topup_user_id` XOR `payments.order_id`. Topup works before any order or wallet row exists; it is not an order with a fake product. |
| Direct crypto checkout | [`create_payment_attempt`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/payments/services.py#L60) currently rejects `KIND_DIRECT` with `direct_card_only`; legacy direct callbacks still have reconciliation code. | Intentional owner-approved change: offer NOWPayments crypto payment for orders as well as crypto wallet topup. Source rejection must not be copied as target policy. Existing payment method/currency remain immutable; switching requires a new order. |
| Networks and account | [`SUPPORTED_PAY_CURRENCIES`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/payments.py#L23) restricts invoice assets to `usdttrc20` and `usdtbsc`; provider receives an opaque payment reference. | Only USDT TRC20 and BEP20, one shared NOWPayments account. Quote USD separately from network asset, and bind callback evidence to the original payment. No native float serialization in target financial payloads. |
| Regional payment rules | [`is_irr_enabled`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/common/services/settings.py#L368) derives source currency availability from independent bot configuration, not customer language. `authorize_bank` enforces it. | International brands: USD wallet/crypto only. Iranian brand bank order settlement: TOMAN only, with both brand and listing permission and the saved bank destination. Localization never grants a payment method. |
| Quote and public reference | Source `purchase_snapshot` saves terms/FX; `purchase_item` rejects a changed expected total before debit. `_create_order` stores one product/quantity and an `ORD-…` string ID. | Store immutable typed per-unit/product/brand/discount/rounding/payment snapshots with sole new public reference `orders.id` UUID. Preserve legacy public aliases through `legacy_id_map`; source `Order.product` declaration does not prove historical row cardinality or authorize dropping old `order_items`. |

## Synthetic calculation anchors

These are illustrative deterministic decimal checks, not execution of Django or verification of a provider response:

| Input | Expected |
|---|---|
| Nobitex latest RLS 1,234,567 | Raw toman rate 123,456.7 |
| USD 12.34 at raw toman 123,456.7 | Raw 1,523,455.678; ceiling 1,525,000 toman |
| USD 10 × quantity 2 × (1−10%) | USD 18.00 |
| USD 0.05 × (1−10%), source cent rounding | USD 0.04 (half-even tie) |
| USD 0.15 × (1−10%), source cent rounding | USD 0.14 (half-even tie) |
| Five units requested, applicable code has three available | Target rejects before order creation with maximum 3; source would discount three |
| Redeemed 2, usage adjustment −5, held 1, cap 4 | Effective used 0; target available 3 |
| Redeemed 4, usage adjustment 3, held 2, owner lowers cap to 1 | Target available 0; history and existing claims remain |

T030/T031/T035 own executable pricing/coupon fixtures, including manual/automatic prices, cent ties, equality at FX cutoff and owner-imposed overcommit. T037–T042 own verified payment settlement and the immutable method/currency contract. No live behavior is qualified here.

## Pinned file identities

| Path | Git blob SHA |
|---|---|
| apps/common/services/discounts.py | 566b758096060d47f8a0472fd68f8d5ce1fd74d7 |
| apps/common/services/pricing.py | 634c2d2e1447610ca56f964eb592e79575862df8 |
| apps/common/services/checkout.py | 81a2f59ee0771300f5e684a53ca3ca9f1e5dd0ab |
| apps/common/services/wallet_funding.py | 6c15a1f529ead1a9fdde554c4862d7b28f20fd4e |
| apps/payments/models.py | be02c1a908c764095e7d652b4b5eafd84c9fc4c1 |
| apps/payments/services.py | 5fead38ac15bf7bc2456be5ea2ff49749e407f6c |
| apps/bot/payments.py | e2b5fea31df0c95d47ce4a2c04f710b283952681 |
| apps/common/services/settings.py | 2243025e3cbc7ec59a3809e132bbf3febc1af715 |
| apps/common/validators.py | caf56733e6d452291b5ae7c009fda0fa026a416b |
