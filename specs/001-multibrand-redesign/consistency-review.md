# Consistency review — 2026-10-31

**Review input:** owner-authorized documentation corrections based on main at 114958a3a7d6117dbc2b536a18cfdec2e2452151, followed by constitution 3.0.0 and the corresponding specification/plan/task updates. **Scope:** governance and specification documentation only.
**Outcome:** read-only Spec Kit alignment reanalysis passed: all six findings resolved in documentation, with zero remaining critical/high/medium findings in this rerun. E1 is closed for constitutional alignment only. No application/runtime/provider/migration/release qualification or implementation authorization is claimed.

[Canonical schema](schema-contract.md) · [traceability](requirements-traceability.md) · [active constitution](../../.specify/memory/constitution.md) · [historical 2.0.0 ratification](constitution-ratification.md).

## Six-finding remediation

IDs in this table identify the latest six-finding review; the older review at the end has its own historical IDs.

| ID | Severity in input | Correction | Current disposition |
|---|---|---|---|
| C1 | CRITICAL | Principle IV preserves exactly-once business effects, prefers validated saved-order-message edits, suppresses known success and pauses uncertain new credential sends for explicit recovery. Complete manifests and protected paid obligations remain. | RESOLVED: 3.0.0 applied and aligned; E1 governance-only closure |
| I1 | HIGH | US3, plan and payment examples now scope automatic shortage credit/full held-use return to live immediate local-only goods using the frozen eligible USD quote. Supplier/mixed review, prepaid issuance and paid local-preorder FIFO remain distinct. | RESOLVED: verified against the approved canonical rules |
| I2 | HIGH | Supplier refresh checks only the uncovered remainder; the 5 ordered / 3 local / 2 supplier example is retained from published fix 3, commit 114958a. | RESOLVED: verified against the approved canonical rules |
| I3 | MEDIUM | Current summaries reflect active 3.0.0 and applied amendments; dated/pinned approval history is preserved and clearly separated. | RESOLVED: verified against the approved canonical rules |
| I4 | MEDIUM | Prepaid issuance and late crypto disposition reference approved FR-055/057, retaining E2/E3 evidence rather than reopening policy. | RESOLVED: verified against the approved canonical rules |
| I5 | MEDIUM | Tasks and current contracts use inherit/override/clear, payment_method, quote_currency, XOR payment origins and historical-only legacy_kind. | RESOLVED: verified against the approved canonical rules |

## Coverage and verification

| Check | Result |
|---|---|
| Functional requirements | 84 unique sequential IDs; 84/84 with planned owners (100%) |
| Success criteria | 53 unique sequential IDs; 53/53 test/protocol owners, including human protocol T079 for SC-008 |
| Tasks | 96 unique sequential IDs; T001 alone complete, 95 unchecked |
| Approved tables | 28 canonical sections, matching the numbered inventory |
| Unmapped tasks / invalid owner references | 0 / 0 |
| New unanswered business ambiguities / actionable duplicates | 0 / 0 |
| Remaining findings in this rerun | 0 critical, 0 high, 0 medium |
| Local Markdown file links in changed documents | 86 checked; zero missing targets |
| Scope and whitespace | Markdown-only corrections; git diff --check passed |

The Bash prerequisite command with --json --require-spec --require-tasks --include-tasks passed. The constitution template resolver passed before the separate governance update. Semantic checks covered all seven principles, six-finding corrections, requirement/story/task alignment and planned coverage. The read-only analysis changed no files; this status report records the completed result afterward. No extension hooks are registered. PostgreSQL, encryption, Native AOT, provider, Telegram and application acceptance tests have not run and are not claimed. The legacy review below remains dated history.

## Evidence gates, owners and required proof

