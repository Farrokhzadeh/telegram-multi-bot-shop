# Tasks: Entity Framework Core Persistence

[Spec](spec.md) · [Plan](plan.md)

## Setup

- [x] T001 Pin EF Core 10 and the matching Npgsql provider in `Directory.Packages.props` and `src/Platform.Persistence/Platform.Persistence.csproj`. Regenerate affected lock files. Keep current publishes managed and preserve route generation and the pinned Native AOT baseline in `.github/workflows/backend-ci.yml`.

## US1 — Read the admin product list with EF Core (P1)

**Independent check** — Call `GET /v1/admin/products` through the existing test host with disposable PostgreSQL. Verify authorization, ordering, and pagination against the current contract.

- [x] T002 [US1] Add a read-only, no-tracking mapping for the required `shop.products` columns in `src/Platform.Persistence`. Use the connection, transaction, and timeouts already owned by `DbSession.cs`.
- [x] T003 [US1] Convert `ProductsAsync` in `src/Platform.Persistence/AdminCatalogStore.cs`. Keep the active-admin check, DTO, PostgreSQL UUID ordering, exclusive cursor, and `limit + 1` pagination. Verify the generated SQL.
- [x] T004 [US1] Run the existing PostgreSQL integration, API contract, and managed smoke checks through `.github/workflows/backend-ci.yml`. Cover active and revoked admins, empty and final pages, and UUID cursor boundaries using existing fixtures. Extend a fixture only for a missing regression case. Verify against the current schema without applying a migration.

## Measure and deploy

- [x] T005 Compare idle and peak RAM for the pre-EF managed baseline and candidate using the same synthetic admin catalog requests, rows, and resource limits. Record both readings and percentage changes in `ops/PERFORMANCE_EVIDENCE.md`.
- [x] T006 Deploy the qualified build through `.github/workflows/backend-ci.yml` and verify staging health, the deployed revision, and availability of the existing AccAbad staging views.

**Order** — T001 → T002 → T003 → T004 → T005 → T006.

[Qualification and staging deployment](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/44796212843) passed for `2dab10b`. All 1,265 tests and managed smoke checks passed. The active staging release matched the deployed revision, its poller and core were running, and the AccAbad view overlay was present and wired.
