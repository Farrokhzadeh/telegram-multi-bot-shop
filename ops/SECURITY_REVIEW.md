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

## Host inventory and database grant follow-up — 2026-11-15

The host inventory found no containers or network for the planned staging project, no running scoped staging rehearsal actors and no matching staging root in the inspected locations. The deployed identities, routes and destinations remain unverified.

Two additional tests passed on authenticated disposable PostgreSQL 18. The restricted maintenance identity could delete eligible expired activity through the established cleanup path. PostgreSQL denied reads from wallet, order, payment and audit tables, private activity fields, unrelated writes and direct deletion of recent activity. The domain-fence test preserved the commerce relations and blocked cleanup of unproven activity. Container inspection confirmed that the runner shared only the disposable database's network namespace, the database had networking disabled, neither actor published ports, and both stopped. These results qualify the tested grant behavior. The actual staging function owner and grants still require comparison with the intended deployment.

## Legacy CI/CD comparison — 2026-11-15

The [legacy branch workflow](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/main/.github/workflows/deploy.yml) tests the pushed commit, builds revision-labeled images and deploys a checksummed release. The legacy `staging` branch deploys one Django instance. The legacy `main` branch deploys the three production instances. The owner-server inventory confirmed that the running legacy staging instance uses a distinct bot credential, PostgreSQL database and role, application keys, data mount and loopback web binding. Its application containers join the fleet network and its database uses the fleet PostgreSQL engine, as described in the [legacy fleet guide](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/main/docs/FLEET_DEPLOYMENT.md).

The redesign staging plan requires a dedicated PostgreSQL process and network and three test brand bindings. The legacy deployment provides a reference for artifact identity checks and independent instance controls. The redesign's [normal host startup](../src/Platform.Core/HostBootstrap.cs) exits with configuration required. Its loopback API qualification and scoped private-history probe provide evidence only for their own entry points. Operational qualification needs a reviewed staging launcher and artifact that bind the redesign services to staging-only data, fake transports, private ingress and test recipients. Keep T075 open until those bindings pass the intended-environment checks.

## Disposable staging infrastructure checkpoint — 2026-11-15

A fresh owner-server probe ran PostgreSQL 18.6 in a dedicated container with an internal isolated bridge, a dedicated data volume and zero published ports. The canonical migration matched the source blob on main and created all 28 tables under the restricted migration owner. The database contained three active synthetic brand identities. A restricted network client authenticated with its own credential and could read the three brand codes. PostgreSQL denied an incorrect password, credential ciphertext reads, order reads, schema creation and access to the maintenance function. The maintenance grant and both security-definer function owners and search paths were checked.

Five nonroot container identities exercised the read-only mounts with generated test keys and synthetic receipt files. Each bot could read its own brand key and receipt. The other brand keys were absent from each bot's mounts and the other receipt files were unreadable. Only the job identity received the inventory key. These were container permission checks. The disposable containers, bridge and PostgreSQL volume were removed after execution, and the pre-existing running container set matched before and after.

The existing [staging runner](../tests/Shared/CutoverStageRuntime.cs) explicitly enables one active brand. Its [prior rehearsal](evidence/t068-stage-delivery-interruption-2026-11-15.json) provides single-brand process evidence. T075 remains open for the three-brand application run with scoped callback routes, authenticated admin ingress, and verified test-only recipient routing. Three real Telegram bots are unnecessary for this simulated run.

## Next qualification step

Review the protected operator identity manifest against the intended staging deployment before any live transport or cutover. Check the following bindings without publishing raw tokens, keys, passwords or identities.

| Binding | What to verify in the intended environment |
|---|---|
| Project, network and data | Container project and network identity, database server and role grants, migration owner, and separate data, asset and backup mounts. |
| Bot and recipient scope | Dedicated test bot identity for each configured brand, test staff destination, and tester and admin allowlists. |
| Secrets and ingress | Separate secret references and file permissions, admin origin, callback route and certificate identity, and rejection of production bindings. |
| Media and release artifact | Receipt and fulfillment media roots, access controls and the reviewed artifact identity. |

The current evidence supports proceeding with an isolated staging setup when separately authorized. T075 remains open until its intended-environment checks pass.