| Gate | Required evidence | Planned owners | Stage blocked |
|---|---|---|---|
| E1 Governance | CLOSED for governance alignment: active 3.0.0, owner authorization and risk/impact records, plus successful 2026-10-31 alignment reanalysis | Constitution amendment records; this review; T007 operational follow-up remains unchecked | Constitutional blocker resolved; no implementation, runtime or release permission is inferred |
| E2 Provider contracts | Exact product/currency/stock schemas; callback/evidence time and quote satisfaction; remote key scope and safe retry; stable item/export identity; recoverable credentials and enforceable charge ceiling per provider | T006/T029/T037/T053/T055–060/T090–094 | Enablement of each real adapter and unattended paid procurement, not writing an offline probe |
| E3 Legacy compatibility | Protected source schema/cardinality, real USD liabilities/precision/denomination, prepaid ownership/expiry exceptions, historical aliases/partial deliveries/date promises, archive-only refund preservation and golden rounding/topup behavior | T006/T013/T029/T061–070 | Lossy field removal/import and final migration-compatibility freeze |
| E4 Runtime qualification | Selected PostgreSQL version and real constraint/lock/retention tests, exact monetary mapping, ciphertext roundtrip, published Linux Native AOT/dependency smoke and failure probes | T004–005/T008–017/T027/T034–036/T042/T052/T058/T074–078/T085–096 as mapped | Claims that the blueprint works; probes are first implementation work only after separate authorization |
| E5 Operational identity/privacy | Real bank-holder/provider-hosted merchant disclosure acceptance, verified staff identity/public contacts, trusted private group membership/routing and protected secret access | T007/T016/T038/T040/T044/T049/T051/T075/T080 | Live payment/customer/staff messaging activation |
| E6 Release/cutover | Isolated restore and reconciliation, stopped old writers, per-brand ownership, retained keys/images, rollback for accepted external effects and explicit separate operator release decision | T067–070/T074–081 | Production cutover only |

These are evidence gaps, not reopened questions about supplier selection, mixed fulfillment, refunds, coupon return, delays or the user's other approved business policies. A genuine incompatible source/provider fact must be brought back with concrete evidence, not guessed away.


## Authorization and next work

Governance/documentation remediation does not authorize application implementation or live operations. With reanalysis passed, the next work still needs separate implementation authorization for offline legacy/PostgreSQL/encryption/Native AOT qualification. E2–E6 retain their stage-specific proof and separate live/cutover controls. T007 remains unfinished because it also owns future operational artifacts.

## Historical review archive

The following 2026-10-30 report is historical evidence. Its pending/unchanged governance statements and old C1 disposition describe that input snapshot; the current sections above supersede them. No historical review result is represented as current runtime evidence.

## Historical: Consistency review — 2026-10-30

**Review input:** main at 350a4d90ebfe6e4082d94820910806bb96c2484e. **Scope:** owner-approved documentation/schema review and correction only.
**Outcome:** forward documentation conflicts corrected; canonical schema selected and complete planned traceability recorded. **Do not call the design fully ratified or a runtime/release qualified:** E1–E6 remain at their stated stages.

[Canonical schema](schema-contract.md) · [traceability](requirements-traceability.md) · [current inventory](database-table-review.md) · [constitution proposal](constitution-alignment-proposal.md).

**Governance status update, 2026-10-30:** the owner completed step 1 by ratifying the exact reviewed amendment; see [constitution-ratification.md](constitution-ratification.md). Step 2 (active-file application and alignment review) is pending. E1 remains open, the active constitution remains 1.1.0, and the original review below is not a new completed alignment run.

### Historical: Findings

Locations identify the review input; corrected counterparts are in this change. FIXED DESIGN means a specification choice, not a tested database/provider implementation.

