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
