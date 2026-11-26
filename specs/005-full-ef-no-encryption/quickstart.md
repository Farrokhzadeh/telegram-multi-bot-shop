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

## Full model validation candidate
Current EF command checks include all18 former SQLite assertions and related mutations; original SQL validation remains until runtime conversion closes. Original-baseline parity tests intentionally migrate only the first EF baseline, so historical encrypted constraints/indexes remain independently compared even after later schema lineages evolve. New accepted-state defaults write explicit replay0 and false flags exactly; invalid zeros hit their original constraints. These runtime-only unset markers do not change reviewed schema scripts or snapshots. Run the affected provider suites at the exact candidate; local SQLite success does not qualify PostgreSQL or a completed EF runtime.

For an isolated fresh plaintext SQLite schema, use `SHOP_TEST_ALLOW_DATABASE_CREATION=1 Platform.SchemaRunner --create-plain-qualification-sqlite`. The command creates a private temporary database and prints the schema digest and format version.

## Offline synthetic SQLite conversion

Use `Platform.SchemaRunner --describe-plain-ef-schema sqlite` or `Platform.SchemaRunner --describe-plain-ef-schema postgres` to print the v2 migration ID and candidate DDL and model digests without opening a database. Each provider has its own EF context and migration snapshot. Fresh and converted targets carry the v2 migration history. The historical v1 migration remains available for recovery.

Use a private Linux x64 directory named `akanti-ef-qualification-*` and set `SHOP_TEST_ALLOW_DATABASE_CREATION=1`. The commands below take absolute paths inside that directory. Use a disposable synthetic source and the keyring that encrypted it. A WAL-mode input requires its existing private database, WAL and shared-memory files. Freezing uses a read-only SQLite backup, then publishes a private read-only snapshot in rollback-journal format, with no sidecars.

```sh
Platform.SchemaRunner --freeze-plain-qualification-source --database "$work/input.db" --snapshot "$work/source.db"
Platform.SchemaRunner --inspect-plain-qualification-source --snapshot "$work/source.db" --inventory "$work/references.json"
Platform.SchemaRunner --prepare-plain-qualification-target --database "$work/target.db"
Platform.SchemaRunner --convert-plain-qualification --job "$work/job.json"
Platform.SchemaRunner --verify-plain-qualification --job "$work/job.json"
Platform.SchemaRunner --prepare-plain-runtime-copy --job "$work/job.json" --output "$work/runtime-example"
Platform.SchemaRunner --verify-plain-runtime-copy --bundle "$work/runtime-example" --receipt-sha256 "$receipt_digest"
```

The optional inventory is owner-only and contains raw references. Use the printed snapshot and target schema digests in `job.json`. The job must be owner-only and specify these fields:

| Field | Value |
|---|---|
| `version` | `1` |
| `source`, `destination` | Distinct database filenames in the job directory |
| `source_assets`, `destination_assets` | Distinct private subdirectories with owner-read-only input assets and conversion output |
| `snapshot_sha256`, `target_schema_sha256` | Printed digests for the frozen source and prepared target |
| `keys` | Private subdirectory `directory`, JobHost recovery-manifest filename `manifest`, `deployment` UUID, `revision`, and `preview` (private 32-byte key filename or null) |
| `brands` | Array of exact source brand objects with UUID `id` and numeric `bot_id` |
| `assets` | Array with one entry per file or archived capture. Use `[]` for an asset-free fixture. |

Each asset entry supplies `format`, `reference_sha256`, `stored_sha256`, `plaintext_sha256`, `brand_id`, `row_id`, `key_id` and `update_id`. Put null in unused scope fields.

| Format | Reference and scope | Input → output filename |
|---|---|---|
| `plain` | Reference digest from the inventory. Stored and plaintext digests must agree. | `<reference_sha256>.asset` → same |
| `payment-evidence` | Reference digest from the inventory, brand, payment row ID and recovery key ID. The plaintext digest must match the stored payment digest. | `<reference_sha256>.asset` → same |
| `customer-history` | Null reference digest, brand and member row ID | `<brand:N>-<member:N>.history` → `<brand:N>-<member:N>.history-v2` |
| `preview-capture` | Null reference digest and numeric Telegram update ID | `<update_id>.capture` → `<update_id>.capture-v2` |
| `preview-cursor` | Null reference digest and previously accepted next offset in `update_id` | `next-offset` → same, with identical source and target byte digests |

Database-generated order manifests stay in the database and are reconstructed to verify their original digest. Other enumerated references need file entries. Preview captures require a matching cursor entry. The asset directories must match the declared layout. Repeat the same convert command and byte-identical job to resume. Rows commit in private batches of 128, and individual asset files publish atomically. The staged marker blocks runtime access. Foreign-key enforcement is restored and the whole database is checked before the row checkpoint can complete. Verification rechecks rows and assets against the frozen originals.

Runtime-copy preparation creates a separate private directory after re-verifying the staged database and assets against the originals. It writes a WAL-mode database, puts files in the runtime layout where a local consumer owns the reference, and records a private file inventory. Take `receipt_digest` from the preparation output. Verification compares every package file to that receipt before first start. Ordinary referenced files remain under `assets/` with their identity in the receipt. The staged database retains its checkpoint. First SQLite activation requires a separate accepted-state operation. The historical PostgreSQL commerce deployment is retired from the current release path.

The isolated preview uses `state-v2` with an explicit private `next-offset` and stores exact JSON bytes in `*.capture-v2`. The synthetic history fixture uses private `*.history-v2` files and binding version 2. Retain older state roots and original files for recovery.

## Plaintext runtime candidate

The SQLite API, worker and bot accept the explicit synthetic mode `SHOP_SQLITE_STAGING=synthetic-acc-abad-v2`. The API and worker require a fresh runtime v2 SQLite schema in WAL mode at the existing private staging path. The API retains its local service key and administrator session checks. The worker and bot retain their Telegram identity files. The v2 API imports synthetic stock in its own transaction, and the v2 bot checks the authenticated API’s `X-Sqlite-Format` value before polling. An offline conversion database remains staged until separate publication work is authorized and completed.

The SQLite staging redeploy source accepts `--plaintext` only for an already accepted v2 runtime. It uses the existing database and supplier evidence directory, requires compatible prior containers for rollback, and leaves first activation to a separate step. It sources API and Telegram identity through the plaintext provisioning branch.

For PostgreSQL, `--create-plain-qualification-postgres --connection-file FILE --expected-ddl-sha256 DIGEST` creates v2 tables in an empty, owned, disposable qualification database. The digest comes from `--describe-plain-ef-schema postgres`. For synthetic provisioning, `--write-plain-postgres-schema --output FILE` writes a candidate SQL artifact in the private qualification directory and prints its file digest. The separate synthetic provisioner accepts the artifact and `--plaintext-sha256 FILE_DIGEST`. Its v2 roles use database credentials, TLS, webhook verification and the delivery probe. Provisioning and runtime qualification require separate execution.

The protected customer history export writes direct per-brand and per-member v2 files. The read-only staging history actor requires a v2 database and a matching private receipt. Historical encrypted snapshots are read by offline recovery.
