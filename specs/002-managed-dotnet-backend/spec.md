# Feature Specification: Managed .NET Backend

**Feature**: `002-managed-dotnet-backend` · **Created**: 2026-11-20 · **Status**: Specified

Akanti already runs on .NET 10 and ASP.NET Core. This change switches its backend deployment from Native AOT to regular .NET (JIT). The [multi-brand redesign](../001-multibrand-redesign/spec.md) still defines shop behavior and data boundaries.

## User scenario and testing

**P1 — Run Akanti with regular .NET.** An operator can build the backend and run its API, Telegram bot host, and job worker using the .NET 10 ASP.NET Core runtime, with the same configuration and existing interfaces. The build also supplies the schema runner if the deployment uses it.

**Acceptance:** Given the managed build and existing test configuration, the backend starts and the existing automated suite passes. Use the existing fixtures and smoke coverage. No new test suite, manual Telegram test, live provider call, or production data is required for this change.

## Requirements

- **FR-001**: Target .NET 10 LTS with ASP.NET Core and use regular JIT publishing for the backend Docker image. Keep current process roles and application behavior.
- **FR-002**: Preserve the API routes, OpenAPI contract, administrator authorization, Telegram.Bot integration, PostgreSQL schema, Npgsql queries, background job semantics, and existing encryption.
- **FR-003**: Keep this backend only. No admin panel, customer web UI, EF Core, SQLite migration, or shop feature expansion is part of this change. Interactive API documentation can be added separately using the existing OpenAPI document.

## Success criteria

- **SC-001**: The managed backend builds and the existing automated suite and applicable offline smoke checks pass.
- **SC-002**: Record idle and peak RAM for the AOT and JIT builds using the same existing synthetic workload and process layout. Report the observed change without imposing a new memory target.

This spec does not authorize a production cutover.
