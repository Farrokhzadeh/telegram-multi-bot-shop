# ProdSeller supplier adapter

Source: the rendered official [ProdSeller v1 documentation](https://prodseller.com/api-docs/), reviewed 2026-11-04. No credentials or live catalog/order requests were submitted during this review. The adapter and all tests use generated credentials and synthetic response data.

## Transport and source scope

ProdSellerSupplierAdapter implements the existing supplier interface and supplies InspectCatalogAsync to the protected CatalogStore composition. It requires matching deployment/service/account credentials and explicit reviewed product ID/content-kind approvals. Products use the documented MongoDB ID; remote order references remain bounded opaque safe path segments. Only HTTPS prodseller.com:443 /v1/products/{id}, POST /v1/orders and GET /v1/orders/{id} are permitted. Credentials go through X-API-Key, are cleared from the request after transport, and never enter a URL. Default transport disables redirects and cookies. No account pools, runtime registration, dashboard or deployment are added.

Native billing is USDT. Product price is the account's actual unit price; publicPrice is not used for purchasing. Numeric stock must be present, delivery.type must be instant and requiresEmailActivation must explicitly be false. Custom delivery, null/missing stock, missing/unknown requirement flags and email activation are not ready. This is product-specific: the adapter does not reject a whole supplier because it also lists excluded products. Read-only selected-product detail is public to the trusted composition through GetProductAsync; no automatic catalog import or publication is introduced.

## Purchase and recovery

Owner-approved option A applies: obtain a fresh exact-source price/stock/readiness observation, compare against the immutable accepted baseline with the inclusive 10% local allowance, and consume that binding-specific proof after the durable attempt commit. No provider cap is claimed; EnforcesMaximum=false. The final supplier price may move after the check. Returned higher charges retain verified acquisition for existing administrator cost review without customer repricing, automatic delivery, refund or repeat purchase.

POST includes only productId and quantity. Idempotency-Key comes from the durable submit attempt, is distinct from the local request intent and respects the documented 100-character limit. There is no HTTP retry or automatic same-key POST replay. Missing original order IDs require manual reconciliation; ProvenIntentLookup=false. Recovery uses GET on the exact parent attempt's remote order. Pending and paid are processing. The docs do not prove that failed means the wallet was recredited, so failed remains unknown rather than confirmed-no-effect. A generic HTTP error, timeout or empty delivery never proves a safe new purchase.

The parser validates the original order/product/quantity, exact non-rounding amount, known state and absence of activation data. Delivered single-key and array aliases can describe one identical unit, never extra units; conflicting aliases, premature keys and incomplete counts remain unknown with protected evidence. Current data cannot silently become an activation flow.

## Credential identity and evidence

The official reference exposes credential strings without stable per-unit IDs. A separately qualified resolver is mandatory for producing verified unit references. Its returned kinds, customer dispositions, reference uniqueness and exact credential-byte multiplicities are checked. No unit IDs are derived from array positions or credential hashes. A resolver returning no units preserves the archived complete response for review without manufacturing fulfillment.

Bounded HTTP response retention, a monotonic single-use price-proof cache and byte-set verification are shared with Akunding in SupplierWire.cs. Response bodies are capped at 1 MiB, JSON depth is bounded and temporary/disposable byte buffers are cleared. ExactDecimal rejects unsupported precision instead of rounding supplier amounts.

## Executable scope and remaining gates

Synthetic tests cover a full protected catalog/checkout/attempt/purchase/archive/acquisition/manifest flow, durable key presence before HTTP, header cleanup, prepayment rejection of activation/custom/unknown items, single-key aliases, pending/paid/failed semantics, wrong identities/quantities, changed input type, malformed/over-precision responses, HTTP/timeouts and missing reference/key with zero POST.

These tests do not supply actual account identity, stable unit-ID guarantees, production evidence-archive durability, intended-environment rate-limit/scheduler qualification or a release decision. Normal hosts remain closed. T057/T091/T092/T093/T094 stay open for their remaining scopes. The earlier Akunding qualification run at fcccff396cc139ee58e0e968d186f3ba504a4860 passed 832 .NET tests and five native executables; the new exact-commit CI must pass before this continuation is called qualified.
