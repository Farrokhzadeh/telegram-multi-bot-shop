# T057 public supplier contract review — 2026-11-04

Reviewed the five current suppliers from [API_INVENTORY.md](API_INVENTORY.md) through their rendered official documentation. This supersedes the inventory's initial text-retrieval limitations for these five pages. No credentials, account data, catalog data requests, quotes, purchases, payments or other live operations were submitted. Opening an operation in Swagger only displayed its documentation.

## Decision

No reviewed purchase contract establishes a server-enforced buyer charge limit or a binding quote consumed by purchase. This is an evidence gap, not proof that a provider cannot offer such a capability. FR-074 therefore blocks restoring any automatic purchasing adapter. T057/T091 and provider-dependent gates remain unchecked. Do not weaken the inclusive 10% rule or treat an administrator's local approval as a remotely enforced cap. The existing worker already routes unverified capabilities to review.

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

Product detail carries variants and their stock/prices. A quantity quote returns current totals/availability, but no binding token or expiration guarantee consumed by purchase is defined. The order schema accepts variant items, currency and wallet payment, without a cap or idempotency input. The quote's existence therefore does not satisfy FR-074. Order details omit credentials; a separate delivery endpoint exposes credential arrays with nullable item identities. Exact eligibility, ambiguous-create correlation and stable unit identity remain unqualified. Never count the two delivery-array aliases as independent acquisitions without proof.

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

To advance a provider, obtain authoritative purchase-limit/binding-quote semantics including mandatory charges and currency, exact eligibility fields, stable unit identity and original-purchase/credential recovery guarantees. Then author its sanitized wire fixtures before implementing one adapter. Account-specific/live qualification remains separately gated. Do not ask the owner to resend saved documentation URLs.
