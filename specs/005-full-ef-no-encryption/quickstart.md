# Validation guide: Feature005
This is a prospective synthetic qualification guide. Planning creates no implementation, performs no migration and changes no running service.
## Prerequisites
Use main, existing SDK10.0.403, pinned packages/locks, existing synthetic test facilities and disposable SQLite/PostgreSQL databases. Read AGENTS.md and this feature's contracts first. Keep private logs and exit codes for bounded jobs; use configured resource/timeout caps. Do not point tests or new conversion tooling at production. No live provider credentials or Telegram delivery.
## Current document checks
```bash
bash .specify/scripts/bash/check-prerequisites.sh --json --require-tasks --include-tasks
python3 .specify/tests/test_linux_tooling.py
git diff --check
```
Expected:005 resolves without a branch change, all design docs and task checklist exist, existing tooling tests pass, no whitespace errors. These results are not runtime acceptance.
## US1 validation after implementation
1. Restore locked dependencies and build the existing solution using existing bounded job patterns. Run the extended existing provider fixtures for entity/schema inventory, UUID cursor, exact decimal/UTC/binary translation, authorization/revocation, money/last-item contention and pre-commit negative cases.
2. Run affected unit/contract/integration tests using the repository's existing fixtures/configuration. For targeted development use `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Ef' --verbosity minimal` after the Ef fixtures are implemented; a zero-test selection is not a pass. Existing affected Sqlite/DbSession/contract fixtures must also run, not just the new filter.
3. Create disposable schemas through the explicit EF schema runner; verify all 28 business entities, fields, constraints/indexes/guards and provider migration metadata. Exercise existing-file adoption with exact baseline and reject drift. Prove guards survive a SQLite rebuild. Confirm no daemon startup DDL.
4. Update inventory.md with every target path disposition and SQL exception. Inspect generated queries and actual call paths; no raw target business-query fallback. Record comparable bounded latency/RSS/query counts; no unbounded client work.
Expected: all maintained SQLite/PostgreSQL behavior remains equivalent, exact values and transaction invariants hold, and all 28 entities are covered. Existing encrypted representation may be used to validate US1 independently.
## US2 validation after implementation
1. Boot disposable API/bot/worker with target encryption key manifests/grants absent and fake/local transports; retain required API tokens and bot/provider identity fixtures. Never start a real Telegram poller to prove this test.
2. Exercise credentials/fulfillment, payment evidence/bank content, supplier evidence, support/media, prepaid code notices, inbox/history and configuration. Verify stored bytes directly and authorized read output after all-role restart. Verify cross-brand/unauthenticated access remains denied and secrets do not appear in list DTOs/logs.
3. Run the implemented SchemaRunner inspection/conversion/resume/verify commands with private fixture manifests, explicit disposable source/target and recovery-only old keys. Exact CLI option syntax must be added with the tool implementation; never substitute a guessed command against an existing database.
4. Cover every case in contracts/conversion.md, including all 28 tables/assets, full field-level content coverage, WAL snapshot, failed key, partial/resumed copy and mismatched checkpoint. Compare exact financial state and prove the original is unchanged.
5. Verify old executable/schema-format refusal and compatible same-data restart with new accepted writes preserved; otherwise safe-stop recovery. Confirm no target encrypt/decrypt/keyring loading remains, including direct crypto and file artifacts outside Persistence.
Expected: no target application encryption dependencies; exact recoverable content/state; all retained security and uncertain-effect controls work.
## Final candidate
Use existing workflow/build/managed runtime probes only after implementation requires them, with deployment explicitly suppressed. Run the full affected SQLite/PostgreSQL regression, formatting/warning review and published Linux host tests at the exact candidate. Retain immutable evidence hashes privately as required and record redacted scope/results in this feature's qualification.md. Do not claim full-EF Native AOT support from the old catalog-only artifact. No staging/production conversion or deployment belongs to this guide without separate execution authorization.

## Implemented design tooling
```bash
dotnet tool restore
dotnet restore Platform.sln -r linux-x64 --locked-mode
dotnet ef dbcontext info --project src/Platform.Persistence/Platform.Persistence.csproj --context SqliteAkantiDbContext --no-build
dotnet ef dbcontext info --project src/Platform.Persistence/Platform.Persistence.csproj --context PostgresAkantiDbContext --no-build
```
Build the project first when needed. The design factories accept no runtime connection arguments and select only in-memory/credential-free design identities. These commands identify models; they do not perform adoption, conversion or deployment. Operator migration/adoption commands remain unimplemented at this checkpoint.

## Managed read-only schema description
```bash
dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --describe-ef-schema sqlite
dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --describe-ef-schema postgres
```
These commands only describe committed migration metadata and a deterministic script hash; design identities never connect to a target. SchemaRunner is managed because EF migration generation is incompatible with Native AOT. No existing-file adoption or data conversion is implemented by this command. Do not substitute it for schema, content or financial qualification.
