# T011 versioned configuration and secret files

Owner-authorized offline foundation task, stacked on qualified T010. Cipher envelopes (T012), actual credentials/source keys, live integrations, customer handlers and deployment remain later work. Normal host startup still exits 78 and application readiness remains false.

## Configuration contract

Call `SecretFiles.Load(directory, manifestFile, grant)` explicitly with an independently trusted `SecretLoadGrant`. The manifest never grants its own service, deployment, revision, brands or inventory access. All must match exactly; partial/missing/extra scope fails before any snapshot is returned. No environment, .env, database, inline key, fallback provider or file watcher supplies key bytes.

[bot-manifest.example.json](compose/bot-manifest.example.json) shows public synthetic metadata only; replace its fake identity as part of a reviewed deployment bundle. Configuration revision is the runtime publication revision shared with T010 bindings, not the database brand policy version. Publish matching grants/bindings/key references together.

Schema 1 manifests contain `schemaVersion`, positive `revision`, `deploymentId`, exact service (`Api`, `BotHost`, `JobHost`), `brands` and `inventory` (object or null). Each brand entry has `brandId`, `currentKeyId`, `keys`; inventory has `currentKeyId`, `keys`. Each key reference has `id` and `file` only. Unknown/duplicate properties, IDs, brands, current IDs and file references fail. Manifest maximum: 64 KiB, depth 8, 32 brand rings, eight keys per ring. These are bounded parser limits, not a change to the approved three-brand business scope.

A key file is exactly 32 binary bytes with no newline/base64 wrapper. Identical material under distinct IDs or scopes is rejected within a service snapshot. Brand keys and the inventory key must be independently generated; authorized services serving the same brand receive that brand's same key material through separately permissioned files. Key IDs and paths are metadata; raw bytes never belong in templates, .env, database or logs.

| Service | Authorized brand rings | Inventory ring |
|---|---|---|
| BotHost | Exactly its one brand | Always denied |
| Api | Explicit bootstrap-authorized brands | Denied |
| JobHost | Explicit bootstrap-authorized brands | Only with independent explicit inventory grant |
| SchemaRunner | None | None |

This is process/mount and loader authorization. It does not sandbox trusted code with access to key memory. Future cipher code uses synchronous scoped callbacks; callbacks must not log/copy/return key material or perform external effects. Brand operations additionally require a trusted T010 BrandContext with matching brand, deployment and configuration revision. Inventory operations have their own API and do not accept a bot context as permission. Admin authentication and audited privileged decryption are later tasks.

The snapshot is immutable. Its current ID is used for future encryption; retained old IDs support explicit historical decryption without trying unrelated keys. A new authorized revision must load completely before replacing the old snapshot. There is no automatic reload/fallback; orchestration must drain old users and dispose old snapshots. Owned key arrays are zeroed on disposal or failed load. Returning no raw-key property and a fixed redacted ToString reduces accidental serialization/logging; safe exceptions retain only an enum, never original path/payload/inner exception. Kernel buffers, arbitrary trusted callback copies and privileged host access are outside managed zeroization guarantees.

## Linux file and Compose boundary

The reader is explicitly Linux x64, matching the qualified runtime. Source-generated libc open/openat calls open every directory component and final file without following symlinks. Descriptor-relative reads prevent path traversal; final opens are nonblocking so a FIFO cannot stall startup. Open-handle attributes, length and permission checks reject unsupported/special files, empty/oversized input and detected changes. Files must be 0400 or 0440; secret directories must have no group/other write. The operator owns immutable source files, parent directories and atomic revision publication. This is not protection against a privileged host replacing mounts or modifying the running process.

`ops/compose/backend.compose.yaml` is a disabled configuration contract. It defines per-service file-backed secret references; each bot has only its own ring, Api has no inventory mount, and only the explicitly privileged JobHost receives inventory. All services use separate nonroot UIDs, read-only root filesystems, no network/ports, dropped capabilities and no-new-privileges. Image digests and protected paths are required inputs; no image is published and no service starts in this task. Current hosts remain fail-closed and do not automatically load these files until later runtime wiring.

Docker Compose file-backed secrets preserve source ownership/mode; uid/gid/mode declarations do not establish actual host permissions. Provision separately permissioned 0400 files for each receiving UID, or reviewed 0440 group access, outside the repository/images and database backups. The loader checks actual mounted permissions. Read-only mount behavior is a Compose contract here, not a completed container/host release test. Rotation adds explicit retained-file references/mounts with reviewed revision changes; nothing mounts the entire host secret directory. Protect and test key backups separately from database backups; do not remove old keys until historical/restore dependencies are proved.

## Qualification

Qualified in CI run 41218689562 at source commit f21a0f84a2fa161e836984e0787c107413528b8a, tree 0fd36b070151adee1fa05cd8ab688e8430a0e269. Unit tests use generated disposable files to exercise identity/revision/scope, old/current IDs, unsafe paths/modes/symlinks/FIFOs, malformed/oversized/unknown/duplicate metadata, missing keys, key-size/reuse and stable snapshots. The published Native AOT probe reads real synthetic files through the libc/BCL path and verifies separate inventory authorization. Compose CI renders configuration with generated temporary paths and an invalid non-pullable image reference; it validates topology without pulling images or starting containers. No real secret path is read.

Reproduce with the pinned solution restore/build, all three test suites and `bash ops/ci/native-smoke.sh`; additionally run `python3 ops/ci/check-secret-compose.py` where Docker Compose is installed. No dependency version, schema, monetary/transaction semantics or business flow changes. SQL SHA-256 remains `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`.

Primary references: [Docker Compose service secrets](https://docs.docker.com/reference/compose-file/services/#secrets), [.NET open-handle permissions](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.getunixfilemode?view=net-10.0) and [Linux no-follow/nonblocking open](https://man7.org/linux/man-pages/man2/open.2.html). E1 remains governance-only closed; E4 is limited to actual recorded probes; E2/E3/E5/E6 remain unqualified.

## Observed evidence — 2026-10-31

[CI run 41218689562](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41218689562), job `124588833138`, passed all steps: 171 tests (95 unit, 46 PostgreSQL integration, 30 contract), zero failed/skipped, zero build warnings/errors, locked restore, audit, Gitleaks, formatting, Compose render validation and all five Linux Native AOT executables. Native secret loading verified exact source bytes, retained old IDs, revision rejection, independent inventory permission and symlink rejection. No real keys or live operations occurred. T011 is complete; T012 is next.

| Executable | SHA-256 |
|---|---|
| Api | `e9ad0c0f833081841b3180da9289b0d5cb8bf2888d0feedb6f7a8d6d86798e26` |
| BotHost | `80606d38209d39aa132e7074395eec838774d0bc8abba24344e5f77a07b5ab2d` |
| JobHost | `1442532d8eaf96c26a708c345f4b3a55aabe91468ca757c931ea097066a04029` |
| SchemaRunner | `21f4e4d831cd0377a70c389f9573913966e75ee547e71b8bc9b34081bf0ad193` |
| AotSmoke | `93f30197acb3647b5f9afd7ffe8f7e7833da00f9ac638dbdd334e6081b8b3ca9` |

The owner subsequently authorized merging all completed work into main and continuing there with task-batched commits, without repeated confirmation for small follow-up tasks. This does not authorize deployment or real integrations.
