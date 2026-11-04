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
| `aitoolifystudio` | AIToolifyStudio | [API reference](https://bot.aitoolifystudio.io/api/reseller/v1) · [@aitoolify_bot](https://t.me/aitoolify_bot) | API URL | Value supplied, not stored in Git; authentication transport unverified |
| `lahastore` | LahaStore | [API reference](https://lahastore.up.railway.app/api/v1/docs) · [@ver_pixel_bot](https://t.me/ver_pixel_bot) | Documentation | Value supplied, not stored in Git; authentication transport unverified |
| `vokhong` | VoKhong | [API reference](https://vokhong.xyz/api) · [@Tainguyen_gptbot](https://t.me/Tainguyen_gptbot) | API/documentation URL | Value supplied, not stored in Git; authentication transport unverified |
| `qamify` | Qamify | [API reference](https://api.qamify.site/docs) · [@Qamify_bot](https://t.me/Qamify_bot) | Documentation | Value supplied, not stored in Git; authentication transport unverified |
| `nastele` | Nastele | [API reference](https://nastele.online/api/partner/docs) · [@Gashopchipchip_bot](https://t.me/Gashopchipchip_bot) | Partner documentation | Value supplied, not stored in Git; authentication transport unverified |
| `testflighty` | Testflighty MAX | [API reference](https://api-tgbot.testflighty.com/t/MAX/api-guide) · [@thaois_bot](https://t.me/thaois_bot) | API guide | Value supplied, not stored in Git; authentication transport unverified |
| `silvex-ai` | Silvex AI | [API reference](https://silvex-ai.com/api/public/reseller) · [@silvexaibot](https://t.me/silvexaibot) | API URL | `x-api-key` (owner supplied); value supplied, not stored in Git |
| `insightxpro` | InsightXPro | [API reference](https://api.insightxpro.store/docs) · Bot not supplied | Documentation | Not supplied; latest row incomplete |

The AIVaultHub address is explicitly a base URL. Bot handles and credential-supplied status were updated from the owner's 2026-11-04 message. Bot ownership and credential validity have not been checked. Raw authentication values are deliberately omitted from this source-controlled inventory; no protected credential files have been provisioned by this update. Store actual credentials only through the existing protected configuration boundary. Authentication transports for the five reviewed providers come from [the contract review](SUPPLIER_CONTRACT_REVIEW.md); other transports remain unverified unless explicitly identified as owner supplied. Do not infer headers from key prefixes. The InsightXPro row was incomplete, so its existing URL is preserved without guessing a bot or credential. URL formatting escapes were normalized without changing hosts or paths.

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
