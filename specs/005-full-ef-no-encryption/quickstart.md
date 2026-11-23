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
Build the project first when needed. The design factories accept no runtime connection arguments and select only in-memory/credential-free design identities. These commands identify models; they do not perform adoption, conversion or deployment. Explicit synthetic creation/adoption commands are listed below; protected-data conversion is still unimplemented.

## Managed read-only schema description
```bash
dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --describe-ef-schema sqlite
dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --describe-ef-schema postgres
```
These commands only describe committed migration metadata and a deterministic script hash; design identities never connect to a target. SchemaRunner is managed because EF migration generation is incompatible with Native AOT. No existing-file adoption or data conversion is implemented by this command. Do not substitute it for schema, content or financial qualification.

## Isolated schema operators
First review the committed migration and obtain its script_sha256 through --describe-ef-schema. Set REVIEWED_SQLITE_SHA256 or REVIEWED_POSTGRES_SHA256 to that reviewed value; never accept a hash from an unreviewed executable.
```bash
SHOP_TEST_ALLOW_DATABASE_CREATION=1 dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --create-ef-qualification-sqlite --expected-script-sha256 "$REVIEWED_SQLITE_SHA256"
SHOP_TEST_ALLOW_DATABASE_CREATION=1 dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --adopt-ef-qualification-sqlite --database "$QUALIFICATION_DATABASE" --expected-script-sha256 "$REVIEWED_SQLITE_SHA256"
SHOP_TEST_ALLOW_DATABASE_CREATION=1 dotnet run --project src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -- --create-ef-qualification-postgres --connection-file "$PRIVATE_QUALIFICATION_CONNECTION_FILE" --expected-script-sha256 "$REVIEWED_POSTGRES_SHA256"
```
Linux x64 only. SQLite creation returns an owner-private temporary akanti-ef-qualification-* directory and shop.db path. Adoption requires that private temporary path, WAL, exact business schema/guard fingerprint, compatible migration metadata and existing financial integrity. Drift is rejected without stamping. It does not normalize a raw legacy SQL schema. PostgreSQL requires the existing loopback shop_test_runner identity, an owned empty shop_qualification_<32hex> database and exact pinned server version; it never creates a server/database or adopts nonempty PostgreSQL data. Connection files use existing private-file checks. These tools are for disposable synthetic qualification only; inspect retained failed targets privately and clean up only their known temporary directories. No production/staging paths or protected data conversion are supported here.

## Rebuild boundary
Business rebuild scripts restore the complete reviewed SQLite trigger set because guards can reference other tables being renamed. Use only an offline exclusive disposable target with verified schema/artifact identity. No existing-file upgrade CLI is implemented; this is not an online migration path. Tests cover four business tables,27 exact triggers,95 deferred references, retained ledger content and immutable-delete rejection. Future plaintext-schema migrations must review/version the guard definitions for that representation.

## PostgreSQL EF provider wiring
EF affected-scope queries bind UUID collections. Slim PostgreSQL data sources used by converted paths must enable arrays before Build; synthetic target/proxy builders now do so. Native EF sessions use NonRetryingExecutionStrategy because the existing transaction boundary owns retry/outcome classification. Current unopened runtime paths and source-only recovery readers are not qualified by the synthetic builder change; complete their wiring with the corresponding conversion tasks.
