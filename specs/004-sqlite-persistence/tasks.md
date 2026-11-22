# Tasks: SQLite Persistence

[Spec](spec.md) · [Plan](plan.md) · [Offline validation](quickstart.md)

## Setup

- [x] T001 Pin Microsoft.Data.Sqlite and the EF Core 10 SQLite provider in `Directory.Packages.props` and `src/Platform.Persistence/Platform.Persistence.csproj`. Regenerate affected lock files.

## Foundation

- [x] T002 Add the explicit empty SQLite schema and initialization operation in `ops/migrations/` and `src/Platform.SchemaRunner/Program.cs`. Preserve canonical keys, constraints, and brand boundaries. Keep synthetic seed rows inside disposable test fixtures.
- [ ] T003 Add SQLite sessions beside `src/Platform.Persistence/DbSession.cs`. Enable WAL and foreign keys, bound the busy timeout, and use immediate write transactions. Keep rollback, cancellation, and pre-commit integrity checks inside the session boundary.

## US1 — Run Akanti on a fresh SQLite staging database (P1)

**Independent check** — Use the existing fixtures with an empty SQLite file and synthetic seed data. Verify catalog authorization and cursor pagination, concurrent checkout, callback replay, and worker restart.

- [ ] T004 [P] [US1] Port the staging catalog and admin reads in `src/Platform.Persistence/`, including the EF Core product list in `AdminProductQuery.cs`. Preserve access checks, UUID ordering, and pagination.
- [ ] T005 [US1] Port checkout, payments, wallets, inventory allocation, and durable jobs as one batch in `src/Platform.Persistence/`, starting with `CheckoutStore.cs`, `PaymentSettlementStore.cs`, and `OutboxStore.cs`. Preserve exact decimal amounts and denominations, immutable postings, encryption, replay keys, and lease fences. Keep provider calls outside write transactions and retain uncertain outcomes for administrator review.
- [ ] T006 [US1] Wire SQLite mode through the staging API, bot, and worker entry points in `src/` and `ops/staging/deploy-shop-runtime.sh`. Share one persistent local database directory, reject an uninitialized file at startup, and retain rollback to the previous staging release.

**T005 progress** — The SQLite store layer now contains local-wallet checkout, prepared crypto/bank checkout with frozen quotes and trusted network/destination binding, encrypted allocation, guarded crypto invoice/IPN capture with restartable protected evidence archive, top-up/order settlement and shortage compensation, audited original bank receipt approval, local-preorder FIFO draining, proof-aware payment expiry, manifest/status delivery and lease-fenced outbox. Synthetic integration tests cover these paths and replay/tenant guards; the expanded suite is not yet fully qualified. A synthetic SQLite supplier path now records original source bindings, bounded prices, durable submission/reconciliation attempts, encrypted customer/bonus acquisition, safe replay validation and protected unknown outcomes; authenticated cost and completeness review and an adapter orchestrator have been added, but are not yet qualified or activated against any live provider. Coupon/prepaid checkout, full provider and administrator transport wiring, and comprehensive release/recovery parity remain open. Leave T005 unchecked pending required full-parity and recovery evidence.

**Acceptance detail** — T003 must verify canonical cross-row pre-commit integrity, WAL/foreign keys, contention, cancellation and rollback. T004 must prove tenant-scoped administrator/catalog reads and cursor order. T005 requires test-backed parity for coupon/prepaid wallet, crypto and bank issuance; verified payment and original evidence; supplier cost/uncertain-outcome review; preorder FIFO; durable delivery; and applicable customer/administrator API and bot routes. Synthetic adapters qualify behavior, not live-provider authorization. T006 must share a persistent SQLite directory across all staging roles and reject PostgreSQL fallback once SQLite state is published. Retain all unchecked task states until evidence exists.

## Qualification and deployment

- [ ] T007 Run the existing applicable automated checks through `.github/workflows/backend-ci.yml`. Adapt the fixtures in `tests/Platform.IntegrationTests/` and `tests/Shared/` for SQLite. Verify exact balances and stock, one settlement under replay, and restart recovery with stale lease rejection. Retain PostgreSQL regression checks.
- [ ] T008 Deploy the qualified build through `.github/workflows/backend-ci.yml`. Verify staging health, the active revision, and that the SQLite file persists across restart.

**T007/T008 evidence** — Qualify the exact candidate revision in CI with SQLite and PostgreSQL regressions, schema/constraint parity, crash/restart, WAL persistence, replay and rollback tests. Deploy only after separate staging authorization and verify image digest/revision, authenticated health, saved SQLite state after process restart and a compatible SQLite rollback or fail-closed recovery. The successful earlier staging cutover does not qualify later T005 changes.

**Order** — T001 → T002 → T003 → T004 and T005 → T006 → T007 → T008. Catalog reads and transactional stores can proceed independently once the schema and session boundary are set. The first deployable increment is US1 after qualification.