| ID | Category | Severity | Input location | Finding | Disposition |
|---|---|---|---|---|---|
| C1 | Constitution | CRITICAL | constitution principles II/III; plan Constitution Check | Imported fleet/schema mandates conflict with approved shared target. | OPEN E1: owner ratification recorded; step 2 application/alignment pending; active constitution unchanged. |
| I1 | Routing | HIGH | spec FR-019/052; backend WalletCheckout/CryptoSettlement; T033/T039 | Immediate local-shortage compensation/instant allocation could swallow accepted mixed/supplier paths. | FIXED: explicit order-kind/route dispatcher and local-only compensation scope. |
| I2 | Availability | HIGH | spec SC-044/046, FR-075/077; catalog tasks | Supplier-only unavailable/read rules contradicted local-only outage exemption. | FIXED: selected local coverage first; fresh remote read only for positive remainder. |
| I3 | Coupon | HIGH | spec FR-056 vs FR-053/058; claim contract | New-cap/expiry/current-eligibility revalidation at fulfillment could invalidate protected old holds. | FIXED: creation validates current capacity; fulfillment validates the pinned immutable granting revision/held claim and whole manifest. |
| I4 | Source lifecycle | HIGH | source selection proposal; schema pointer guards | An always-enabled pointer invariant could prevent disabling the selected source. | FIXED: same-product pointer remains; enabled required on selection/new eligibility, later disable allowed. |
| I5 | Typed schema | HIGH | inventory/model added-field placeholders | Preorder, hold, observation, cost/account, review and credit fields lacked a selected contract. | FIXED DESIGN: one canonical 28-table type/key/guard contract; no executed SQL claim. |
| I6 | Time/state | HIGH | orders paid_at/payment_confirmed_at; zero-due and cancelled cases | Competing accepted clocks and mixed financial/business status risked false FIFO or erased paid history. | FIXED: one accepted clock, not_required and typed fulfillment/resolution distinctions. |
| I7 | Prepaid routing | HIGH | FR-055/T030/T033 vs generic per-unit goods rule | N-use source sale could create fake goods/procurement or be treated as coupon redemption. | FIXED: prepaid_units versus product_purchase discriminator and one paid-source coupon. |
| I8 | Safe retry | HIGH | supplier request/attempt fields; T056/T059/T091 | A local request key, provider key, lease retry and explicit admin retry were not structurally distinct. | FIXED DESIGN: parent submit, per-attempt key/cap and exact admin authorization/revision; E2 remains. |
| I9 | Staff delivery | HIGH | old private-DM delivery definition; FR-082 | New private staff group had no recipient/purpose XOR or trusted destination branch. | FIXED: separate typed staff recipient; no customer-purpose group content or group-text authority. |
| I10 | Bonus storage | HIGH | FR-072; inventory/fulfillments | Admin bonuses had no durable typed home or disjoint identity guard within 28 tables. | FIXED DESIGN: admin_held inventory and serialised cross-table capture; E2/E4 proof still required. |
| I11 | Manifest | HIGH | complete-order release and delivery unit/part relation | Whole-manifest membership had no exact selected generation keys. | FIXED DESIGN: immutable order/unit generation, encrypted manifest and unique stable parts. |
| I12 | Retention | HIGH | FR-071/084; subscription/delivery/job FKs | Closed-consent deletion could break live references or cascade into mixed-domain history. | FIXED DESIGN: separate clocks, active protection and coordinated terminal restock-only cleanup. |
| I13 | Money provenance | HIGH | payments kind/currency; ledger source; legacy aliases | Purpose/method/quote/asset and generic posting source remained ambiguous. | FIXED DESIGN: XOR origin, explicit quote currency, typed posting links and retained legacy evidence; E3 remains. |
| I14 | Schema sequence | HIGH | T008/T015 vs later schema tasks; T062 | Early FKs referenced future tables, while history/checkpoint wording could add unapproved tables. | FIXED: define 28-table baseline before cyclic FKs; downstream qualification; external operational manifests. |
| I15 | FIFO | HIGH | oldest eligible head, hold and concurrent worker wording | Filtering holds or skipping a locked head could let later consumers bypass the queue. | FIXED DESIGN: absolute active paid head includes holds under product serialization; no business-head SKIP LOCKED. |
| I16 | Lock order | MEDIUM | checkout/coupon owner edit/parent FK guards | Different operations could acquire related parents in inverse order. | FIXED DESIGN: deterministic affected member/product/coupon/wallet/order/unit order and revision recheck. |
| I17 | Traceability | MEDIUM | tasks and SC owners | Recent approvals were appended without a complete requirement/acceptance owner matrix. | FIXED: 84/84 FR and 53/53 SC planned mappings; no task unaccounted for. |
| I18 | Active versus history | MEDIUM | table-review/model/normalization; dated appendices | Earlier alternatives, stale counts and field proposals looked concurrently normative. | FIXED: concise current model/inventory/audit, canonical precedence and separately labelled chronology. |
| I19 | Evidence labels | MEDIUM | quickstart/checklist/future artifact paths | Planned checks/paths and completed setup could be confused with runtime or script execution. | FIXED: setup only remains checked, future paths labelled, PowerShell/runtime limits explicit. |

### Historical: Coverage and validation

| Check | Result |
|---|---|
| Functional IDs | 84 unique sequential FR-001–084, unchanged |
| Success-criterion IDs | 53 unique sequential SC-001–053, unchanged |
| Table inventory | Exactly 28 approved relations; canonical schema sections match |
| Backlog | 96 unique sequential tasks; only T001 setup checked, 95 unchecked |
| Planned FR coverage | 84/84 have named task and acceptance owners; 100% planned, not tested |
| Planned SC coverage | 53/53 have test/protocol owners; SC-008 is human acceptance sampling |
| Task accountability | No task unaccounted for across FR/SC/architecture-evidence controls |
| Relative links | PASS: 70 relative Markdown links resolve against the complete source tree plus new review artifacts; future code paths in backticks are not claimed files |
| Scope | Markdown only; no application code, SQL/migrations, secrets, live API calls, wallet postings, group messages or data deletion |
| Constitution | Active blob a202123617e90da99249484acfd965b333cae48f remains unchanged; proposal is not ratification |
| Automation | This is application specification work, not a ChatGPT scheduled task |

Final static snapshot checks passed with zero reported errors. Machine checks on the final edited snapshot verify counts, ID/reference targets, local Markdown links, immutable setup status, 28 schema sections and changed-file scope. They do not test business functionality, SQL constraints, provider behavior or AOT hosts.

