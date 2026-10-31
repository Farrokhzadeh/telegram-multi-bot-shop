# Canonical schema and explicit operator qualification

**Scope:** T008, authorized by the owner's "do next" on 2026-10-31. This adds the 28-table forward baseline and tests it with synthetic data. It does not import historical data, apply a staging/production migration, enable a customer feature or close E2–E6.

**Later batch:** T009 transaction/money helpers are now qualified in [TRANSACTION_QUALIFICATION.md](TRANSACTION_QUALIFICATION.md). Remaining-task statements and test/executable identities below describe the cited T008 source snapshot.

## What is implemented

`migrations/0001_core.sql` declares all 28 tables before attaching cyclic foreign keys and static guards. It uses UUID entity keys, membership-derived ownership, bounded scalar fields, finite exact NUMERIC without a fixed scale, finite timestamps, typed origins and explicit controlled vocabularies. It adds no schema-history table, JSON/jsonb/array/EAV state, supplier-account table, refund record or stored order total.

The baseline covers the canonical foreign keys, uniqueness, row shapes and static cross-row checks. These include same-member support/flow links, order/listing scope, immutable quotes/accepted times, exact wallet posting chains, held coupon grant/history, supplier source/cost snapshots, disjoint customer/bonus item identity, whole preorder allocation, complete manifests, saved order-message targets, typed jobs, customer/staff recipient separation and narrow elapsed-time retention deletion.

It is not an implementation of authenticated admin APIs, quote calculations, verified payment callbacks, procurement/delivery workers, new ciphertext envelopes, campaign sending or retention scheduling. Later story tasks qualify/evolve these existing tables and implement the services; they must not create duplicate tables. In particular, T009–T012 and T014–T017 remain unfinished. Database ciphertext fields do not prove the unimplemented keyring/context envelope is correct.

## Runner protocol

The separate `src/Platform.SchemaRunner/` executable is the only operator entry point. API, bot and job startup do not call it. With no explicit operation it exits 78.

1. Read an exact eight-field external manifest and an owner-private connection file. Reject missing/duplicate/unknown fields, symlink inputs and group/other file access. Connection credentials never appear in arguments, receipts or exception output.
2. Before connecting, require the reviewed embedded SQL digest and the exact disposable qualification identity. This version supports `environment=qualification`, loopback, role `shop_test_runner`, `shop_qualification_` plus 32 hex characters, and PostgreSQL `180006` only. The existing disposable-creation flag must be explicitly enabled. Staging/production support and release authority remain future work under the release gates.
3. Check the connected database/user/owner/server version, begin one transaction and acquire a database-local migration advisory lock. Initial application requires an empty destination and no existing `shop` schema.
4. Execute the embedded reviewed SQL, then verify the exact table registry, forbidden types, validated constraints and enabled guards. Hash the ordered columns/defaults, constraints, indexes, user and FK trigger state, function definitions, domains and table/schema privileges.
5. Atomically write a schema comment containing revision, SQL digest and catalog digest, then commit. There is no history/checkpoint table. A failed transaction leaves no baseline or revision marker.
6. On repeat, recompute the catalog and compare the existing marker. Return unchanged only on an exact match. Reject partial/unmarked schemas and drift; never repair or adopt them silently. Write a new owner-private external receipt with `CreateNew`; retain receipts through the protected release/import evidence process.

The marker is integrity metadata controlled by the migration owner, not authentication against a malicious database superuser. Privileged DDL and later role grants require a reviewed migration/catalog transition. A receipt write failure after commit leaves the schema applied; retry verification with a new receipt path. No automatic reversal or repeated DDL is attempted.

## Reproduce in the disposable harness

Use the pinned SDK and CI PostgreSQL identity from `RELEASE_GATES.md`:

```bash
dotnet restore Platform.sln -r linux-x64 --locked-mode
dotnet build Platform.sln -c Release --no-restore
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --no-restore
bash ops/ci/native-smoke.sh
```

`RelationalSchemaTests` creates/drops only its owned random databases. Native smoke also publishes and invokes the operator executable, using private temporary files and checking the external receipt. Other hosts retain their qualification-only behavior and not-ready business status.

For an explicitly prepared disposable destination, the operator syntax is:

```bash
artifacts/native/SchemaRunner/Platform.SchemaRunner --apply-schema \
  --manifest /absolute/private/qualification.manifest \
  --connection-file /absolute/private/qualification.connection \
  --receipt /absolute/private/new-receipt.manifest
```

Use `migrations/0001_core.manifest.example` as a rejected-until-filled template. Obtain the SQL hash from the reviewed build; do not put a password in the manifest. The tool does not create databases, obtain credentials, deploy services or apply schemas on ordinary host startup.

## Registry and compatibility boundaries

