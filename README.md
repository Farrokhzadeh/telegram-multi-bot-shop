# Akanti (آکانتی)

Akanti is a multi-brand commerce backend for Telegram shops. Each brand has its own bot, customer memberships, wallets, orders, and payment settings. Brands can share a product catalog and stock pool. AccAbad (`acc-abad`) is the first brand Akanti will replace.

The staging backend uses managed C# and ASP.NET Core on .NET 10 with PostgreSQL. SQLite support is in progress and is not yet wired into the staging bot. It models payments, inventory, durable jobs, and delivery in a 28-table relational schema. The [admin API guide](docs/ADMIN_API.md) explains how to get the OpenAPI contract and start a frontend.

## Release status

Spec Kit records 96/96 tasks complete for the approved backend scope. The [staging image qualification](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/44502732355) passed 1,268 tests, Native AOT checks, and a one-brand synthetic canary. The staging Telegram bot can browse a synthetic product and use a test wallet. Akanti still needs the customer and operator workflows wired into the deployed runtime before it can replace AccAbad.

## Method 1 — Run or update the local demo

Run a local demo on a Linux x86-64 machine with the ASP.NET Core 10 runtime, Bash, `tar`, `sha256sum`, and `curl`. It needs no database, bot token, or payment credentials.

1. Open a successful [Offline backend qualification run](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/workflows/backend-ci.yml). Note its full commit SHA and download `staging-api-linux-x64-<commit>` from **Artifacts**. Unzip the GitHub download to get `Platform.Api-linux-x64.tar.gz`. Artifacts are retained for seven days, so use an available successful run that packages `0001_core.sql`.
2. Open a terminal in the directory containing that `.tar.gz` file and run the commands below. Replace the example SHA with the full SHA shown on the run.

   ```bash
   candidate=c75d4b1ed5e09d891ae485357de8f5de1bcc09c9
   demo=$(mktemp -d)
   tar -xzf Platform.Api-linux-x64.tar.gz -C "$demo" &&
     test "$(cat "$demo/SOURCE_COMMIT")" = "$candidate" &&
     (cd "$demo" && sha256sum --check SHA256SUMS) &&
     "$demo/Api/Platform.Api" --serve-qualification
   ```

3. Leave that terminal open. In a second terminal, run this check. A successful response includes `"mode":"synthetic-qualification"` and `"applicationReady":false`. The false value is expected in the demo.

   ```bash
   curl --fail --show-error \
     -H 'X-Qualification-Token: public-synthetic-probe' \
     http://127.0.0.1:18080/qualification
   ```

**Update or roll back.** Download a newer successful build, stop the current process with `Ctrl+C`, and repeat the steps with the new SHA. The command creates a fresh directory each time. Keep the previous archive and SHA so you can run that build again if needed.

## Method 2 — Deploy or update staging through GitHub Actions

**If staging is already configured, push to `main`.** The same workflow qualifies that commit, publishes the image to Docker Hub, and deploys it over SSH. Open the [Actions run](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/workflows/backend-ci.yml) and wait for **deploy-staging** to pass.

The staging server pulls the managed .NET image by digest and checks the API locally. The runtime image supplies .NET 10 for container roles. The host Telegram bridge uses a self-contained JIT publish. Where the Telegram shop is registered, it also starts the shop and waits for a successful Telegram poll. The server uses the GitHub-built image directly.

### One-time setup

1. Create a private Docker Hub repository named `akanti`. Its owner is the `DOCKERHUB_NAMESPACE`, usually your Docker Hub username. Give GitHub Actions a Docker Hub token with push access. Log in to Docker Hub on the server as the deployment account with a separate pull-only token.
2. In the Akanti repository create a GitHub Actions environment named `akanti-staging`. Set variables `DOCKERHUB_NAMESPACE`, `DEPLOY_HOST`, `DEPLOY_USER`, and optionally `DEPLOY_PORT` (defaults to 22). Set secrets `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`, `DEPLOY_SSH_KEY`, and `DEPLOY_KNOWN_HOSTS`. Use the independently verified host key in known-hosts format.
3. On the server, give that deployment account Docker access and an account-owned directory at `/var/lib/akanti-staging` with mode `0700`. The deployment needs Linux x86-64, Docker, Bash, `flock`, at least 3 GiB free disk, 1 GiB available RAM, and two CPUs. The server pulls the image and runs it.

### Redeploy, verify, or roll back

