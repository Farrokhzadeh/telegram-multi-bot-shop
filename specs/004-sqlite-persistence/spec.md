# Feature Specification: SQLite Persistence

**Feature**: `004-sqlite-persistence` · **Created**: 2026-11-20 · **Status**: Specified

## User scenario and testing

Run the Akanti staging backend and bot on a fresh SQLite database on one host. Use synthetic brand, administrator, and catalog rows only in disposable qualification databases. Use the existing tests to exercise catalog reads, checkout, payments, allocation, and restart recovery. Existing PostgreSQL rows and AccAbad history are outside this feature. Production cutover requires separate approval.

## Requirements

- **FR-001**: Use a persistent SQLite file for the staging backend. Create the schema through an explicit setup step. Keep the existing logical entities, constraints, and brand boundaries. Use the SQLite provider for the existing EF Core catalog query and adapt PostgreSQL-specific stores where needed.
- **FR-002**: Preserve administrator access, catalog pagination, exact monetary values and denominations, shared inventory rules, and existing encryption. Keep current API contracts and bot behavior.
- **FR-003**: Preserve atomic checkout, settlement, wallet updates, and inventory allocation under concurrent requests. Replayed callbacks or retries must not create duplicate charges, credits, or allocations.
- **FR-004**: Persist outstanding jobs, leases, retry state, and delivery markers across restart. Retain uncertain payment, supplier, and delivery outcomes for administrator review.
- **FR-005**: Qualify the required customer and administrator transport paths, catalog reads and writes, wallet and prepared crypto/bank checkout, coupons and paid prepaid entitlement issuance, verified payment and manual review, stock/preorder fulfillment, supplier attempts, and manifest/Telegram recovery against the existing contracts. Use isolated synthetic provider adapters; live calls require separate approval.
- **FR-006**: Preserve canonical PostgreSQL logical constraints in SQLite, including tenant/ownership, unique replay keys, immutable financial postings, order/source provenance, and multi-row pre-commit checks. Compare schema inventories and the affected invariants before changing any baseline schema; reject missing or invented order columns.
- **FR-007**: Separate empty-file disposable qualification from the authorized isolated synthetic PostgreSQL-to-SQLite staging snapshot copy. Neither path imports protected legacy data. Preserve source/target identity, row coverage and integrity evidence without exposing secrets.
- **FR-008**: Keep SQLite data, WAL/sidecar files, and committed obligations intact across restart and deployment failure. After SQLite is published or accepts writes, rollback may use only a proven schema-compatible previous SQLite executable on the same accepted data; never automatically resume a stale PostgreSQL writer. Otherwise stop safely for operator recovery.

## Success criteria

- **SC-001**: The staging backend and bot start on a fresh SQLite database and pass the existing applicable checks against synthetic data.
- **SC-002**: The existing purchase and callback scenarios preserve exact balances and stock under concurrent checkout and replay. An interrupted worker resumes from persisted state without repeating committed work.
- **SC-003**: Each required SQLite commerce and administrator flow has targeted parity evidence against the canonical schema and contracts, including exact amounts, tenancy, encryption, replay and review states. No missing SQLite column or unverified provider operation is accepted as complete.
- **SC-004**: Failure injection verifies transaction rollback, busy-writer contention, callback replay, WAL/process restart, stale leases, interrupted evidence archiving, unknown supplier/delivery outcomes and recovery without duplicated financial or external effects.
- **SC-005**: One persistent SQLite directory survives API, bot and worker restarts with unchanged accepted balances, jobs, and delivery markers; a qualified same-database SQLite rollback works or the services fail closed without PostgreSQL fallback.
- **SC-006**: The exact candidate commit passes relevant SQLite tests, PostgreSQL regressions and staging artifact qualification before any separately approved deploy. Verify deployed image/revision, authenticated health and restart persistence; keep T003–T008 open until their individual gates are evidenced.

## Constitution check

[Principle IV](../../.specify/memory/constitution.md#iv-reliable-financial-and-background-side-effects-non-negotiable) permits SQLite for fresh, single-host staging once synthetic qualification verifies durable job recovery under contention, replay, and restart.
