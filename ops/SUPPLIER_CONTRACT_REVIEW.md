# T057 public supplier contract review — 2026-11-04

Reviewed the five current suppliers from [API_INVENTORY.md](API_INVENTORY.md) through their rendered official documentation. This supersedes the inventory's initial text-retrieval limitations for these five pages. No credentials, account data, catalog data requests, quotes, purchases, payments or other live operations were submitted. Opening an operation in Swagger only displayed its documentation.

## Decision

No reviewed purchase contract establishes a server-enforced buyer charge limit or a binding quote consumed by purchase. This is an evidence gap, not proof that a provider cannot offer such a capability. The owner subsequently approved option A on 2026-11-04: a fresh verified price check within the inclusive immutable 10% allowance can permit an uncapped purchase, accepting execution-price movement after the check. Missing remote caps alone no longer block implementation. T057/T091 and other provider-dependent gates remain unchecked for their remaining identity/eligibility/recovery/account requirements. Do not represent a local check or administrator approval as a remotely enforced cap. The existing worker already routes unverified capabilities to review.

## Akunding

Source: [official reseller reference](https://akunding.shop/api/docs), especially pricing, idempotency, purchase and order objects; linked schema: [OpenAPI](https://akunding.shop/api/openapi.json). API root: `https://akunding.shop/api`; bearer authentication; billing denomination is USDT, not an asserted USD equivalent.

Purchase documents numeric product identity and quantity, without a ceiling or binding-quote input. Account-bound idempotency is documented, including different-content conflicts, unpaid-attempt retention and paid-but-undelivered states. A timeout must not authorize another purchase. Single-order reads/export recover delivered strings; persistent per-unit identity and ordering guarantees still need evidence. Labels alone do not prove absence of customer-input requirements. The page warns that its schema understates the idempotency-header requirement; preserve that discrepancy before generating a client.

## ProdSeller

Source: [official v1 reference](https://prodseller.com/api-docs/), product, purchase and order sections. API root: `https://prodseller.com/v1`; `X-API-Key`; account balance is denominated in USDT.

The product contract exposes instant/custom delivery and email-activation requirements. Activation and custom delivery are excluded under FR-068 even when inventory exists. Purchase takes product identity/quantity and optional activation inputs; no ceiling or quote-token input is documented. A stable idempotency header is described as replaying the first order, but retention, changed-content handling and reconciliation without a returned remote ID need stronger evidence. Order history/detail can return delivered credentials. Exact item identity and array stability remain unproven. Do not reuse the removed adapter's host, payload, price field or USD assumption.

## Canboso

Source: [Buyer API 2.2.0 Swagger](https://canboso.com/api/swagger/), purchase/history operations and request schema. Operations are under `/api/v2/telegram-buyer`. Purchase uses a buyer key in its body; history documents `x-buyer-key`. Never put actual keys in URLs.

Wallet pricing/currency depends on the key; history separates stored VND/USD amounts, and the purchase example uses VND. Products include ready accounts and excluded slots/upgrades requiring customer input. The purchase schema has no price cap or binding quote. Required idempotency replays the original response, but retention/durability and credential replay after process loss need verification. History lookup by order code excludes delivered credentials; it cannot alone recover acquisition. Bonus/final quantities are exposed, without sufficient stable per-unit attribution proof.

## RoboticVN

Source: [Customer API 2.2.0 Swagger](https://api.roboticvn.com/api/v2/docs) and its linked [OpenAPI](https://api.roboticvn.com/api/v2/docs/openapi.json). Root: `https://api.roboticvn.com`; `x-api-key`; currency enums distinguish `vnd` and `usd`.

Product detail carries variants and their stock/prices. A quantity quote returns current totals/availability, but no binding token or expiration guarantee consumed by purchase is defined. The order schema accepts variant items, currency and wallet payment, without a cap or idempotency input. The quote is a current price observation, not a remote cap; the owner-approved option A permits such a local check after its other requirements qualify. Order details omit credentials; a separate delivery endpoint exposes credential arrays with nullable item identities. Exact eligibility, ambiguous-create correlation and stable unit identity remain unqualified. Never count the two delivery-array aliases as independent acquisitions without proof.

## RapidX

Source: [Cluster Shop API 1.0.0 Swagger](https://pay.rapidx.me/docs), product/purchase operations; linked [OpenAPI](https://pay.rapidx.me/openapi.json). Root: `https://pay.rapidx.me`; bearer authentication.

Product documentation distinguishes manual delivery; keep those products excluded. Its separate Telegram OTP/session/cancellation API is outside this supplier implementation scope. Purchase documents product identity/quantity, without a cap, binding quote or idempotency parameter. The shown success schema is unspecified/string-shaped, so currency, purchase/item identity and credential recovery cannot be inferred. Order-list/detail routes exist but do not themselves prove usable acquisition/reconciliation semantics. Do not restore the removed adapter's invented transaction paths, response shapes or USD currency.

## Executable scope and remaining work

The existing synthetic tests already cover mapping-enable rejection, changed eligibility after accepted payment, zero submit on failed preflight, retained paid obligations, protected partial acquisition and full-manifest release.

This batch adds:
- Contract cases rejecting USDT/VND/case-changed currency under a USD binding and rejecting mismatched product/variant proofs.
- Database cases rejecting unready, excluded or unknown observations before wallet/crypto/bank checkout creates an order, invoice, debit, claim or procurement request.
- Zero-balance 100%-coupon rejection for unknown remote eligibility, alongside the full-local-stock exemption.

These tests exercise trusted observation boundaries, not real provider JSON parsing. Future parsers must explicitly translate missing/unknown requirements to rejection; a synthetic ready flag proves no provider capability. Exact activation, slot, upgrade, manual and unknown wire fixtures remain required for each parser before its implementation. No generic input form, account pool, table/column or runtime activation is added.

To advance a provider under owner-approved option A, obtain authoritative current prices including mandatory charges/currency, exact eligibility fields, stable unit identity and original-purchase/credential recovery guarantees. A remote purchase cap is optional; report its actual capability honestly. Then author its sanitized wire fixtures before implementing one adapter. Account-specific/live qualification remains separately gated. Do not ask the owner to resend saved documentation URLs.

## Provider continuation after option A — 2026-11-04

ProdSeller's rendered v1 reference now supplies exact wire fields for its offline adapter. The account-specific unit charge is price (not publicPrice), detail stock is numeric for instant items, and explicit requiresEmailActivation=false plus delivery.type=instant is required. The order returns orderId, product.id, quantity, amount and status with deliveredKey/deliveredKeys aliases. The documented idempotency header limit is 100 characters. No failed-order refund/no-effect guarantee or stable per-unit ID is established, so neither is invented. See [the adapter qualification scope](PRODSELLER_SUPPLIER_ADAPTER.md). No real account or purchase was accessed.

The current Canboso 2.2.0 Swagger UI explicitly documents the buyer key as a required catalog query parameter, while history uses its own header security. Do not infer header support for catalog reads or send actual credentials in URLs. Catalog transport remains closed until a supported safe authentication contract is verified. The purchase example also reports final/bonus quantities that are not independently matched by the example account array, so examples alone do not prove complete unit cardinality/attribution.

RoboticVN's published 2.2.0 OpenAPI confirms wrapped data for product/quote/checkout/order, wallet-only create inputs, explicit usd/vnd quote currency, quantity-specific realtime quote fields, separate delivery credentials and two delivery-array aliases. Order status/payment_status are unrestricted strings, and Delivery.item_id and per-entry quantity are nullable; these are not a verified completed-state or per-unit identity contract. Do not guess statuses or count aliases as separate deliveries. No real endpoint was called.

The RapidX documentation navigation timed out in this browser during this continuation. This is a retrieval limitation, not proof of supplier downtime or missing capability; the earlier contract review remains the available evidence. No guessed schema, authentication key or live purchase was used as a workaround.
