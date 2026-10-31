# AccAbad Multi-Brand Commerce Constitution

## Core Principles

### I. Behavioral parity and customer safety (NON-NEGOTIABLE)
A platform rewrite MUST preserve existing customer-facing and administrator workflows unless an explicitly approved, independently specified change replaces them. This includes purchases, balances, pricing, receipt approvals, inventory allocation, delivery, services, Telegram UI/callbacks, AI support, translations, notifications, and panel operations. The currently deployed Python application and its tests/documented behavior are the reference; every workflow MUST be inventoried with source anchors and measurable acceptance evidence. Historical records are never reconstructed from current prices.

### II. Preserve source data, identities, obligations, and encryption (NON-NEGOTIABLE)

Existing legacy shops remain operational under their existing owners until each brand's separately
authorized freeze. Captured production/staging evidence, source schemas, key formats and instance
bindings remain authoritative read-only migration inputs; review and migration tooling must not
alter production sources. Never assume similarly named source/target entities are compatible. A
new separately reviewed target schema may normalize tables and assign target identities only with
explicit source-to-target provenance, repeatable import, exact money/denomination preservation and
reconciliation of every outstanding paid obligation.

Preserve source primary identifiers and historical references in protected evidence/mappings,
including actual partial deliveries, prepaid entitlements and legacy external-refund archives. No
current price or guessed ownership may reconstruct historical money or erase liability. Unexpected
multi-product orders, non-USD wallets or unrepresentable source facts block the affected import
until reviewed.

Legacy Fernet, previous-key and approved legacy/plaintext readers remain migration/recovery-only
and must roundtrip against protected samples. New target ciphertext uses separately reviewed
authenticated per-brand keyrings and a restricted shared-inventory scope, with reversible
migration, verified backups and field/context integrity. Production sources are never
automatically altered and target daemons never apply schema changes on startup.

### III. Explicit shared-core boundaries and recoverability (NON-NEGOTIABLE)

The approved target may run one shared commerce backend, relational database, catalog/local stock
pool and globally authorized administrative API for at least three separately branded Telegram
bots. Brand/customer access, customer presentation, wallet accounting, bank destinations,
encrypted brand secrets and audit attribution remain isolated. Shared inventory decryption has an
independently restricted scope. Explicitly approved shared provider/merchant accounts do not merge
customer balances or authorize automatic supplier fallback.

Shared infrastructure has a common failure/upgrade boundary; do not claim physical per-brand
failure isolation. Define and test per-brand writer ownership, callback routing, stop/resume
controls and rollback/reconciliation within that shared core. Keep staging credentials/data and
all production traffic separate. The owner-selected staging environment may share the production
server only with separately qualified runtime, database/volume, secret, routing and resource
boundaries and an independent stop procedure. It shares the host failure boundary;
host-destructive staging tests must not run there. See
[staging-plan.md](../../specs/001-multibrand-redesign/staging-plan.md); this hosting choice does
not authorize deployment. Consolidated host/process choices require measured qualification, not
implied deployment permission.

Migrate brands one at a time from verified frozen checkpoints after stopping all old writers for
the affected brand. Stock must never be saleable by old and new systems simultaneously. Use
verified immutable artifacts, backup/restore drills and retained keys/images. Retain protected
legacy keys for as long as any retained encrypted archive, backup or rollback procedure depends on
them; real production cutover requires a separate explicit operator decision. A target failure or
rollback must preserve all brands' accepted money and committed background obligations.

### IV. Reliable financial and background side effects (NON-NEGOTIABLE)

A purchase or receipt approval MUST NOT charge or credit twice, allocate stock twice, create
duplicate logical fulfillment, or lose a committed job on crash. Customer credential release
requires an authorized complete-order manifest. Delivery recovery MUST reuse that manifest and
its durable delivery intents without repeating procurement, financial movements, allocation or
coupon redemption.

