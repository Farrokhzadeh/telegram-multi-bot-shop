# T075 staging security and isolation review

## Offline checkpoint — 2026-11-15

**T075 remains unchecked. 85/96 tasks are complete.** This checkpoint covers the configuration template and tested application boundaries. The [staging plan](../specs/001-multibrand-redesign/staging-plan.md) and [release identity contract](RELEASE_GATES.md) define the operational acceptance requirements.

| Boundary | Verified offline | Operational evidence still needed |
|---|---|---|
| Data and brand isolation | `BrandIsolationTests` verified independent memberships for the same Telegram identity and rejected foreign orders, listings and wrong-member flow targets. | Verify the intended staging database, roles, volumes and media paths are separate from their production counterparts. |
| Secrets and leakage | `SecretFilesTests` and `SupplierCredentialTests` rejected wrong deployment, service and credential scope, unsafe paths, symlinks and writable files. `CipherTests` exercised encryption and scope binding. The pinned Gitleaks scan reported zero findings across 410 build inputs and the Compose template. | Verify actual staging-only secret ownership, modes, least-privilege grants and runtime log handling. |
| Bot and callback routing | `BrandContextTests` and `CutoverTelegramRouterTests` exercised exact binding, rejection of untrusted callbacks and foreign memberships, and replay under synthetic identities. | Verify the dedicated bot and callback routes, their ingress credentials and the absence of production fallback in the intended environment. |
| Protected media and recipients | `LocalDeliveryTests` denied a wrong-brand manifest read and prevented wrong-target delivery completion and wrong-context anchor updates. Incomplete fulfillment exposed no manifest. | Verify actual receipt-media custody and access, staff destinations, and customer/tester allowlists. |
| Administrator authorization | `AdminAuthTests`, `AdminAuthStoreTests`, `AdministrationHttpTests`, `BrandAdministrationTests` and `AdminCatalogAndResolutionTests` exercised independent authentication, session and command scope, and audited writes with synthetic identities. | Verify actual staging admin ingress, service roles, and function owner/grant boundaries against the staging deployment. |

Fresh execution passed **61 unit and 42 integration cases** on the owner server. The integration runner used authenticated disposable PostgreSQL 18 with distinct test credentials, and the artifact checks and documentation-only change review bound the cases to the current implementation. TRX digests and case outcomes were rechecked. All three test containers stopped.

The [configuration-only Compose template](compose/backend.compose.yaml) rendered with synthetic values. The render showed five distinct service UIDs, 15 distinct secret-file references, network disabled and no published ports. Each bot received its own brand key; the inventory key was scoped to the job service. The expected staging project label and private network name had no match during the host inventory lookup.

## Next qualification step

Review an operator-provided staging identity manifest for the database, service secret grants, three dedicated test bots, callback endpoints, staff destinations and media roots. Check the declared separation and deny cases in the intended environment before any live transport or cutover. This checkpoint used synthetic data and transports.
