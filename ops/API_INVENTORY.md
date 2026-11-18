# Owner-supplied API inventory

Recorded 2026-11-04 from the owner's complete API list. This is the durable project reference for current and future integrations. Consult it before asking the owner to resend URLs. Preserve all entries even when an integration is deferred. Labels identify the supplied sites; exact provider codes, ownership and compatibility must be verified before implementation.

## Complete list

| Reference | API / service | Supplier URL and bot | URL type | Authentication / credential status |
|---|---|---|---|---|
| `akunding` | Akunding | [API reference](https://akunding.shop/api/docs) · [@Akunding_store_bot](https://t.me/Akunding_store_bot) | Documentation | Bearer token; value supplied, not stored in Git |
| `prodseller` | ProdSeller | [API reference](https://prodseller.com/api-docs/) · [@ProdSellerBot](https://t.me/ProdSellerBot) | Documentation | `X-API-Key`; value supplied, not stored in Git |
| `canboso` | Canboso | [API reference](https://canboso.com/api/swagger) · [@PremiKeyBot](https://t.me/PremiKeyBot) | Swagger documentation | Value supplied; type/validity unverified; not stored in Git. Purchase body `key`; history `x-buyer-key` per reviewed docs |
| `roboticvn` | RoboticVN | [API reference](https://api.roboticvn.com/api/v2/docs) · [@hiroboticvn_bot](https://t.me/hiroboticvn_bot) | Documentation | `x-api-key`; value supplied, not stored in Git |
| `aivaulthub` | AIVaultHub | [API reference](https://reseller.aivaulthub.store) · [@Gemini_Shop_Robot](https://t.me/Gemini_Shop_Robot) | Base URL | `X-API-Key`; value supplied, not stored in Git |
| `rapidx` | RapidX | [API reference](https://pay.rapidx.me/docs) · [@Clustershopbot](https://t.me/Clustershopbot) | Documentation | Bearer token; value supplied, not stored in Git |
| `aitoolifystudio` | AIToolifyStudio | [API reference](https://bot.aitoolifystudio.io/api/reseller/v1) · [@aitoolify_bot](https://t.me/aitoolify_bot) | API URL | `Authorization: Bearer`; value supplied, not stored in Git |
| `lahastore` | LahaStore | [API reference](https://lahastore.up.railway.app/api/v1/docs) · [@ver_pixel_bot](https://t.me/ver_pixel_bot) | Documentation | `X-API-Key`; value supplied, not stored in Git |
| `vokhong` | VoKhong | [API reference](https://vokhong.xyz/api) · [@Tainguyen_gptbot](https://t.me/Tainguyen_gptbot) | API/documentation URL | Value supplied, not stored in Git; authentication transport unverified |
| `qamify` | Qamify | [API reference](https://api.qamify.site/docs) · [@Qamify_bot](https://t.me/Qamify_bot) | Documentation | `Authorization: Bearer` or `X-API-Key`; value supplied, not stored in Git |
| `nastele` | Nastele | [API reference](https://nastele.online/api/partner/docs) · [@Gashopchipchip_bot](https://t.me/Gashopchipchip_bot) | Partner documentation | `X-API-Key`; value supplied, not stored in Git |
| `testflighty` | Testflighty MAX | [API reference](https://api-tgbot.testflighty.com/t/MAX/api-guide) · [@thaois_bot](https://t.me/thaois_bot) | API guide | `Authorization: Bearer`; value supplied, not stored in Git |
| `silvex-ai` | Silvex AI | [API reference](https://silvex-ai.com/api/public/reseller) · [@silvexaibot](https://t.me/silvexaibot) | API URL | `x-api-key` (owner supplied); value supplied, not stored in Git |
| `insightxpro` | InsightXPro | [API reference](https://api.insightxpro.store/docs) · [@insightXpro_bot](https://t.me/insightXpro_bot) | Documentation | `Authorization: Bearer` (owner supplied); value supplied, not stored in Git |

The AIVaultHub address is explicitly a base URL. Bot handles and credential-supplied status were updated from the owner's 2026-11-04 message. Bot ownership has not been checked. The November 18 table records authenticated read observations. Raw authentication values are deliberately omitted from this source-controlled inventory; no protected credential files have been provisioned by this update. Store actual credentials only through the existing protected configuration boundary. Authentication transports for the five reviewed providers come from [the contract review](SUPPLIER_CONTRACT_REVIEW.md); other transports remain unverified unless explicitly identified as owner supplied. Do not infer headers from key prefixes. URL formatting escapes were normalized without changing hosts or paths.

## Read-only credential checks — 2026-11-18

All 14 supplied entries were reviewed. Requests used the listed provider hosts and header-based authentication. The checks record HTTP status and JSON content type. No account IDs, balances, order payloads or credentials are included. HTTP 200 means the read returned a JSON response. Eligibility, purchase, delivery, live supplier recovery and production activation remain independently gated.

| Reference | GET path | Result | Note |
|---|---|---|---|
| `akunding` | `/api/v1/me` and `/api/v1/products/{product_id}` | HTTP 200 on both | The first client returned HTTP 403. The standard curl retest matched the prior owner-supplied account and the exact mapped product. Stock, availability and price fields were present. |
| `prodseller` | `/v1/balance` | HTTP 200 |  |
| `canboso` | `/api/v2/telegram-buyer/orders` | HTTP 401 | Header-auth request rejected. The documented balance route requires a key in the URL. |
| `roboticvn` | `/api/v2/wallet/balance` | HTTP 200 |  |
| `aivaulthub` | `/api/v1/me` | HTTP 200 |  |
| `rapidx` | `/api/me` | HTTP 200 |  |
| `aitoolifystudio` | `/api/reseller/v1/balance` | HTTP 200 |  |
| `lahastore` | `/api/v1/me` | HTTP 200 |  |
| `vokhong` | Supplied `/api` URL | Untested | Documentation URL returned 404. No verified authenticated GET route. |
| `qamify` | `/v1/ping` | HTTP 200 |  |
| `nastele` | `/api/partner/balance` | HTTP 200 |  |
| `testflighty` | `/t/MAX/api/telegram-buyer/balance` | HTTP 200 |  |
| `silvex-ai` | `/api/public/reseller/me` | HTTP 200 | The supplied API base, without `/me`, returned 404 with the header. |
| `insightxpro` | `/api/v1/balance` | HTTP 200 |  |

Akunding's current authenticated result leaves [T080's historical identity gate](T080_STAGING_REHEARSAL.md#isolated-accabad-checkpoint--2026-11-18) open because the frozen record lacks an account ID and its original credential received HTTP 401. The November 18 curl retest used the bearer authentication documented in Akunding's [OpenAPI schema](https://akunding.shop/api/openapi.json). No purchase or payment calls were made.

## Current supplier work

Start the T057 contract review with Akunding, ProdSeller, Canboso, RoboticVN and RapidX, the five providers already named in the [supplier matrix](../specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md). Keep all other entries for future review; this inventory does not add provider codes to the schema, expand the current product scope or activate transports.

Read-only documentation retrieval on 2026-11-04 returned these limited results:

| Provider | Retrieval result | Capability conclusion |
|---|---|---|
| Akunding | Documentation URL could not be accessed through the web retrieval tool; search did not return the API contract | Unverified |
| ProdSeller | Documentation URL could not be accessed through the web retrieval tool; search did not return the API contract | Unverified |
| Canboso | Swagger UI reached at `/api/swagger/`, but retrieval returned no readable endpoint/schema content | Unverified |
| RoboticVN | Documentation URL could not be accessed through the web retrieval tool; search did not return the API contract | Unverified |
| RapidX | Documentation URL could not be accessed through the web retrieval tool; search did not return the API contract | Unverified |

These are retrieval limitations, not findings that the services are unavailable or lack capabilities. The remaining nine entries have been saved without a capability review. The next read-only evidence step is to obtain readable official documentation or the site's actual linked OpenAPI schema, then record exact endpoint/version and evidence for eligibility, currency, enforceable maximum charge, purchase/item identity, uncertain outcomes and credential recovery. Never replace missing evidence with guessed endpoints or mock responses.

The [quarantine record](SUPPLIER_ADAPTER_QUARANTINE.md), [supplier qualification](SUPPLIER_QUALIFICATION.md) and existing per-provider gates remain authoritative. Documentation availability alone does not close T057/T091 or prove account-specific/live behavior. Do not call purchase/payment endpoints, use real credentials, enable a normal host or deploy as part of saving or reviewing this list. Store actual credentials only through the existing protected configuration boundary.

## Rendered documentation review — 2026-11-04

The initial text-retrieval limitations above were subsequently resolved using the rendered official pages for Akunding, ProdSeller, Canboso, RoboticVN and RapidX. The [T057 contract review](SUPPLIER_CONTRACT_REVIEW.md) records the observed contracts and remaining gaps. All 14 original entries remain preserved. The other nine services remain deferred; no automatic procurement adapter is qualified by this review.
