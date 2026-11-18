# Akanti (آکانتی)

Akanti is a multi-brand commerce backend for Telegram shops. Each brand has its own bot, customer memberships, wallets, orders, and payment settings. Brands can share a product catalog and stock pool. AccAbad (`acc-abad`) is the first planned migration brand.

The backend uses C# and .NET 10 Native AOT with PostgreSQL. It models payments, inventory, durable jobs, and delivery in a 28-table relational schema. Operator workflows are implemented in the backend. A graphical admin panel is outside the current scope.

## Release status

The approved implementation scope is 96/96 complete. The [latest qualified build](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/44375416448) passed 1,266 tests, Native AOT smoke checks, and a one-brand synthetic canary. The API, bot, and job entry points currently support qualification only. The two paths below run the qualification build locally or deploy it to a staging server. Production deployment is blocked until the release gates pass. Live suppliers stay disabled.

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

## Method 2 — Deploy or update staging through GitHub Actions

Akanti's staging workflow follows the legacy release sequence. GitHub tests the selected revision, packages the executable and matching SQL, pushes the image to Docker Hub, and connects by SSH. The server pulls by the image digest. It checks the new container before retiring the previous Akanti staging container.

### One-time setup

1. Create a private Docker Hub repository named `akanti`. Give GitHub Actions a Docker Hub token with push access. Log in to Docker Hub on the server as the deployment account with a separate pull-only token.
2. In the Akanti repository create a GitHub Actions environment named `akanti-staging`. Set variables `DOCKERHUB_NAMESPACE`, `DEPLOY_HOST`, `DEPLOY_USER`, and optionally `DEPLOY_PORT` (defaults to 22). Set secrets `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`, `DEPLOY_SSH_KEY`, and `DEPLOY_KNOWN_HOSTS`. Use the independently verified host key in known-hosts format.
3. On the server, give that deployment account Docker access and an account-owned directory at `/var/lib/akanti-staging` with mode `0700`. The deployment needs Linux x86-64, Docker, Bash, `flock`, at least 3 GiB free disk, 1 GiB available RAM, and two CPUs. The server pulls the image and runs it.

### Each deploy or update

1. Open [Offline backend qualification](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/workflows/backend-ci.yml). Select **Run workflow** on `main` and enable **Publish to Docker Hub and deploy to staging**.
2. Wait for **deploy-staging** to report the commit and image digest. The job runs the new container's local health probe. A failed replacement leaves the previous healthy container available.
3. Record the digest and commit from each successful run. To roll back, invoke [the same deploy helper](ops/staging/deploy-dockerhub.sh) on the server with the previous `docker.io/<namespace>/akanti@sha256:<digest>` and its full commit SHA. The older image must still be available in Docker Hub.

The current deployment runs Akanti's qualification API in an isolated container. It has no Telegram update receiver, database connection, or live provider credentials. Leave the legacy staging bot serving until Akanti has a qualified Telegram staging runtime and an exclusive handoff.

## Live staging and production

Serving real Telegram users requires a qualified Telegram receiver and job runtime. Before an operator can schedule AccAbad's cutover, the release packet must include the exact deployment artifact and schema, a final balance and paid-order reconciliation, verified database and media restores with key recovery, and a handoff plan for the bot, callbacks, and stock. The handoff needs one writer and a recovery plan for effects accepted after the switch. Each brand requires an explicit release decision. [Release gates](ops/RELEASE_GATES.md) · [Single-brand cutover and rollback runbook](ops/BRAND_CUTOVER_RUNBOOK.md) · [Staging canary evidence](ops/T081_STAGING_CANARY.md)

## Development and reference

- Use the SDK pinned in [global.json](global.json). The [qualification workflow](.github/workflows/backend-ci.yml) specifies locked restore, formatting checks, disposable PostgreSQL tests, and Native AOT builds.
- Start with the [functional spec](specs/001-multibrand-redesign/spec.md), [architecture plan](specs/001-multibrand-redesign/plan.md), [schema contract](specs/001-multibrand-redesign/schema-contract.md), and [implementation tasks](specs/001-multibrand-redesign/tasks.md).
- Read the [operator handoff](specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md) for qualification history and the [supplier matrix](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md) for approved provider scope.
- The repository uses Spec Kit 1.2.0 with Bash scripts in `.specify/scripts/bash/`. Run `bash .specify/scripts/bash/check-prerequisites.sh --paths-only --json` to locate the active feature. See [AGENTS.md](AGENTS.md) for contribution and evidence rules.

The [legacy Django shop](https://github.com/Farrokhzadeh/AccAbad-Shop) remains the source for AccAbad migration evidence. The redesign was imported from its [`7fcc4a7` revision](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).
