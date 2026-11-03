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

## Protected credential continuation — qualified 2026-11-03

Source `876dae66ade5a9b41eca02e076b37631a9364b1f`, [CI 41645042624](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41645042624), passes **714 .NET tests (156 unit, 446 integration, 112 contract)**, **20 Python checks** and all five Native AOT executables, zero failures/skips and zero build warnings/errors. Locked restore, formatting, secret scan, Compose, 53 bounded acceptance-witness mappings and evidence guards pass. The approved batch adds 20 unit and 11 integration cases across T094/T092. T094's task-scoped commit is `de97f84b801db06fcf2e58216a12c7135dfbfefe`; T092's is `f5666d0ee27c64c8cb803e3acaf215e73b822dd9`.

`SupplierCredentialFiles` uses the existing secure Linux reader for a separate complete manifest, independently granted deployment, service, revision and provider/account set. Api/JobHost scopes only, strict fields, nonempty bounded read-only no-follow files, SHA-256 credential commitments, duplicate-file/account rejection and all-or-nothing loading fail closed. Credential bytes stay private in disposable immutable snapshots; trusted synchronous callbacks check the exact provider/account. Dispose clears bytes; failed loads clear material. Metadata arrays are copies without credentials. Credential revisions are independent of commerce keyring revisions; protected catalog composition requires matching deployment/service and derives one shared account per provider across brands. Normal hosts remain disabled.

Twenty unit cases cover digest-bound generated-file rotation, old snapshot immutability, disposal, exact account scope, complete grants, forbidden bot/account pools, strict malformed/missing/duplicate/inline fields, identity mismatches, unsafe paths and writable/symlink/empty/oversized/missing/directory/changed files. Three new integration cases qualify protected catalog composition and reject wrong deployment/service before provider transport. Existing account-recovery cases now load actual generated read-only revision-7 and revision-8 credential files into their adapters; same-account recovery uses lookup without resubmission, while a different account is blocked before a call. Actual native execution loads/rotates credentials and uses the protected registry for three-brand mapping/enablement/selection.

This proves target code and generated file behavior, not actual provider identity, real credential custody, live key rotation, remote recovery guarantees or deployed service isolation. Provider capability and runtime evidence remain E2/E4 gates. T094 stays unchecked for its complete remaining scope; total remains 78/96 complete. No provider SDK, account pool, database table/column, live adapter or normal-host activation was introduced.

Automatic approval review rejected earlier attempts to upload the expanded private payload. The owner then explicitly approved the listed 22 files to Farrokhzadeh/telegram-multi-bot-shop on main with full CI. That upload succeeded; exact remote file hashes and scope match the local checkpoint. The successful run above qualifies the actual uploaded source. No deployment or real-service permission follows. See the supplier qualification for T092's separate bounded result. Full exact-head CI remains required after recording this evidence.
