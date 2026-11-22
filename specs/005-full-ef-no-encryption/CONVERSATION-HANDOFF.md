# Full EF and encryption removal — specification checkpoint
- Baseline: main `c44dc1bde2966d866cdcca771fd5d8921621bf57`, 2026-11-22.
- Owner scope: only full Entity Framework conversion and removal of new Akanti application encryption; latest instruction requests specify only.
- Created: `spec.md`, `checklists/requirements.md`; feature pointer selects 005.
- Status: specification quality reviewed; no plan, tasks, implementation, conversion or deployment performed.
- Historical features/checklists/baselines and their hashes are unchanged.
- EF scope: all 28 entities, all maintained target runtime paths and explicit EF schema ownership; existing EF catalog-only milestone is insufficient.
- Encryption scope: all target content and runtime key dependencies; retain authentication, hashes/signatures, TLS, access restrictions and legacy-only readers/keys needed by source/archive/backup recovery.
- Next when requested: run speckit-plan for 005, inventory runtime persistence/encryption and schema fields, record the scoped owner-approved target-encryption governance amendment, then design model, migration, invariants, conversion/recovery and acceptance coverage.
- Out of scope: AccAbad mapping/import, feature 002 follow-up, live providers, protected-data execution, staging/production changes and key retirement.
