# Implementation Plan: Entity Framework Core Persistence

**Branch**: `main` | **Date**: 2026-11-20 | **Spec**: [EF Core Persistence](spec.md)

## Scope and design

Use EF Core 10 and the matching Npgsql provider on the existing managed .NET 10 backend. Convert `AdminCatalogStore.ProductsAsync` first. Keep the [admin API contract](../../docs/ADMIN_API.md), including `GET /v1/admin/products`, and use the existing store and DTOs. Later conversions can follow in related groups once this first slice passes.

1. Pin compatible packages in [Directory.Packages.props](../../Directory.Packages.props) and regenerate affected lock files. Keep EF Core inside [Platform.Persistence](../../src/Platform.Persistence). Use managed publishes for projects that consume it, retain route generation, and keep the existing Native AOT comparison on its pinned pre-EF revision. EF Core's [Native AOT support](https://learn.microsoft.com/ef/core/performance/nativeaot-and-precompiled-queries) is experimental.
2. Add a small read-only EF mapping for `shop.products`. Map `id` as the UUID key and project `title`, `fulfillment_kind`, `availability_policy`, `local_first`, `active`, and `revision` into the existing `AdminProduct`. Query without tracking. Preserve PostgreSQL UUID ordering and the exclusive cursor by checking the generated SQL. Fetch `limit + 1` rows and preserve the existing next-cursor rule.
3. Run the query on the connection and transaction already owned by `DbSession`, after the existing active-admin check. Give the context the [existing connection and transaction](https://learn.microsoft.com/ef/core/saving/transactions), keep disposal scoped to the query, and preserve timeout and cancellation behavior.

## Constitution check

The shared catalog keeps its existing administrator access rules. The [canonical schema](../001-multibrand-redesign/schema-contract.md) and [explicit SQL migrations](../../ops/migrations) remain authoritative. The plan preserves existing database roles, encryption, imported history, and the SQL and transaction guarantees for checkout, payments, allocation, and job claims.

## Verification and staging

- Reuse the existing admin catalog and API contract fixtures on disposable PostgreSQL. Check active and revoked admin sessions, empty and final pages, and cursor boundaries with known UUIDs. Extend an existing fixture only if it misses a specific regression.
- Run the existing locked restore, build, automated tests, and managed smoke checks in [backend CI](../../.github/workflows/backend-ci.yml). Verify the EF query works against the existing schema without applying a migration.
- Compare managed builds before and after EF Core with the same synthetic admin catalog request, data, and process limits. Record idle and peak RAM and the percentage change. Deploy through the existing staging workflow after qualification.
