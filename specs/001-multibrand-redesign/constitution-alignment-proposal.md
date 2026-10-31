# Constitution alignment amendment 2.0.0 — applied; historical approval text

**Prepared:** 2026-10-30. **Historical input:** inherited version 1.1.0. **Current status, 2026-10-31:** this 2.0.0 amendment is applied; the [active constitution](../../.specify/memory/constitution.md) is now 3.0.0 following the separately owner-authorized delivery recovery amendment. The pinned approval text remains authoritative for 2.0.0; dated procedure below records its original approval stage.
**Owner-ratified amendment:** 2.0.0, a major change because physical fleet/schema assumptions change.
**Ratified:** 2026-10-30; see [the exact approval record](constitution-ratification.md). Application is complete. See the latest consistency review for alignment status; E2–E6 retain separate evidence requirements.
**Authority:** already approved shared-backend/28-table requirements. This proposal adds no production, migration, live-provider or deployment permission.

## Why an amendment is needed

The imported constitution requires existing table/key types and per-instance database/stage binding (II) and three fully isolated production deployments/databases/admin credentials (III). The approved redesign deliberately uses a new normalized target, shared backend/catalog/inventory/database, global admins and selected shared provider accounts, while retaining branded customer isolation and safe source preservation.

The inherited governance says: “Any exceptions require a written rationale, risk assessment, approval, and a version amendment”. The owner ratified the exact proposal at commit 2c869fa23733fe1087f6fb12527eb6a644878b0a on 2026-10-30; the pinned approval source is recorded separately. At ratification, application and alignment analysis were pending; application is now complete and the latest review supplies current alignment status. Do not undo approved business decisions to simulate compliance or silently edit the constitution during analysis.

## Ratified exact replacement for principle II

### II. Preserve source data, identities, obligations, and encryption (NON-NEGOTIABLE)

Existing legacy shops remain operational under their existing owners until each brand's separately authorized freeze. Captured production/staging evidence, source schemas, key formats and instance bindings remain authoritative read-only migration inputs; review and migration tooling must not alter production sources. Never assume similarly named source/target entities are compatible. A new separately reviewed target schema may normalize tables and assign target identities only with explicit source-to-target provenance, repeatable import, exact money/denomination preservation and reconciliation of every outstanding paid obligation.

Preserve source primary identifiers and historical references in protected evidence/mappings, including actual partial deliveries, prepaid entitlements and legacy external-refund archives. No current price or guessed ownership may reconstruct historical money or erase liability. Unexpected multi-product orders, non-USD wallets or unrepresentable source facts block the affected import until reviewed.

Legacy Fernet, previous-key and approved legacy/plaintext readers remain migration/recovery-only and must roundtrip against protected samples. New target ciphertext uses separately reviewed authenticated per-brand keyrings and a restricted shared-inventory scope, with reversible migration, verified backups and field/context integrity. Production sources are never automatically altered and target daemons never apply schema changes on startup.

## Ratified exact replacement for principle III

### III. Explicit shared-core boundaries and recoverability (NON-NEGOTIABLE)

The approved target may run one shared commerce backend, relational database, catalog/local stock pool and globally authorized administrative API for at least three separately branded Telegram bots. Brand/customer access, customer presentation, wallet accounting, bank destinations, encrypted brand secrets and audit attribution remain isolated. Shared inventory decryption has an independently restricted scope. Explicitly approved shared provider/merchant accounts do not merge customer balances or authorize automatic supplier fallback.

Shared infrastructure has a common failure/upgrade boundary; do not claim physical per-brand failure isolation. Define and test per-brand writer ownership, callback routing, stop/resume controls and rollback/reconciliation within that shared core. Keep staging credentials/data and all production traffic separate. The owner-selected staging environment may share the production server only with separately qualified runtime, database/volume, secret, routing and resource boundaries and an independent stop procedure. It shares the host failure boundary; host-destructive staging tests must not run there. See [staging-plan.md](staging-plan.md); this hosting choice does not authorize deployment. Consolidated host/process choices require measured qualification, not implied deployment permission.

Migrate brands one at a time from verified frozen checkpoints after stopping all old writers for the affected brand. Stock must never be saleable by old and new systems simultaneously. Use verified immutable artifacts, backup/restore drills and retained keys/images. Retain protected legacy keys for as long as any retained encrypted archive, backup or rollback procedure depends on them; real production cutover requires a separate explicit operator decision. A target failure or rollback must preserve all brands' accepted money and committed background obligations.

## Approved metadata and unchanged principles

During step 2, apply the owner-ratified metadata: set the title to “AccAbad Multi-Brand Commerce Constitution”, Version to 2.0.0 and Last Amended to the actual approval date; keep original ratification provenance in the amendment record. Add a sync-impact note identifying approved FR-001–084 and this review.

Principles I and IV–VII remain unchanged: explicit parity exceptions, reliable financial/outbox effects, authenticated scope/privacy, evidence-based stack/AOT qualification and focused traceable testing. Governance and the separate production-release decision remain unchanged. Release gate wording is read against the approved shared target and protected independent source instances, never as permission to operate live systems.

## Risk assessment and evidence

| Change | Risk | Required control |
|---|---|---|
| Same-server staging | Test load, restore mistakes or host failures can affect production | Separate runtime/data/secrets/routes, qualified resource limits, independent stop and no host-destructive tests |
| Shared DB/core | A failure or bad migration can affect several brands | Static tenant guards, shared-stock serialisation, staged rehearsal, platform restore and per-brand writer ownership |
| New target IDs/schema | Mis-mapping or dropped legacy semantics | Protected snapshots, alias/key maps, repeat imports and signed reconciliation |
| New encryption scopes | Wrong-key disclosure or unrecoverable history | Migration-only old readers, new-context re-encryption, key separation and roundtrip/restore proof |
| Global staff/private group | Internal notices could leak if routed as customer content | Independent active admin auth, trusted staff-only recipient kind, metadata-only notices and protected secret retrieval |
| Shared supplier/merchant | Wrong account replay or common merchant disclosure | Original account binding, verified callback origin, safe retry/cost contracts and operator privacy acceptance |

## Historical application procedure — completed for 2.0.0

Step 1 is complete: owner ratification of the pinned exact replacement, metadata and risk table is recorded in constitution-ratification.md. At that approval stage, step 2 remained: apply the amendment as a separate governance change with version/impact metadata. Re-run constitution alignment analysis before calling the design ratified. Do not mark runtime/provider/migration tasks complete from ratification alone.

This records the original two-stage procedure. The separately applied active constitution has since advanced through 2.0.0 to 3.0.0; see its amendment records and the latest consistency review. The pinned 2.0.0 approval is not retroactively changed.