### Historical: Evidence gates, owners and required proof

| Gate | Required evidence | Planned owners | Stage blocked |
|---|---|---|---|
| E1 Governance | Explicitly ratified constitution alignment to the approved shared target, with version and risk/impact record; proposal supplies concrete text | T007, constitution-alignment-proposal.md | Formal design ratification/implementation policy signoff |
| E2 Provider contracts | Exact product/currency/stock schemas; callback/evidence time and quote satisfaction; remote key scope and safe retry; stable item/export identity; recoverable credentials and enforceable charge ceiling per provider | T006/T029/T037/T053/T055–060/T090–094 | Enablement of each real adapter and unattended paid procurement, not writing an offline probe |
| E3 Legacy compatibility | Protected source schema/cardinality, real USD liabilities/precision/denomination, prepaid ownership/expiry exceptions, historical aliases/partial deliveries/date promises, archive-only refund preservation and golden rounding/topup behavior | T006/T013/T029/T061–070 | Lossy field removal/import and final migration-compatibility freeze |
| E4 Runtime qualification | Selected PostgreSQL version and real constraint/lock/retention tests, exact monetary mapping, ciphertext roundtrip, published Linux Native AOT/dependency smoke and failure probes | T004–005/T008–017/T027/T034–036/T042/T052/T058/T074–078/T085–096 as mapped | Claims that the blueprint works; probes are first implementation work only after separate authorization |
| E5 Operational identity/privacy | Real bank-holder/provider-hosted merchant disclosure acceptance, verified staff identity/public contacts, trusted private group membership/routing and protected secret access | T007/T016/T038/T040/T044/T049/T051/T075/T080 | Live payment/customer/staff messaging activation |
| E6 Release/cutover | Isolated restore and reconciliation, stopped old writers, per-brand ownership, retained keys/images, rollback for accepted external effects and explicit separate operator release decision | T067–070/T074–081 | Production cutover only |

These are evidence gaps, not reopened questions about supplier selection, mixed fulfillment, refunds, coupon return, delays or the user's other approved business policies. A genuine incompatible source/provider fact must be brought back with concrete evidence, not guessed away.

### Historical: Selected engineering decisions

- One same-product source pointer; selected disable is allowed without fallback.
- Single accepted-time/FIFO column; explicit order kind, zero-due and terminal-resolution states.
- Current observation versus immutable accepted account/cost/quantity snapshots; remote max charge must be enforceable.
- Complete manifest generation and typed stable send parts; admin-only bonuses stay outside saleable stock/customer manifests.
- Exact ledger origins and bank-admin USD input, separate quote/native denomination and preserved legacy aliases.
- Explicit customer/staff recipient XOR, verified two-channel staff binding and independent backend auth.
- Protected held coupons are not revalidated as new purchases after owner edits.
- Full 28-table baseline precedes cyclic constraints. Operational schema/import manifests are not hidden business tables.
- Absolute FIFO head includes holds and cannot be bypassed by a scheduler optimisation.
- Separate safe 180-day maintenance clocks; no financial/domain cascades or expired analytics as replay authority.

### Historical: What was and was not run

The review read the pinned repository tree, root AGENTS.md, active feature selector, imported constitution, specification/plan/model/contracts/tasks/checklist and the repository's speckit-analyze checklist. No extension hooks file exists in the reviewed tree.

The PowerShell prerequisite command could not run because pwsh is unavailable. Its required paths/feature selector and complete task artifact were checked directly. The read-only analyze guidance was used for diagnosis; the user's explicit correction request authorized remediation and saved documentation artifacts. No skill, constitution or spec-kit script was modified.

Public documentation lookup returned no readable content in this session. No current supplier/SQL/library capability is certified from that failed lookup. Existing source-code evidence remains labelled source-only. PostgreSQL, native publish, provider and Telegram tests were not run; their implementations do not exist in this repository.

### Historical: Recommended next sequence

1. Review the canonical forward schema and concrete constitution amendment. Apply governance amendment only through its explicit approval/version procedure.
2. After separate implementation authorization, run offline PostgreSQL/crypto/AOT/provider-fixture qualification and produce the legacy inventory/golden evidence. Start with E3 risks that can change representation; do not immediately deploy or enable suppliers.
3. Record proof per gate, make any evidence-driven corrections, then sign off the affected design/compatibility scope.
4. Live credentials, paid procurement, customer messaging, production imports/cutover each require their later stage controls and explicit authorization. This review grants none.

The requested documentation consistency review is complete. The remaining critical governance conflict and stage-specific evidence are visible rather than silently waived.
