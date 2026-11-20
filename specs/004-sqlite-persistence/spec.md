# Feature Specification: SQLite Persistence

**Feature**: `004-sqlite-persistence` · **Created**: 2026-11-20 · **Status**: Draft

## User scenario and testing

Run the Akanti staging backend and bot on a fresh SQLite database on one host. Seed a test brand, administrator, and catalog with synthetic data. Use the existing tests to exercise catalog reads, checkout, payments, allocation, and restart recovery. Existing PostgreSQL rows and AccAbad history are outside this feature. Production cutover requires separate approval.

## Requirements

- **FR-001**: Use a persistent SQLite file for the staging backend. Create the schema through an explicit setup step. Keep the existing logical entities, constraints, and brand boundaries. Use the SQLite provider for the existing EF Core catalog query and adapt PostgreSQL-specific stores where needed.
- **FR-002**: Preserve administrator access, catalog pagination, exact monetary values and denominations, shared inventory rules, and existing encryption. Keep current API contracts and bot behavior.
- **FR-003**: Preserve atomic checkout, settlement, wallet updates, and inventory allocation under concurrent requests. Replayed callbacks or retries must not create duplicate charges, credits, or allocations.
- **FR-004**: Persist outstanding jobs, leases, retry state, and delivery markers across restart. Retain uncertain payment, supplier, and delivery outcomes for administrator review.

## Success criteria

- **SC-001**: The staging backend and bot start on a fresh SQLite database and pass the existing applicable checks against synthetic data.
- **SC-002**: The existing purchase and callback scenarios preserve exact balances and stock under concurrent checkout and replay. An interrupted worker resumes from persisted state without repeating committed work.

## Constitution check

[Principle IV](../../.specify/memory/constitution.md#iv-reliable-financial-and-background-side-effects-non-negotiable) requires PostgreSQL-backed durable jobs. Moving those jobs to SQLite requires a proposed amendment and owner approval before implementation. The proposal must preserve the existing durability and recovery guarantees.
