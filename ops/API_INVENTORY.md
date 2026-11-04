# Owner-supplied API inventory

Recorded 2026-11-04 from the owner's complete API list. This is the durable project reference for current and future integrations. Consult it before asking the owner to resend URLs. Preserve all entries even when an integration is deferred. Labels identify the supplied sites; exact provider codes, ownership and compatibility must be verified before implementation.

## Complete list

| Reference | API / service | Owner-supplied URL | URL type | Owner-supplied authentication |
|---|---|---|---|---|
| `akunding` | Akunding | [https://akunding.shop/api/docs](https://akunding.shop/api/docs) | Documentation | Not supplied |
| `prodseller` | ProdSeller | [https://prodseller.com/api-docs/](https://prodseller.com/api-docs/) | Documentation | Not supplied |
| `canboso` | Canboso | [https://canboso.com/api/swagger](https://canboso.com/api/swagger) | Swagger documentation | Not supplied |
| `roboticvn` | RoboticVN | [https://api.roboticvn.com/api/v2/docs](https://api.roboticvn.com/api/v2/docs) | Documentation | Not supplied |
| `aivaulthub` | AIVaultHub | [https://reseller.aivaulthub.store](https://reseller.aivaulthub.store) | Base URL | `X-API-Key: <your key>` |
| `rapidx` | RapidX | [https://pay.rapidx.me/docs](https://pay.rapidx.me/docs) | Documentation | Not supplied |
| `aitoolifystudio` | AIToolifyStudio | [https://bot.aitoolifystudio.io/api/reseller/v1](https://bot.aitoolifystudio.io/api/reseller/v1) | API URL | Not supplied |
| `lahastore` | LahaStore | [https://lahastore.up.railway.app/api/v1/docs](https://lahastore.up.railway.app/api/v1/docs) | Documentation | Not supplied |
| `vokhong` | VoKhong | [https://vokhong.xyz/api](https://vokhong.xyz/api) | API/documentation URL | Not supplied |
| `qamify` | Qamify | [https://api.qamify.site/docs](https://api.qamify.site/docs) | Documentation | Not supplied |
| `nastele` | Nastele | [https://nastele.online/api/partner/docs](https://nastele.online/api/partner/docs) | Partner documentation | Not supplied |
| `testflighty` | Testflighty MAX | [https://api-tgbot.testflighty.com/t/MAX/api-guide](https://api-tgbot.testflighty.com/t/MAX/api-guide) | API guide | Not supplied |
| `silvex-ai` | Silvex AI | [https://silvex-ai.com/api/public/reseller](https://silvex-ai.com/api/public/reseller) | API URL | Not supplied |
| `insightxpro` | InsightXPro | [https://api.insightxpro.store/docs](https://api.insightxpro.store/docs) | Documentation | Not supplied |

The AIVaultHub address is explicitly a base URL. Its authentication header name is `X-API-Key`; `<your key>` is a placeholder, not a stored credential. Other API roots, headers and routes must come from verified documentation; do not infer them from a documentation hostname or from the removed adapters. URL formatting escapes from the owner's message were normalized without changing hosts or paths.

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
