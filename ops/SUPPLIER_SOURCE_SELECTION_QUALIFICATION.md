# T094 supplier source selection qualification

## Current T094 checkpoint — 2026-11-15

**T094 remains unchecked; 84/96 complete, 12 unchecked.** This is final offline acceptance-matrix verification, not real-provider qualification. [Generated review](evidence/t094-offline-selection-qualification-2026-11-15.json) · [T093 actual-source findings and remaining gate](T093_QUALIFICATION.md).

The current implementation was mapped before adding code or repeating tests. No production code, SQL, dependency or physical schema change was needed. Current implementation authority is `SupplierSourceSelectionStore`, `CatalogStore`, the accepted-payment stores and `SupplierCredentialFiles`; the older task's proposed `SupplierSourceSelection.cs` path does not require a parallel implementation.

| Task requirement | Current implementation and bounded evidence |
|---|---|
| Shared audited source selection, variants and uniqueness | Reused 36 selection/administration/race cases from the immediately preceding T093 checkpoint. Same-product enabled selection, null-aware variant identity, actor/reason/revision audit, authenticated replay and referenced-history protection remain intact. Three brands are included only for shared-selection/account isolation. |
| Accepted payment and zero-due binding | `SupplierSourceRaceTests.EveryAcceptanceRouteSerializesSelectionAndKeepsOneImmutableBindingAcrossHundredReplays` covers wallet, free, crypto and bank paths. `UnpaidInvoiceBindsSelectedSourceAtSettlementAndNeverChangesOnReplay` covers unpaid switches. Fresh mixed-settlement cases preserve accepted money and selected evidence under disabled/switched sources. |
| Whole coupon claim and mixed quantities | Fresh `PreparedCheckoutTests` verify zero-due acceptance, one full held claim, immutable positive remote remainder, exact wallet debit or no money, replay without refresh/rebind, no invoice/fabricated payment and full-local zero-transport/zero-request behavior. |
| Eligibility and post-payment review, FR-068 / SC-037 | Fresh `SupplierEligibilityTests` reject unready/excluded/unknown requirements before orders, invoices, debits, claims or work, including fully discounted checkout. Existing enablement/re-enablement and worker evidence preserves paid review; real mapped-product eligibility remains unverified. |
| Cost baseline, FR-074 | Immutable accepted cost/source/account snapshots remain covered by current source/payment cases and reused 16 canonical schema cases. Existing T090 supplier recovery evidence covers inclusive 10%, non-rebasing review and owner-approved option A; it does not prove an actual provider's current currency/cost. |
| Availability/recovery, FR-075 / SC-044 | Fresh `CatalogQuoteTests` cover published outage visibility across brands, hidden-listing protection, normal recovery without visibility/financial writes, protected old orders and local-stock exemption. |
| Direct refresh, FR-077 / SC-046 | Fresh prepared/catalog/eligibility cases cover failed-refresh zero mutation, exact uncovered quantity, source/revision change during unlocked transport and immutable duplicate creation. Fresh unpaid-dispatch evidence confirms that invoice creation does not bind or allocate stock. Existing synthetic adapter parsing evidence is not live wire proof. |
| Local-first exemption, FR-078 / SC-047 | Fresh mixed checkout and settlement recompute paid local allocation and bind only the positive supplier remainder. Full local coverage bypasses supplier transport. Existing T090 recovery evidence retains original split/source history and complete-order-only release. |
| One protected configured account and rotation | Fresh 20 credential-file cases verify bounded read-only no-follow files, complete independent grants, pinned snapshot rotation/disposal, duplicate/account-pool/bot rejection and exact account scope. Reused account-replacement cases use generated revisioned files: same-account original lookup, different-account blocking before transport, no resubmission. These do not prove real provider identity, real custody, live key rotation or deployed service isolation. |

Fresh results: 12 baseline, 20 credential-file and 42 PostgreSQL checkout/eligibility/catalog cases. Reused matching results: 36 selection/account/race and 16 canonical schema cases. These scoped counts are not a new whole-suite or whole-success-criterion claim. Current 410 compile inputs, immutable integration/unit artifacts and exact passing case sets/TRX hashes are verified. No rebuild or new Native AOT publication is claimed.

The independent final checker revalidates the prior T093 complete private row streams against frozen keyed commitments, source schemas and T066 execution-sealed plans; this is reuse of frozen evidence, not a new live observation. All three source ordinals are included only for final reconciliation. T093 verified one saved Akunding connection/mapping/expired availability snapshot, empty saved account identity and no supplier-cost/currency columns. No operational mapping, selected pointer, credential or observation is imported or activated. Do not infer cost from retail prices, supplier identity from a local label, or eligibility from a historical enabled flag.

All 274 preexisting container configurations/images/mounts/states and original source bytes remain unchanged; the original source remains stopped and all three new actors are stopped. Focused targets are authenticated synthetic PostgreSQL18 with network isolation and bounded runtime resources. Sensitive records, credentials, identifiers, amounts, keys and receipts stay on the owner server.

Private checkpoint: `/var/lib/teleg-cli/t094-selection-20261115/`; checkers `unit.py`, `run-tests.py`, `review.py`; ledger `unified-progress-20261115.md`. Source provenance and the proposed exact-account/product read plan remain under `/var/lib/teleg-cli/t093-source-20261115/`.

**Remaining closure gate:** authoritative current account/product/native-cost/currency/eligibility evidence under T093/E2 and the applicable protected account/runtime proof. The earlier explicit T094 provider/runtime gate is retained. Passing generated credentials and synthetic transport responses cannot close it. AGENTS.md requires separate authorization for real supplier calls. The prepared plan permits only bounded read-only existing-account/exact-product observations after official endpoint verification; no purchase, rotation, mapping enablement, deployment or production bot/database access is included. Successful reads qualify only what was observed; further missing evidence remains blocked. Other providers retain separate gates.

Normal hosts remain fail closed. No CI or real supplier/payment/Telegram call, financial settlement, production operation or release is claimed. The reviewer checklist and constitution remain unchanged. Earlier dated evidence below retains its original scope and status.

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