1. For a manual redeploy, open **Offline backend qualification**, select **Run workflow** on `main`, and enable **Publish to Docker Hub and deploy to staging**. Pull requests and pushes to implementation branches only run qualification and build checks.
2. A successful **deploy-staging** job reports the exact commit and image digest. For a registered shop it also reports that the staging Telegram bot is healthy. If a new poller fails, the deploy script attempts to restart the previous runtime and fails the job.
3. Keep the commit and digest for each successful run. To restore a previous compatible image, run [the deploy helper](ops/staging/deploy-dockerhub.sh) on the server as the deployment account:

   ```bash
   bash ops/staging/deploy-dockerhub.sh 'docker.io/<namespace>/akanti@sha256:<digest>' '<full-commit-sha>'
   ```

   The image must still be available in Docker Hub and contain the Telegram runtime if the shop is registered.

The workflow serves the isolated qualification API on a new installation. Telegram updates require an existing, separately provisioned test bot and shop database. The deployment uses protected `shop-root`, `shop-active` and `shop-schema` files in `/var/lib/akanti-staging` to identify that runtime. Updates retain the staging database, Telegram credentials, cursor and wallet data. If the bundled schema differs, apply the matching staging migration before deploying.

## AccAbad replacement acceptance

Use AccAbad’s customer and operator behavior as the reference. Run the supported flows through Akanti’s staging Telegram bot and workers, using isolated data and test transactions. Record the exact image and the evidence for each flow. The backend test suite and the wallet pilot cover narrower scopes.

| Workflow | Staging acceptance | AccAbad source |
|---|---|---|
| Start and navigation | Start, return to the menu, change language, and open the catalog, profile, orders, and support with the expected buttons. | [Command registration](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/management/commands/runbot.py) · [Keyboards](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py) |
| Catalog and checkout | Browse the migrated catalog, select a product and quantity, see the final price, and complete checkout. Replaying a confirmation creates one order and one debit. | [Customer menus](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py) · [Purchase UI](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py) |
| Wallet funding and payments | See the balance and transactions, submit a receipt or follow a test invoice, and verify that approval or replay credits the wallet once. | [Customer menus](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py) · [Deposit UI](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py) · [Payments](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/payments.py) |
| Delivery and order history | Receive a test item in Telegram, find the same order in history, and verify that restart or retry never sends it twice. Keep ambiguous delivery in review. | [Order delivery](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/order_delivery.py) · [Customer menus](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py) |
| Support and operator actions | Follow the supported customer help paths. Verify receipt decisions, inventory and order actions, and the corresponding customer notifications. | [Customer menus](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py) · [Admin flows](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py) · [Staff notifications](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/staff_notifications.py) |

## Production cutover

Serving real Telegram users requires a qualified Telegram receiver and job runtime. Before an operator can schedule AccAbad's cutover, the release packet must include the exact deployment artifact and schema, a final balance and paid-order reconciliation, verified database and media restores with key recovery, and a handoff plan for the bot, callbacks, and stock. The handoff needs one writer and a recovery plan for effects accepted after the switch. Each brand requires an explicit release decision. [Release gates](ops/RELEASE_GATES.md) · [Single-brand cutover and rollback runbook](ops/BRAND_CUTOVER_RUNBOOK.md) · [Staging canary evidence](ops/T081_STAGING_CANARY.md)

## Development and reference

Akanti uses `Telegram.Bot` for Telegram API requests. For the Python staging scripts, set `AKANTI_TELEGRAM_BOT_HOST` to the absolute path of the executable `Platform.BotHost` from the same build. The scripts pass Telegram requests to that executable over local pipes.


- Use the SDK pinned in [global.json](global.json). The [qualification workflow](.github/workflows/backend-ci.yml) specifies locked restore, formatting checks, disposable PostgreSQL tests, managed staging builds, and a separate Native AOT baseline comparison.
- Start with the [functional spec](specs/001-multibrand-redesign/spec.md), [architecture plan](specs/001-multibrand-redesign/plan.md), [schema contract](specs/001-multibrand-redesign/schema-contract.md), and [implementation tasks](specs/001-multibrand-redesign/tasks.md).
- Read the [operator handoff](specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md) for qualification history and the [supplier matrix](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md) for approved provider scope.
- The repository uses Spec Kit 1.2.0 with Bash scripts in `.specify/scripts/bash/`. Run `bash .specify/scripts/bash/check-prerequisites.sh --paths-only --json` to locate the active feature. See [AGENTS.md](AGENTS.md) for contribution and evidence rules.

The [legacy Django shop](https://github.com/Farrokhzadeh/AccAbad-Shop) remains the source for AccAbad migration evidence. The redesign was imported from its [`7fcc4a7` revision](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).