Baseline activity registry v1 permits the following pairs. Context must also belong to the original membership/listing. Codes carry no raw message, callback, payment or credential payload.

| Event type | Actions | Required context |
|---|---|---|
| navigation | start, catalog_open, product_open | Product for product_open |
| quantity | quantity_enter | Product and positive selected quantity |
| checkout | checkout_open | Product |
| support | support_open, support_reply | Ticket for support_reply |
| subscription | subscribe, unsubscribe | Product |
| discount | discounts_open | Membership |
| policy | policy_check | Membership |

The source registry is command/callback/flow/system; outcomes are accepted/rejected/unavailable/blocked/cancelled/duplicate. This v1 structural registry does not implement event collection or access/retention workers.

- Provider codes identify source registries, not enabled adapters or remote contract proof. `synthetic` is reserved for disposable tests. Real account configuration remains outside these tables and unimplemented under T011.
- Flow, activity, audit, job and delivery codes are finite baseline vocabularies. Changes require explicit versioned DDL and service tests; no unknown code becomes executable work. T087 still owns the full activity service/privacy/access registry and read/cleanup implementation.
- New quote currency/method and funding XOR rules are checked; real quote satisfaction, denomination limits and cryptographic evidence remain provider/service qualification.
- Historical exceptions require the separately provisioned `shop_legacy_import` role and a matching protected mapping. This batch does not create that role or import source rows. Real precision/cardinality/aliases/partial deliveries and archive obligations remain E3 work.
- Staff group purpose/shape is checked, while the actual trusted group ID, current independent admin authentication and enqueue/send authorization remain T011/T016/T049/E5 work. A stored ID grants no privilege.
- Retention guards use 4,320 elapsed hours and strict older-than comparison. They do not grant maintenance access or schedule deletion; active consent and unresolved dependencies remain protected.

## Observed evidence

The local PostgreSQL 18.6 PGlite 0.5.14 engine has loaded the full baseline and exercised the catalog, ownership, finite-number, whole-manifest, coupon-history, wallet-chain, supplier-acquisition, preorder and cutoff checks. This is a local SQL check, not Linux x64/.NET execution evidence.

Full qualification passed in [CI run 41207382338](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41207382338), job `124555133012`, at commit [`774043e16be60730905ea488670ab57f7318daa2`](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/commit/774043e16be60730905ea488670ab57f7318daa2), tree `f3f7970c66dbf08168250f99113d28f66d581e1e`. All steps completed successfully. T008 is complete for this offline forward-schema scope; E3 real-source compatibility and live operational gates remain open.

| Check | Observed result |
|---|---|
| Build / dependencies | SDK 10.0.403, 16 projects, locked restore, zero build warnings/errors, formatting and NuGet audit passed |
| Security | Default Gitleaks 8.30.1 rules passed with existing narrow public-fixture exceptions |
| Database | PostgreSQL 18.6 / `180006`; image digest `sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4` |
| Tests | 52 passed, zero failed/skipped: 15 unit, 34 PostgreSQL integration and 3 transport contract |
| Schema failure probes | Exact registry/no forbidden columns; wrong manifest and partial schema; rollback; concurrent operators; repeat/drift checks including disabled guards and added types |
| Integrity probes | Membership/brand links, payment XOR/assets/uniqueness, finite/exact money, matching immutable wallet postings, complete manifests and saved anchors, typed jobs, immutable coupon grants/owner edits, supplier caps/unknowns/item identity, FIFO holds/whole allocation, retention cutoff and terminal compensation/replay |
| Native execution | API, bot, job, SchemaRunner and AotSmoke publish and run on Linux x64; ordinary hosts/operator reject startup without explicit configuration; guarded API remains not ready |
| Native operator | Applies/verifies the 28-table schema through the native dependency probe, invokes the separate native operator with protected files and validates its external receipt |

The reviewed SQL SHA-256 is `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`. The following hashes identify the qualification binaries from the cited commit, not a production release bundle. Later documentation commits do not change which source revision these hashes identify.

| Executable | SHA-256 |
|---|---|
| `Platform.Api` | `5b5bade54cab1a5f011faada9c3b18eef44f8204cf479ceb5b2c0e5db9b7d93e` |
| `Platform.BotHost` | `95f6e7268ce95c3cbafe9d9ec41659cf82daf60780264cb1b31cfbec3e26bcce` |
| `Platform.JobHost` | `9df186b28082fe59fd47b5403f50da21235d1831e05b33df3b88fda5c4946e49` |
| `Platform.SchemaRunner` | `acf5f96706e413d191737a138985546b1851ec6df63cee4106f89b96930d5e14` |
| `Platform.AotSmoke` | `34ab3ac75644ede81593ab0bae1437f711d44b49e9705d753a509999e70b3d3c` |
