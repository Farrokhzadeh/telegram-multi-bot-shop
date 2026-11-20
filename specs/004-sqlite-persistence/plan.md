# Implementation Plan: SQLite Persistence

**Branch**: `main` | **Date**: 2026-11-20 | **Spec**: [SQLite Persistence](spec.md)

## Summary

Run the managed Akanti backend and Telegram bot against a fresh SQLite file on the staging host. Preserve existing behavior and use synthetic seed data.

## Technical context

Use .NET 10, Microsoft.Data.Sqlite and the EF Core 10 SQLite provider. Retain the existing store interfaces and PostgreSQL deployment path. Reuse the existing automated fixtures and GitHub Actions qualification. See [research](research.md) for storage and concurrency decisions.

## Constitution check

Pass before and after design under the staging allowance in v3.1.0. Keep logical schema constraints, brand boundaries, authorization, encryption, exact amounts and durable recovery. The existing PostgreSQL release and legacy data remain available for rollback.

## Implementation

1. Add explicit schema setup and a SQLite session boundary in [persistence](../../src/Platform.Persistence) and [SchemaRunner](../../src/Platform.SchemaRunner). Use the [data model](data-model.md) and switch the existing EF Core admin catalog query for SQLite mode.
2. Port the checkout, wallet, payment, allocation and durable job stores together. Replace PostgreSQL locks and deferred trigger checks with equivalent transaction guards. Preserve callback and command replay handling and keep provider calls outside write transactions.
3. Wire SQLite into the existing staging API, bot, and worker entry points and deploy helpers. Mount one persistent database directory shared by these roles. Seed synthetic data and qualify the full path before replacing the staging runtime. Retain rollback to the previous staging release.

## Project structure

[Research](research.md) · [Data model](data-model.md) · [SQLite runtime contract](contracts/sqlite-runtime.md) · [Validation quickstart](quickstart.md)
