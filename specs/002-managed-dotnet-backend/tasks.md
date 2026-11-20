# Tasks: Managed .NET Backend

**Input:** [Spec](spec.md) · [Plan](plan.md) · [Offline validation](quickstart.md)

## Setup

- [x] T001 Record the current AOT image, role layout, and existing synthetic workload from `.github/workflows/backend-ci.yml` and `ops/PERFORMANCE_EVIDENCE.md` so the managed build has a comparable baseline.

## Foundation

- [ ] T002 Publish the API, bot, worker and applicable schema runner as regular .NET 10 in `.github/workflows/backend-ci.yml` and `ops/staging/Dockerfile`. Use framework-dependent outputs in the ASP.NET Core image and retain a self-contained JIT publish where a helper runs directly on the host.

## US1 — Run Akanti with regular .NET (P1)

**Independent check:** Boot the managed image with the existing offline configuration. Confirm Development docs load, other environments omit the docs, and existing contract and authorization checks pass.

- [ ] T003 [P] [US1] Add only Swagger UI middleware in `Directory.Packages.props` and `src/Platform.Api/Platform.Api.csproj`. Wire `/swagger` and `/openapi/admin-api.json` in `src/Platform.Api/Program.cs` using `src/Platform.Api/OpenApiContract.cs`. Show the existing 29 operations in Development and keep admin bearer authorization unchanged.
- [ ] T004 [US1] Update managed launch and artifact paths in `ops/staging/deploy-dockerhub.sh` and `ops/staging/deploy-shop-runtime.sh`. Preserve role arguments, schema matching, health and rollback behavior, and the current staged AccAbad data and imported views.

## Verification and documentation

- [ ] T005 Run the existing automated suites and applicable offline smoke checks from `.github/workflows/backend-ci.yml`. Use `specs/002-managed-dotnet-backend/quickstart.md` to check the local Swagger routes and existing contract. No new suite or live Telegram, provider or production-data checks.
- [ ] T006 Run the same existing synthetic workload against AOT and JIT with matching role layout and resource settings. Record idle and peak RAM and the percentage change in `ops/PERFORMANCE_EVIDENCE.md`.
- [x] T007 Update `README.md` and `docs/ADMIN_API.md` with the managed deployment, Development Swagger URL, and existing bearer authentication.

**Order:** T001 starts the baseline. T002 and T003 can proceed independently because the publish and API edits touch different files. T004 follows T002. T005 follows T002–T004, then T006 and T007 close the feature. The MVP is US1 with offline verification. Production cutover is outside this checklist.
