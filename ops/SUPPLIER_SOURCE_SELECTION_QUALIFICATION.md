# T094 supplier source selection qualification

## Execution evidence

Source `125985b7f84b6b2cd28a28ab165a9b394c12a2b4`, [CI 41625122419](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41625122419), passes **661 .NET tests: 136 unit, 413 integration, 112 contract**, **20 Python checks** and all five Linux Native AOT executables. Zero test failures/skips and zero build warnings/errors. Locked restore, formatting, secret scan, Compose, 53 bounded acceptance-witness mappings and evidence guards pass. Actual native execution prints the audited shared source selection, immutable paid binding and command replay witness.

## Implementation

`SupplierSourceSelectionStore.SelectAsync` authenticates the current independent administrator and appends the existing content-bound audit before product/source locks. It requires supplier fulfillment, exact product/source revisions, enabled state and same-product ownership. One transaction advances the shared product pointer/revision and queues existing restock evaluation. Invalid commands roll back the audit and work. Committed replay returns the original revision without restoring an old pointer. Changed-content replay and revoked administrators fail closed.

Selection applies across all brands without rewriting accepted request bindings, orders, money or coupon claims. Ready-item eligibility, direct provider refresh, binding at accepted payment and procurement charge ceilings retain their existing authorities. Mapping creation/enablement, protected account configuration and new HTTP endpoints are outside this checkpoint. No schema, dependency, host startup or live service change is made.

## Qualified bounded probes

| Probe | Result |
|---|---|
| Shared managed/native qualification | Three brands bind the selected source/variant/account; an earlier paid request keeps its original binding; replay never restores an old pointer |
| 100 concurrent command replays | One product revision, audit and restock job; altered source revision conflicts |
| Disabled/foreign/stale source, stale product, local policy | No selection audit, pointer change or queued work |
| Competing commands at one revision | One success and one conflict without an orphan audit |
| Revoked administrator replay | Authentication rejection before replay or mutation |
| Switch during direct refresh | Old evidence rejected without order/debit or observation mutation; selection occurs while transport is outside database locks |
| Unpaid invoice, switch and settlement | Binding at settlement uses fresh selected-source observation; later switching and payment replay preserve that binding |

Eleven integration cases and one shared native path pass. The competing command case uses current administrator serialization plus product revision fencing; it does not qualify every distinct-admin/payment/source lock interleaving. The invoice case exercises ordered switch-before-settlement, not an exhaustive simultaneous settlement matrix. All provider/payment transports are synthetic.

## Correction and authorization

The first source at `4723d30d7dbaf4e2c059bb4f07f9ba82e814817c`, CI `41624034244`, passed build/formatting and 660 of 661 tests. Its foreign-source fixture omitted the required product availability policy and failed during setup. The qualified source supplies `immediate` explicitly without changing application/schema constraints. The failed run is not qualification evidence.

Automatic approval review initially rejected the upload. The owner explicitly approved uploading the ten reviewed files to Farrokhzadeh/telegram-multi-bot-shop on main and running full CI. Upload succeeded; qualification uses the existing private repository. No deployment or live service permission follows.

## Remaining scope

The selection-only checkpoint left mapping administration, payment/source/race coverage and provider/account capability proof open. The later continuation below qualifies additional bounded implementation; T094 remains unchecked for its full remaining scope. Total remains **78/96 complete, 18 unchecked**. T096 protected historical compatibility and intended-environment requirements, T089 protected legacy activity reconciliation and all earlier provider/source/human/operational/release gates remain unchanged. Full SC-040 and human SC-008 remain planned/unexecuted; releaseReady=false remains.

