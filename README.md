# Akanti (آکانتی)

Akanti is a multi-brand commerce backend for Telegram shops. Each brand has its own bot, customer memberships, wallets, orders, and payment settings. Brands can share a product catalog and stock pool. AccAbad (`acc-abad`) is the first planned migration brand.

The backend uses C# and .NET 10 Native AOT with PostgreSQL. It models payments, inventory, durable jobs, and delivery in a 28-table relational schema. Operator workflows are implemented in the backend. A graphical admin panel is outside the current scope.

## Release status

The approved implementation scope is 96/96 complete. The [latest qualified build](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/44375416448) passed 1,266 tests, Native AOT smoke checks, and a one-brand synthetic canary. The API, bot, and job entry points currently support qualification only. The two paths below install and update disposable environments. Production deployment is blocked until the release gates pass. Live suppliers stay disabled.

## Method 1 — Start here

Run a local demo on a Linux x86-64 machine with Bash, `tar`, `sha256sum`, and `curl`. It needs no database, bot token, or payment credentials.

1. Open a successful [Offline backend qualification run](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/workflows/backend-ci.yml). Note its full commit SHA and download `staging-api-linux-x64-<commit>` from **Artifacts**. Unzip the GitHub download to get `Platform.Api-linux-x64.tar.gz`. Artifacts are retained for seven days, so use an available successful run that packages `0001_core.sql`.
2. Open a terminal in the directory containing that `.tar.gz` file and run the commands below. Replace the example SHA with the full SHA shown on the run.

   ```bash
   candidate=c75d4b1ed5e09d891ae485357de8f5de1bcc09c9
   demo=$(mktemp -d)
   tar -xzf Platform.Api-linux-x64.tar.gz -C "$demo" &&
     test "$(cat "$demo/SOURCE_COMMIT")" = "$candidate" &&
     (cd "$demo" && sha256sum --check SHA256SUMS) &&
     "$demo/Platform.Api" --serve-qualification
   ```

3. Leave that terminal open. In a second terminal, run this check. A successful response includes `"mode":"synthetic-qualification"` and `"applicationReady":false`. The false value is expected in the demo.

   ```bash
   curl --fail --show-error \
     -H 'X-Qualification-Token: public-synthetic-probe' \
     http://127.0.0.1:18080/qualification
   ```

**Update or roll back.** Download a newer successful build, stop the current process with `Ctrl+C`, and repeat the steps with the new SHA. The command creates a fresh directory each time. Keep the previous archive and SHA so you can run that build again if needed.

## Method 2 — Operator canary

Use a dedicated disposable Linux x86-64 machine or VM with Git, Docker Engine, Docker Compose v2 or later, Python 3, OpenSSL, and `sudo`. This runs the same one-brand catalog canary as CI against disposable PostgreSQL. It uses fixed Compose and Docker resource names, so give it a dedicated Docker daemon.

1. Clone this repository and, from the repository root, check out the exact SHA from the successful Actions run. Download and unzip its Linux x64 artifact as in Method 1. Keep `Platform.Api-linux-x64.tar.gz` in the repository root for the commands below. Compare the executable and schema SHA-256 values in the archive with those logged by the **Package native staging candidate** step of the run.
2. Verify the package and run the canary with the image digests used by CI. Replace the example SHA with the full SHA for your artifact.

   ```bash
   (
     set -euo pipefail
     candidate=c75d4b1ed5e09d891ae485357de8f5de1bcc09c9
     git switch --detach "$candidate"
     test "$(git rev-parse HEAD)" = "$candidate"
     tarball="$PWD/Platform.Api-linux-x64.tar.gz"
     bundle=$(mktemp -d)
     tar -xzf "$tarball" -C "$bundle"
     test "$(cat "$bundle/SOURCE_COMMIT")" = "$candidate"
     (cd "$bundle" && sha256sum --check SHA256SUMS)
     git show "$candidate:ops/migrations/0001_core.sql" | cmp - "$bundle/0001_core.sql"

     sdk=mcr.microsoft.com/dotnet/sdk@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
     postgres=postgres@sha256:bd5dfa494a5a259704365813dbfa5fa6b3e69be7348d43b547f540c730478583
     sudo docker pull --platform linux/amd64 "$sdk"
     sudo docker pull --platform linux/amd64 "$postgres"
     sudo docker tag "$postgres" postgres:18.6
     sudo python3 ops/staging/check_ci_canary.py "$bundle/Platform.Api" "$bundle/0001_core.sql"
   )
   ```

3. Confirm the script reports `PASS native staging catalog`. The canary checks catalog access, rejection of the wrong bot, independent bot stop, and replay detection after restart. It removes its disposable services and volumes at teardown.

**Update or roll back.** Use a clean disposable VM for the next build, check out the new SHA, and repeat the canary with the new executable and its matching SQL. Retain the prior verified archive and hashes to rerun the previous canary. A production rollback after accepting payments or orders requires preserving and reconciling those effects.

## Production deployment and updates

Both paths need a qualified production runtime before they can serve real customers. Before an operator can schedule AccAbad's cutover, the release packet must include the exact deployment artifact and schema, a final balance and paid-order reconciliation, verified database and media restores with key recovery, and a handoff plan for the bot, callbacks, and stock. The handoff needs one writer and a recovery plan for effects accepted after the switch. Each brand requires an explicit release decision. [Release gates](ops/RELEASE_GATES.md) · [Single-brand cutover and rollback runbook](ops/BRAND_CUTOVER_RUNBOOK.md) · [Staging canary evidence](ops/T081_STAGING_CANARY.md)

## Development and reference

- Use the SDK pinned in [global.json](global.json). The [qualification workflow](.github/workflows/backend-ci.yml) specifies locked restore, formatting checks, disposable PostgreSQL tests, and Native AOT builds.
- Start with the [functional spec](specs/001-multibrand-redesign/spec.md), [architecture plan](specs/001-multibrand-redesign/plan.md), [schema contract](specs/001-multibrand-redesign/schema-contract.md), and [implementation tasks](specs/001-multibrand-redesign/tasks.md).
- Read the [operator handoff](specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md) for qualification history and the [supplier matrix](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md) for approved provider scope.
- The repository uses Spec Kit 1.2.0 with Bash scripts in `.specify/scripts/bash/`. Run `bash .specify/scripts/bash/check-prerequisites.sh --paths-only --json` to locate the active feature. See [AGENTS.md](AGENTS.md) for contribution and evidence rules.

The [legacy Django shop](https://github.com/Farrokhzadeh/AccAbad-Shop) remains the source for AccAbad migration evidence. The redesign was imported from its [`7fcc4a7` revision](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).
