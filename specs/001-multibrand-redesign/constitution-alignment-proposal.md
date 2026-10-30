# Constitution alignment proposal — not ratified

**Prepared:** 2026-10-30. **Current constitution:** version 1.1.0 at [the imported source file](../../.specify/memory/constitution.md), unchanged.
**Proposed amendment:** 2.0.0, a major change because physical fleet/schema assumptions change.
**Authority:** already approved shared-backend/28-table requirements. This proposal adds no production, migration, live-provider or deployment permission.

## Why an amendment is needed

The imported constitution requires existing table/key types and per-instance database/stage binding (II) and three fully isolated production deployments/databases/admin credentials (III). The approved redesign deliberately uses a new normalized target, shared backend/catalog/inventory/database, global admins and selected shared provider accounts, while retaining branded customer isolation and safe source preservation.

The inherited governance says: “Any exceptions require a written rationale, risk assessment, approval, and a version amendment”. This document is the concrete proposal; it is not a ratified change. Do not undo approved business decisions to simulate compliance or silently edit the constitution during analysis.

## Proposed exact replacement for principle II

### II. Preserve source data, identities, obligations, and encryption (NON-NEGOTIABLE)

Existing production/staging databases, source schemas, key formats and instance bindings remain authoritative read-only migration evidence until an explicitly authorized cutover. Never assume similarly named source/target entities are compatible. A new separately reviewed target schema may normalize tables and assign target identities only with explicit source-to-target provenance, repeatable import, exact money/denomination preservation and reconciliation of every outstanding paid obligation.

Preserve source primary identifiers and historical references in protected evidence/mappings, including actual partial deliveries, prepaid entitlements and legacy external-refund archives. No current price or guessed ownership may reconstruct historical money or erase liability. Unexpected multi-product orders, non-USD wallets or unrepresentable source facts block the affected import until reviewed.

Legacy Fernet, previous-key and approved legacy/plaintext readers remain migration/recovery-only and must roundtrip against protected samples. New target ciphertext uses separately reviewed authenticated per-brand keyrings and a restricted shared-inventory scope, with reversible migration, verified backups and field/context integrity. Production sources are never automatically altered and target daemons never apply schema changes on startup.

## Proposed exact replacement for principle III

### III. Explicit shared-core boundaries and recoverability (NON-NEGOTIABLE)

The approved target may run one shared commerce backend, relational database, catalog/local stock pool and globally authorized administrative API for at least three separately branded Telegram bots. Brand/customer access, customer presentation, wallet accounting, bank destinations, encrypted brand secrets and audit attribution remain isolated. Shared inventory decryption has an independently restricted scope. Explicitly approved shared provider/merchant accounts do not merge customer balances or authorize automatic supplier fallback.

Shared infrastructure has a common failure/upgrade boundary; do not claim physical per-brand failure isolation. Define and test per-brand writer ownership, callback routing, stop/resume controls and rollback/reconciliation within that shared core. Keep staging credentials/data and all production traffic separate. Consolidated host/process choices require measured qualification, not implied deployment permission.

Migrate brands one at a time from verified frozen checkpoints after stopping all old writers for the affected brand. Stock must never be saleable by old and new systems simultaneously. Use verified immutable artifacts, backup/restore drills and retained keys/images; real production cutover requires a separate explicit operator decision. A target failure or rollback must preserve all brands' accepted money and committed background obligations.

## Proposed metadata and unchanged principles

After explicit approval, set the title to “AccAbad Multi-Brand Commerce Constitution”, Version to 2.0.0 and Last Amended to the actual approval date; keep original ratification provenance in the amendment record. Add a sync-impact note identifying approved FR-001–084 and this review.

Principles I and IV–VII remain unchanged: explicit parity exceptions, reliable financial/outbox effects, authenticated scope/privacy, evidence-based stack/AOT qualification and focused traceable testing. Governance and the separate production-release decision remain unchanged. Release gate wording is read against the approved shared target and protected independent source instances, never as permission to operate live systems.

## Risk assessment and evidence

| Change | Risk | Required control |
|---|---|---|
| Shared DB/core | A failure or bad migration can affect several brands | Static tenant guards, shared-stock serialisation, staged rehearsal, platform restore and per-brand writer ownership |
| New target IDs/schema | Mis-mapping or dropped legacy semantics | Protected snapshots, alias/key maps, repeat imports and signed reconciliation |
| New encryption scopes | Wrong-key disclosure or unrecoverable history | Migration-only old readers, new-context re-encryption, key separation and roundtrip/restore proof |
| Global staff/private group | Internal notices could leak if routed as customer content | Independent active admin auth, trusted staff-only recipient kind, metadata-only notices and protected secret retrieval |
| Shared supplier/merchant | Wrong account replay or common merchant disclosure | Original account binding, verified callback origin, safe retry/cost contracts and operator privacy acceptance |

## Application procedure

Review the exact replacement and risk table, record explicit approval, then apply the amendment as a separate governance change with version/impact metadata. Re-run constitution alignment analysis before calling the design ratified. Do not mark runtime/provider/migration tasks complete from ratification alone.

This review does not modify the active constitution file.
