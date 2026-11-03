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

T094 remains unchecked. Mapping administration, remaining payment/source/race coverage and provider/account capability proof stay open. Total remains **78/96 complete, 18 unchecked**. T096 protected historical compatibility and intended-environment requirements, T089 protected legacy activity reconciliation and all earlier provider/source/human/operational/release gates remain unchanged. Full SC-040 and human SC-008 remain planned/unexecuted; releaseReady=false remains.

SQL SHA-256 remains `52a1a3967339adc58248507a94baa90b42bf5079e8c2572fdc3b01da3979fad6`. Constitution 3.0.0, canonical 28-table/no-JSON baseline and frozen administrator HTTP contract are unchanged. The read-only reviewer checklist remains 92/126, SHA-256 `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`. Normal hosts remain fail closed. No real credentials/source rows, live services, staging/production mutation, cutover or deployment occurred. Independent AI remains separate. Local .NET/PostgreSQL/Docker remain unavailable; Actions supplies execution proof. Spec Kit prerequisites and all 20 local Python checks pass; no extension hooks exist. Verify full CI on exact latest main after recording this evidence.

## Mapping lifecycle continuation — local, pending qualification

The owner requested finishing the remaining work after the selection checkpoint. The local continuation implements disabled immutable mapping creation, bounded authenticated metadata pagination and revision-checked enable/disable commands. Enablement uses the existing configured-account CatalogStore boundary for an exact-product/variant ready-item inspection outside database locks, then reauthenticates and rechecks product/source revisions. A monotonic 30-second budget covers inspection through final writes. Enablement, its new-revision typed observation, audit and restock work commit atomically. A ready but out-of-stock mapping may be configured without becoming purchasable. Disable retains the selected identity. Re-enablement never clears accepted review or queues procurement. Full-local checkout retains its refresh exemption.

The existing catalog observation writer is shared by refresh and enablement. No schema, dependency, public HTTP endpoint, credential storage, account pool or host activation is introduced. Protected provider/account identity and live capability proof remain independently gated. T094 stays unchecked and the total remains 78/96 complete.

Nineteen new integration cases cover creation replay and null-aware variant uniqueness, bounded metadata/authentication, seven failed-inspection categories, available/out-of-stock enablement, skipped-transport replay, disabled listing/remote rejection/full-local bypass, revocation and product-change races during transport, deferred commit rollback/retry, and preservation of a blocked paid obligation after re-enablement. Separate administrator identities exercise competing selections and selection against wallet/free/crypto/bank acceptance; each route then replays acceptance 100 times with one retained binding and financial effect. The existing managed/native shared probe now creates and enables its mapping through the real administration methods.

These C# changes have not been built or executed: local .NET/PostgreSQL/Docker remain unavailable, and automatic approval review initially rejected upload of the expanded payload beyond the earlier ten approved files. The owner subsequently directed proceeding with the reviewed nine-file upload to the existing repository on main and full CI. The earlier 661-test qualification does not qualify this continuation. All 20 local Python checks, Spec Kit prerequisites, bounded evidence catalogue validation and whitespace checks pass. Full checkpoint CI and exact latest-main verification remain required before claiming qualification.

The expanded upload contains exactly these nine paths:

- src/Platform.Persistence/CatalogStore.cs
- src/Platform.Persistence/SupplierSourceSelectionStore.cs
- tests/Shared/SupplierSourceSelectionQualification.cs
- tests/Platform.IntegrationTests/SupplierSourceAdministrationTests.cs
- tests/Platform.IntegrationTests/SupplierSourceRaceTests.cs
- specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md
- specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md
- specs/001-multibrand-redesign/tasks.md
- ops/SUPPLIER_SOURCE_SELECTION_QUALIFICATION.md