For order credential delivery, prefer editing an existing order message whose bot, membership,
order and message ID have been validated and durably saved. Retry an edit against that same
message and authorized manifest; recheck current order state and generation before editing so
stale work cannot overwrite the current result. Suppress known successful duplicate sends and
persist available delivery markers. Missing or uneditable messages, files and multi-part
delivery require explicit recovery handling; never silently fall back to a new credential
message after an uncertain outcome. Uncertain new-message sends MUST retain a durable recovery
state and pause automatic resending pending reconciliation or explicit authorized recovery.
Preserve the paid obligation and protected access to the same complete manifest. Do not promise
exactly-once remote Telegram receipt or that a customer has read a message.

Keep PostgreSQL-backed durable jobs and recovery semantics: deduplication, transactional
enqueue, leases, retries and persisted delivery markers. In-memory Channels MAY provide bounded
wake-up/dispatch, but MUST NOT be the sole record of outstanding business work. External
callbacks/webhooks require verification and replay protection.

### V. Security, tenant scope, and privacy
Every administrative action requires authentication, authorization, CSRF defenses for state-changing browser requests, and tenant-bound object lookups. Telegram callbacks MUST authorize the actor and the target operation. Never put bot tokens or passwords in URLs, repositories, logs, or config templates. Retain scoped media permissions, secure session cookies, encryption key separation, least privilege, redaction, and secret-free backups/reports. Sensitive media and credentials remain accessible only to authorized users.

### VI. Evidence-based implementation and efficiency
Prefer simple, modular components and independently verifiable service boundaries. Target supported .NET LTS for a C# rewrite, with Native AOT as a goal subject to a successful Linux publish and real dependency/runtime checks. No library, ORM, templating system, process consolidation, single-file packaging, startup time, or memory target is mandatory without feasibility evidence. Document baseline and target measurements before asserting performance gains. Native AOT must not require dropping functionality or safety.

### VII. Focused verification and traceability
Maintain feature-to-code-to-test traceability for critical flows. Prioritize targeted unit/contract tests and a small set of realistic PostgreSQL/Telegram-sandbox integration and failure-recovery tests; avoid redundant test suites. Tests are required for money, encryption, tenancy, migration, and delivery. Require warning review and an actual published Native AOT smoke test before claiming AOT compatibility.

## Development and release gates

1. Establish current main/staging baseline, feature inventory, schema inventory, encrypted sample fixtures, and operating resource baseline.
2. Complete a dependency spike before choosing data access/HTML/Telegram implementations; record warnings and fallback options.
3. Run against disposable copies of staging data with distinct credentials and no live payment or customer messages.
4. Demonstrate parity, tenant isolation, restore/rollback, and a publish-and-run probe before moving any traffic.
5. Treat changes to schema, money flows, security, and deployment as review blockers until validation evidence exists. Keep the current Python fleet operational during development.

## Amendment record

### 3.0.0 — 2026-10-31

The owner authorized the remaining reviewed fixes through "fix next things also", after
accepting the distinction between logical fulfillment and remote Telegram receipt and selecting
editing an existing order message with a saved ID as the preferred delivery path. This is a
governance/documentation authorization, not application implementation or live-operation approval.

- Input: active 2.0.0 constitution, blob `bebe289131002492ecd72df768d9c81e4c9a073e`;
  the six-finding alignment review and subsequent owner delivery clarification in this conversation.
- Major version rationale: principle IV's absolute prohibition on repeated remote credential
  messages is redefined as exactly-once local business effects with explicit transport uncertainty.
- Retained controls: one charge/credit, stock allocation, logical fulfillment and coupon
  redemption; durable jobs, verified callbacks, complete-manifest release and brand authorization.
- Duplicate-message risk: a lost send result may hide an accepted message. Prefer a validated
  saved order-message edit, suppress known successful sends, and pause uncertain new-message
  resends for explicit recovery. Files/multi-part and missing/uneditable-anchor cases require
  qualified recovery handling, never a blind replacement send.
- Delay risk: pausing an uncertain send may delay automatic delivery. Preserve the obligation,
  protected complete-manifest access and an observable recovery case; never mark it sent by guess.
