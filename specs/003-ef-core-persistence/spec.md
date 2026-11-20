# Feature Specification: Entity Framework Core Persistence
**Feature**: `003-ef-core-persistence` · **Created**: 2026-11-20 · **Status**: Specified
## User scenario and testing
A backend maintainer can use Entity Framework Core for routine data access on .NET 10. Customers and administrators continue to see the same catalog, permissions, and API responses. Start with the administrator product list as the first conversion. Use the existing tests against disposable PostgreSQL to verify the list, including empty pages, page boundaries, and authorization.
## Requirements
- **FR-001**: Use EF Core 10 with the matching Npgsql PostgreSQL provider inside `Platform.Persistence`. Keep existing store interfaces, API contracts, and bot flows.
- **FR-002**: Map the first catalog read to the existing `shop.products` table. Preserve the existing shared-catalog access rules, product fields, sort order, and cursor pagination. Extend EF Core to routine reads and writes in groups where the existing behavior and transaction guarantees can be preserved.
- **FR-003**: Keep the [canonical PostgreSQL schema](../001-multibrand-redesign/schema-contract.md) and [explicit SQL migrations](../../ops/migrations) as the schema authority. Map existing types, keys, defaults, and database-generated values as needed. Schema changes remain explicit operator operations.
- **FR-004**: Preserve brand scoping, intentional shared inventory, database roles, encryption, and imported history. Retain the existing SQL for operations that depend on PostgreSQL locks, guarded updates, or transaction ordering, including checkout, settlements, allocation, and job claims. Where an operation combines EF Core and SQL, preserve its existing transaction boundary.
## Success criteria
- **SC-001**: The administrator product list runs through EF Core. The existing PostgreSQL integration and API contract checks pass, with the same ordering, pagination, and access control.
- **SC-002**: The change can start against an existing staging-schema copy without schema changes or a data import. Existing offline build and smoke checks qualify the managed backend for staging deployment.
- **SC-003**: Compare idle and peak RAM before and after EF Core on the same disposable workload, exercising the converted catalog query on both builds. Report the observed change.