SQL SHA-256 remains `52a1a3967339adc58248507a94baa90b42bf5079e8c2572fdc3b01da3979fad6`. Constitution 3.0.0, canonical 28-table/no-JSON baseline and frozen administrator HTTP contract are unchanged. The read-only reviewer checklist remains 92/126, SHA-256 `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`. Normal hosts remain fail closed. No real credentials/source rows, live services, staging/production mutation, cutover or deployment occurred. Independent AI remains separate. Local .NET/PostgreSQL/Docker remain unavailable; Actions supplies execution proof. Spec Kit prerequisites and all 20 local Python checks pass; no extension hooks exist. Verify full CI on exact latest main after recording this evidence.

## Mapping lifecycle/payment/account continuation — 2026-11-03

Source `4bdcc281cf40c0e168616c756f310a1e866086c3`, [CI 41632523436](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41632523436), passes **683 .NET tests: 136 unit, 435 integration, 112 contract**, **20 Python checks** and all five Native AOT executables, zero failures/skips and zero build warnings/errors. Formatting, locked restore, secret scan, Compose, 53 bounded acceptance-witness mappings and evidence guards pass. Earlier `03c73ff70d7e21f497b716f9e15310ead30779fb` / CI `41631841447` was superseded and cancelled when the account/history probes were added; it is not qualification evidence.

The store now implements immutable disabled mapping creation, bounded authenticated metadata pagination and revision-checked enable/disable. Enablement authenticates and reads the exact source under product/source locks, rolls back its temporary probe audit and releases all locks, then uses the existing configured-account CatalogStore boundary for a direct exact-product/variant ready-item inspection. Before writing, it reauthenticates and rechecks product/source revisions. A monotonic 30-second budget covers inspection through final writes. The new source revision, typed observation, audit and restock job commit atomically. Ready out-of-stock products may be configured without becoming purchasable. Disable retains the selected pointer; re-enablement cannot clear accepted review or enqueue procurement. Full-local checkout retains its supplier-refresh exemption. The ordinary catalog and enablement reuse one observation writer.

| Additional probe | Qualified bounded result |
|---|---|
| Create/replay/uniqueness/metadata | 100 creation replays produce one immutable disabled mapping/audit; nullable variants follow canonical uniqueness; bounded stable pages require a current administrator |
| Failed inspection | Ineligible, error, buyer-input, empty-stock buyer-input, missing cost, missing configured account and transport failure produce no enable audit/observation/work |
| Available/out-of-stock lifecycle | Exact configured account/product/variant inspection enables once; 100 replays skip transport even without a catalog; disable preserves selected identity; remote checkout blocks while full local coverage works |
| Revocation/product edits during transport | No database locks span inspection; final authorization/revision checks reject stale proof without retaining the provisional audit |
| Deferred commit failure | Source revision/enabled state, observation, audit and restock job roll back together; the original command succeeds after removing the owned fixture fault |
| Paid review after re-enable | Original source revision/cost/account and paid review remain; no automatic repeat procurement, wallet credit or release |
| Distinct-admin selection/payment races | One product revision has one selection winner; wallet/free/crypto/bank acceptance races preserve one immutable binding and original money/claim facts across 100 acceptance replays per route |
| Adapter/account replacement | A fresh same-account adapter reconciles the original intent by lookup without another submit; a different account is rejected before any provider call, preserving the original unknown obligation; restoring the original account permits lookup recovery |
| Referenced identity/history | Provider/product/variant/product-owner mutation and deletion are rejected after acceptance, preserving original pending liability and cost baseline |

These are 22 additional integration cases. The existing shared managed/native probe now performs actual mapping creation and verified enablement before three-brand selection and immutable paid-binding/replay checks. The adapter replacement cases use synthetic instances; they do not prove real credential-file rotation, deployed account continuity, provider recovery, or every possible lock schedule. No live provider is called. A different-account rejection retains the pre-existing unknown/review state; it does not claim a newly implemented account-replacement administration endpoint or automatic account-repair workflow.

Automatic approval review initially rejected the expanded payload. After the owner directed proceeding with the reviewed nine-file upload to the existing private repository on main and full CI, upload succeeded. The later account/history probes stay within that same source-test file. No deployment or real-service authorization follows.