- Wrong/stale edit risk: validate bot/member/order/message linkage and current generation/state;
  qualify crash, concurrent-edit and recovery cases under T034/T052/T077 and E4 before use.
- Principles I–III and V–VII, development/release gates and Governance remain unchanged.
  Original adoption remains 2026-10-15; the approved 2.0.0 record remains historical evidence.

E1 closure requires the separate alignment review; this amendment alone is not a readiness claim.
See the latest [consistency review](../../specs/001-multibrand-redesign/consistency-review.md)
for current governance status. E2–E6 retain their stage-specific proof and authorization gates.
The baseline remains 28 tables, 84 requirements, 53 success criteria and 96 tasks; only T001
is complete. No application implementation, migration execution, deployment, production data/key
access, live integration, financial mutation or deletion is authorized by this amendment.

### 2.0.0 — 2026-10-30

The owner ratified this amendment on 2026-10-30. The original constitution was adopted on
2026-10-15; the version metadata preserves that original ratification date.

- Approved baseline: FR-001–084 and SC-001–053; the 28-table shared-backend architecture.
- Exact approved source: [proposal at 2c869fa](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/blob/2c869fa23733fe1087f6fb12527eb6a644878b0a/specs/001-multibrand-redesign/constitution-alignment-proposal.md),
  blob `7190bf250852a38e75c5edce39c3fdd38d43e6da`.
- Approval provenance: [owner ratification record](../../specs/001-multibrand-redesign/constitution-ratification.md).
- Major version rationale: principles II and III redefine inherited physical schema/fleet
  assumptions to permit the approved shared target and reviewed source-to-target mapping.
- Principle II changes from preserving the physical source schema as the runtime contract to
  preserving source evidence, identities, obligations and recoverable encryption migrations.
- Principle III changes from per-instance deployment isolation to explicit shared-core and
  same-server staging boundaries, with qualified isolation, ownership and recovery controls.
- Principles I and IV–VII, the development/release gates and Governance remain unchanged.
  The staging-plan link in principle III is relocated for this file's directory.
- This record and the risk table preserve the approved written rationale/impact evidence.

At application of 2.0.0, the governance amendment was applied and the separate constitution
alignment review was pending. This historical record does not close E1 or establish readiness;
the latest amendment and consistency review determine current governance status.
Provider E2, legacy E3, runtime E4, identity/privacy E5 and release E6 retain their
[documented stages and required proof](../../specs/001-multibrand-redesign/consistency-review.md#evidence-gates-owners-and-required-proof).
No application implementation, migration execution, live integration, financial mutation or
production cutover is authorized by this amendment. Only repository setup T001 is complete.

### Approved risk assessment and controls

| Change | Risk | Required control |
|---|---|---|
| Same-server staging | Test load, restore mistakes or host failures can affect production | Separate runtime/data/secrets/routes, qualified resource limits, independent stop and no host-destructive tests |
| Shared DB/core | A failure or bad migration can affect several brands | Static tenant guards, shared-stock serialisation, staged rehearsal, platform restore and per-brand writer ownership |
| New target IDs/schema | Mis-mapping or dropped legacy semantics | Protected snapshots, alias/key maps, repeat imports and signed reconciliation |
| New encryption scopes | Wrong-key disclosure or unrecoverable history | Migration-only old readers, new-context re-encryption, key separation and roundtrip/restore proof |
| Global staff/private group | Internal notices could leak if routed as customer content | Independent active admin auth, trusted staff-only recipient kind, metadata-only notices and protected secret retrieval |
| Shared supplier/merchant | Wrong account replay or common merchant disclosure | Original account binding, verified callback origin, safe retry/cost contracts and operator privacy acceptance |

## Governance

This constitution supersedes feature plans. Any exceptions require a written rationale, risk assessment, approval, and a version amendment (MAJOR removes/weakens safeguards, MINOR adds principles, PATCH clarifies wording). A checked checklist means verified evidence, not an intention. The C# feature remains a **draft migration design**, not an authorized production cutover.

**Version**: 3.0.0 | **Ratified**: 2026-10-15 | **Last Amended**: 2026-10-31
