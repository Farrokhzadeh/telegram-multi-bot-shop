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

**T005 progress** — SQLite local-wallet checkout, prepared catalog quote/checkout, encrypted allocation, verified crypto top-up, order-linked settlement, durable outbox, manifest preparation and Telegram delivery recovery now have synthetic implementations and focused tests. This does not establish full payment, supplier or administrative parity; verify exact financial invariants and recovery through T007 before checking T005.

## Qualification and deployment

- [ ] T007 Run the existing applicable automated checks through `.github/workflows/backend-ci.yml`. Adapt the fixtures in `tests/Platform.IntegrationTests/` and `tests/Shared/` for SQLite. Verify exact balances and stock, one settlement under replay, and restart recovery with stale lease rejection. Retain PostgreSQL regression checks.
- [ ] T008 Deploy the qualified build through `.github/workflows/backend-ci.yml`. Verify staging health, the active revision, and that the SQLite file persists across restart.

**Order** — T001 → T002 → T003 → T004 and T005 → T006 → T007 → T008. Catalog reads and transactional stores can proceed independently once the schema and session boundary are set. The first deployable increment is US1 after qualification.