T094 remains unchecked for exact provider observation/cost/account capability proof under E2/E3/E4, protected account/runtime composition and any remaining scoped matrix requirements. Total remains **78/96 complete, 18 unchecked**. Existing source/provider/legacy/human/operational/release gates are unchanged; do not infer whole-SC or release completion. SQL/checklist hashes, 28 typed/no-JSON tables, frozen administrator HTTP surface, constitution 3.0.0 and fail-closed normal hosts are unchanged. Local .NET/PostgreSQL/Docker remain unavailable; Actions supplies execution proof. Verify full CI on exact latest main after recording this evidence.

## Protected credential continuation — pending execution

The local T094 continuation implements a separate pinned, complete supplier credential manifest and immutable disposable snapshots through the existing secure Linux reader. Independent deployment/revision/service/account grants, strict fields, bounded read-only no-follow files, credential SHA-256 commitments, partial-load cleanup, metadata-only account projection and digest-bound rotation are implemented. Api/JobHost scopes are allowed; bots/account pools/inline credentials/unsafe or changed files fail closed. Protected CatalogStore composition checks deployment/service and shares one provider/account registry across brands. The old synthetic constructor remains available for qualification; normal hosts remain disabled.

Twenty new unit cases and three new integration cases are authored; existing managed/native source-selection and original-account-recovery probes now use generated read-only credential files and real snapshot reloads. These C# changes are unbuilt/unexecuted because local .NET/PostgreSQL/Docker are unavailable. All 20 Python checks and static evidence/whitespace checks pass. Prior 683-test source/main CI does not qualify this continuation. T094 remains unchecked for execution and external provider/account/runtime gates; total remains 78/96 complete. No live credential/source data or service was accessed. The next eligible task prepared locally is T092's complete-set partial-acquisition review approval; it has a separate task commit and evidence scope.

## Expanded payload — upload authorized, execution pending

Automatic approval review initially rejected the expanded payload. The owner has now explicitly approved uploading the listed 22 files to Farrokhzadeh/telegram-multi-bot-shop on main and running full CI. Upload and execution are proceeding; no passing execution is claimed yet. The previous qualified main is 19731699ac2d35b1dad2ac55d17e5091058b5f3c, CI 41634324671. Local task commits are 5220db4 (T094 credentials), 1641c69 (T092 complete review) and 17a7de1 (T092 audit/manifest lock regression). Full C# and Native AOT execution remain pending; the 683-test baseline does not qualify this work. The new local coverage comprises 20 unit and 11 integration cases, with generated-file/native probes. All earlier task/evidence gates remain open.

The explicitly approved upload covers these 22 files on main, with full CI and fixes grouped by task:

- ops/SUPPLIER_QUALIFICATION.md
- ops/SUPPLIER_SOURCE_SELECTION_QUALIFICATION.md
- specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md
- specs/001-multibrand-redesign/contracts/backend-contracts.md
- specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md
- specs/001-multibrand-redesign/tasks.md
- src/Platform.Persistence/CatalogStore.cs
- src/Platform.Persistence/DeliveryStore.cs
- src/Platform.Persistence/SupplierRequestStore.cs
- src/Platform.Security/SecretFiles.cs
- src/Platform.Security/SupplierCredentialFiles.cs
- tests/Platform.AotSmoke/Platform.AotSmoke.csproj
- tests/Platform.AotSmoke/Program.cs
- tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj
- tests/Platform.IntegrationTests/SupplierPartialApprovalTests.cs
- tests/Platform.IntegrationTests/SupplierSourceAdministrationTests.cs
- tests/Platform.IntegrationTests/SupplierSourceRaceTests.cs
- tests/Platform.UnitTests/Platform.UnitTests.csproj
- tests/Platform.UnitTests/SupplierCredentialTests.cs
- tests/Shared/SupplierCredentialQualification.cs
- tests/Shared/SupplierPartialQualification.cs
- tests/Shared/SupplierSourceSelectionQualification.cs
