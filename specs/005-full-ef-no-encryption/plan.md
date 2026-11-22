# Implementation Plan: Full EF Persistence and Encryption Removal
**Branch**: `main` | **Date**: 2026-11-22 | **Spec**: [spec.md](spec.md)
**Input**: `specs/005-full-ef-no-encryption/spec.md`; baseline `48eeccda54275cfaf3c7b5c1ba22eff1d08d650b`.
## Summary
Convert all 28 target tables and every maintained target read/write path to EF Core, then remove application encryption completely from new Akanti. Preserve contracts, money, tenancy, durable jobs and uncertain-outcome handling. Deliver an explicit, recoverable old-encrypted-target to new-plaintext-target conversion design/tool; do not perform protected conversion or deployment in this feature's implementation without its separate execution authorization. Existing source readers/keys remain recovery-only. Scope does not include AccAbad mapping or other unfinished features.
## Technical Context
**Language/Version**: C#14, .NET10, SDK10.0.403; existing enforced C# whitespace remains spaces.
**Primary Dependencies**: Existing EF relational/SQLite10.0.15 and Npgsql provider10.0.3; private design/tool pins added with focused compatibility checks, no unrequested framework upgrade.
**Storage**: SQLite staging and retained PostgreSQL target support; one 28-entity logical model, provider-specific mappings and migration snapshots; EF migration bookkeeping is infrastructure, not a 29th business table. Plain target database/asset storage; legacy encryption readers confined to tooling.
**Testing**: Existing unit/contract/integration suites, focused synthetic provider parity/failure tests and managed publish/run probes. No live provider/Telegram calls. Reuse existing fixtures and avoid redundant suites.
**Target Platform**: Existing Linux API/BotHost/JobHost/SchemaRunner role layout; managed .NET runtime. No speculative service consolidation or AOT claim.
**Project Type**: Multi-brand commerce backend, Telegram transport and durable workers.
**Performance Goals**: No claimed speedup or invented threshold. Record baseline/candidate query counts, representative latency and idle/peak RSS under identical synthetic bounded workloads. Reject unbounded client materialization, full-table per-command validation and pagination regressions; investigate measured regressions before completion.
**Constraints**: Exact values/denominations and source identities; shared stock with scoped access; one transaction per logical operation, bounded writer contention; no implicit schema changes, live calls, protected-data execution or deployment. Keep all historical evidence hashes intact.
**Scale/Scope**: 28 business entities, all maintained target runtime paths including target writes in existing migration tools, both retained target providers, all target encryption fields/files/bootstrap paths. Full completion is not a catalog-only milestone.
## Constitution Check
| Gate | Before research | After design |
| --- | --- | --- |
| Existing user/admin behavior | Preserve except the owner's encryption change | Existing contracts retained; US1 parity checks required |
| Identity, money, obligations | No data/denomination reinterpretation | Exact mapping, immutable postings and full conversion coverage |
| Target encryption | Owner explicitly supersedes prior target keyring requirement | Scoped constitution 4.0.0 amendment records approval, rationale and plaintext-storage risk |
| EF selection | Explicit owner framework requirement | All 28 mapped; retained paths cannot silently fall back to business SQL |
| Financial/background durability | Preserve logical transaction and remote fences | Explicit UoW, concurrency guards, pre-commit validation, durable leases/replay evidence |
| Security | Remove only content encryption | Retain authentication, hashing/signatures, least privilege, scoped content access and redaction |
| Migration/release | No implicit activation or protected writes | Copy/conversion is an explicit tool; actual execution/deploy remains separately gated |
| Evidence/governance | Preserve pinned source/checklists/hashes | Only prospective governance and new design docs changed; no historical completion claims reused |
Preflight searched workflow/CI/Spec Kit scripts/evidence for constitution path/blob pins; no pin requiring the current constitution file to remain immutable was found. Historical text remains preserved under the feature-specific override. The owner decision supplies the approval; no new decision is required. Gates pass for design only; runtime qualification is outstanding.
## Project Structure
| Path | Responsibility |
| --- | --- |
| `src/Platform.Persistence/Entities/` | 28 plain entity types; no duplicate domain model or encryption wrappers |
| `src/Platform.Persistence/Configurations/` | Explicit field/key/FK/index/check/provider mapping, no cascade deletion of retained history |
| `src/Platform.Persistence/AkantiDbContext.cs` | Shared model and narrowly provider-specific derived contexts |
| `src/Platform.Persistence/EfSession.cs` | Existing-session-compatible EF unit of work, authorization/writer binding and commit validation |
| `src/Platform.Persistence/EfIntegrity.cs` | EF-based translated pre-commit checks replacing business SQL assertions |
| `src/Platform.Persistence/Migrations/Sqlite/`, `Migrations/PostgreSql/` | Provider migrations/snapshots; never runtime startup migration |
| `src/Platform.SchemaRunner/` | Explicit migration/adoption/conversion command routing and identity receipts |
| `src/Platform.Migration/` | Existing source readers plus isolated old-target recovery reader and new EF target writer |
| `src/Platform.Api/`, `Platform.BotHost/`, `Platform.JobHost/` | Final EF/plain-content registration and startup; keep required authentication credentials |
| `src/Platform.Security/` | Retained auth/verification/file protections; target ciphers retired, old readers moved to tooling |
| `src/Platform.Payments/`, `Platform.Fulfillment/`, `Platform.Telegram/`, `Platform.Campaigns/` | Existing callers/adapters updated without live effects |
| `ops/staging/`, `.github/workflows/backend-ci.yml` | Plain-target synthetic configuration and verification; deployment requires separate authorization |
| `tests/Platform.*Tests/` | Extend existing meaningful fixtures for parity, money, recovery, migration and content access |
Documentation: `research.md`, `data-model.md`, `contracts/persistence.md`, `contracts/conversion.md`, `quickstart.md`, `inventory.md`, `tasks.md`, rolling handoff and checklists under this feature directory. Prefer existing names/utilities; new filenames in this plan are planned destinations, not existing implementations.
## Phase 0 — Baseline and feasibility
Use the checked-in inventory as the review starting point; verify runtime callers, EF queries, raw SQL, direct crypto, reference-only asset formats and target-writer tools. Record each file's disposition, not just grep counts. Reuse the existing test/build baseline; add narrowly required provider compatibility, exact-decimal/order translation and migration-rebuild probes. Prove model-owned artifacts survive SQLite rebuilds and PostgreSQL guards remain effective. Do not begin store conversion if a critical financial invariant has no valid EF/UoW strategy.
## Phase 1 — Shared foundation
Build explicit entities/configuration from the pinned table/constraint references in data-model. Add contexts using existing connections and transactions, provider selection included in model cache identity. Preserve exact decimal text/numeric, UTC, UUID ordering, binary values, revision concurrency, composite tenant references and restricted deletion. Preserve password/token hashes and signature-key metadata. Establish reviewed provider migration baselines and an existing-file adoption procedure that validates schema before recording migration history; no automatic EnsureCreated/Migrate in daemons. Migrations represent constraints, filtered uniqueness and database guards, including reviewed DDL only where needed. The two migration lineages first adopt the encrypted baseline, then explicitly transition to the final plaintext model through the offline conversion protocol; no dummy decrypting migration runs in daemons.
The foundation uses a small disposable mapped fixture to validate transaction/validation and migration-runner infrastructure. Full 28-table migration generation and full entity-dependent assertions follow entity/configuration tasks in US1; scaffolding does not depend on later unimplemented entities.
## Phase 2 — US1 complete EF conversion
Convert in dependency order: identity/branding/catalog and admin; exact wallet postings; prepared checkout/coupons; payment capture, evidence, settlement and review; allocation/manifests/supplier request and review; outbox/Telegram recovery; campaigns/support/history/retention; existing migration-tool target writers. Read queries use bounded projections/no tracking; writes use one authorized UoW with guarded revisions/affected-row checks. Both retained providers must pass their affected behavior checks. Keep external calls outside DB transactions; preserve purchase/receipt idempotency and unknown outcomes. Update callers outside Persistence and remove old domain SQL paths after parity passes. US1 can be tested independently with the existing encrypted representation; it is an intermediate internal milestone, not feature completion.
## Phase 3 — US2 remove target encryption
Introduce the final plaintext schema/DTOs and direct content/credential access throughout all roles. Migrate every formerly encrypted column and referenced asset format, remove encryption-only key columns/checks and key manifests, and retire ciphers and encryption-specific IPC. Preserve semantic signature keys, keyed fingerprints, auth tokens, file protections and scoped service access. Move historical decryptors into migration/recovery-only tooling. Update synthetic provisioning, credentials, compose/mount checks and fixtures to boot without target encryption keys. Remove direct inbox/history/evidence encryption as well as column-based encryption; shared library reference alone is not proof of a runtime dependency, but runtime code may not load old keyrings.
## Phase 4 — Explicit conversion and failure recovery
Implement identity-bound copy-and-convert against isolated synthetic source fixtures first, using exact old codecs and new EF targets. Enumerate all records and referenced artifacts, preserve digests/provenance, reject missing/wrong keys or incomplete coverage, checkpoint safely, and validate full financial/content/foreign-key state before publication. Never mutate the original. Manifest contains format/schema versions, identities, hashes, record/asset dispositions and private checkpoint references; no secret values. Conversion publication is separate from daemon startup. Preserve accepted new writes on rollback; allow only same-plaintext-schema compatible executable recovery or stop safely. A restored old snapshot is not a rollback after accepted writes.
## Phase 5 — Qualification and closure
Run focused checks while converting, then full affected SQLite/PostgreSQL suites, formatting, warning review and managed Linux publish/run probes at the exact final candidate. Prove all three roles start without application keyrings, unauthorized content remains inaccessible, money/replay/failure invariants hold, and synthetic conversion resumes without data loss. Capture bounded comparable resource observations and full inventory dispositions. Qualify workflow/schema/provision tooling with deployment suppressed. Do not alter existing pinned AOT qualification history or claim full-EF AOT support. Tasks remain unchecked until their evidence exists. No staging deployment task is included; request execution authorization only if separately asked to deploy.
## Complexity Tracking
| Change | Why required | Rejected shortcut |
| --- | --- | --- |
| Scoped major governance amendment | Owner removed application encryption and requires full EF | Leaving contradictory target keyring rules silently active |
| Two provider migration lineages | Both target providers remain maintained | A SQLite-only model with hidden PostgreSQL business SQL |
| Explicit UoW and offline conversion | Financial atomicity and exact recoverable transformation | SaveChanges-only atomicity or destructive in-place reinterpretation |
