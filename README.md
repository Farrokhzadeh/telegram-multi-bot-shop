# Telegram Multi-Bot Shop

AccAbad multi-brand redesign: the specification home for three independently branded Telegram shops sharing a commerce backend, product catalog and stock pool.

**Status: specification review only.** No application implementation, executable database migrations, deployment or live integrations are included.

## Start here

- [Conversation handoff](specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md)
- [Functional specification](specs/001-multibrand-redesign/spec.md)
- [28-table database review](specs/001-multibrand-redesign/database-table-review.md)
- [Logical data model](specs/001-multibrand-redesign/data-model.md)
- [Supplier and preorder review](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md)
- [Architecture plan](specs/001-multibrand-redesign/plan.md)
- [Future implementation tasks](specs/001-multibrand-redesign/tasks.md)

## Current approved decisions

- C#/.NET 10 Native AOT backend and PostgreSQL; no graphical admin UI in scope.
- 28 approved relational table contracts, no JSON/JSONB/EAV business-state columns.
- Brand-isolated users, USD wallets, orders and payments; shared catalog/stock.
- Automated supplier products: ready-to-deliver accounts, keys and codes only, without buyer fulfillment inputs; activation/upgrade/custom supplier flows are excluded.
- Multiple supplier mappings per product; one account per supplier and one product-level active selection shared across all brands.
- Supplier choice binds at accepted payment. An unavailable source preserves payment and the paid obligation for administrator review, without automatic fallback or compensation.
- Paid local preorders have no fulfillment time cap or admin-defined availability dates/ETAs; they wait for complete stock or an explicit admin resolution, with no date-change notifications.
- Prefer editing a saved, validated order message for credential delivery; uncertain new sends require explicit recovery without blind replacement messages.
- Deliver the complete requested order quantity. Local preorder FIFO waits for the whole oldest order; incomplete supplier acquisitions remain protected internally with zero customer release.
- Nonfinancial bot activity events have 180-day rolling retention; financial, order, support and admin-audit history follow separate policies.
- Paid-preorder cancellation requests go through existing support tickets for manual staff review; support activity does not automatically cancel or pause orders. Once cancellation is approved, the admin handles repayment outside the shop; cancellation creates no wallet credit or external payout record. All held coupon uses return automatically with approved cancellation, keeping their original expiry and current eligibility.
- Customers may opt into a one-time, brand-scoped back-in-stock alert; no stock is reserved and no order is placed. Durable opt-ins use approved table 28 `product_stock_subscriptions`.
- Use local stock first and allow mixed fulfillment, delivering the complete order together. Refresh supplier stock/cost/eligibility before creating an order only when remote units are needed; fully covered local-only checkout works even if the supplier is unavailable.
- Published products remain visible as **Out of stock** when eligible local stock and the selected supplier cannot cover a purchase; unavailable quantities are blocked.
- Supplier cost increases up to and including 10% of the acceptance-time supplier cost are allowed; larger increases require admin review. Customer prices stay fixed.
- Valid 100%-off supplier orders procure automatically when checkout commits; coupon uses stay held until complete fulfillment, with no wallet debit or external invoice.
- Verified supplier bonus items beyond the ordered quantity go to the admin, with protected content access; customers receive their ordered quantity only.
- Confirmed supplier failures notify the private shared staff chat; admins may safely retry or resolve with a same-brand wallet credit. Bank-paid credits use an admin-entered USD amount.
- Admins may pause a paid local preorder without letting later local orders bypass it; verified staff skip channel checks and may disable enforcement per brand.
- Use the order ID directly for customers. Each bot has its own public admin contact; closed restock subscriptions are retained for 180 days.
- External refunds and their records stay outside the platform.

The feature contains **84 functional requirements, 53 success criteria and 96 planned tasks**. Repository setup T001 is complete; the remaining 95 tasks are unchecked and application implementation remains deferred. Remaining schema/provider/migration decisions are listed in the handoff.

## Review deliverables

- [Consistency findings and remaining evidence gates](specs/001-multibrand-redesign/consistency-review.md)
- [Canonical 28-table field and constraint contract](specs/001-multibrand-redesign/schema-contract.md)
- [84-requirement / 53-criterion task traceability](specs/001-multibrand-redesign/requirements-traceability.md)
- [Applied 2.0.0 shared-core amendment and historical approval source](specs/001-multibrand-redesign/constitution-alignment-proposal.md)
- [2.0.0 owner ratification and application status](specs/001-multibrand-redesign/constitution-ratification.md)

Constitution 3.0.0 is active, including the applied shared-core amendment and owner-approved saved-order-message delivery recovery. The 2026-10-31 alignment reanalysis passed; E1 is closed for governance only. E2–E6 remain stage-specific proof requirements. Documentation does not claim schema execution, provider/runtime qualification or implementation/release authorization.

## Spec-kit

Spec Kit **1.2.0 uses Bash/Linux commands** under `.specify/scripts/bash/`; all ten
generic command skills and the integration settings select the shell variant. The six
scripts are based on [upstream commit 4ca46a0](https://github.com/github/spec-kit/tree/4ca46a0706a662b1edc07086ef16f021820c2db0/scripts/bash).
The upstream MIT license is included alongside them. Interpreter discovery in `common.sh` and
`create-new-feature.sh` uses captured input instead of `/dev/fd` process substitution, so the
full resolver also runs in restricted Linux workspaces. Existing templates, feature selection,
business requirements and application task statuses are preserved.

Use Bash and Python 3; preset/extension manifest composition requires PyYAML. `jq` is optional.
Set `TMPDIR` to a writable temporary directory in environments without `/tmp`.
The active feature is selected by `.specify/feature.json`, independently of the Git branch.
No PowerShell runtime or Spec Kit CLI installation is needed for these commands.

Run from the repository root:

```bash
bash .specify/scripts/bash/check-prerequisites.sh --paths-only --json
bash .specify/scripts/bash/resolve-template.sh constitution-template --json
bash .specify/scripts/bash/check-prerequisites.sh --json --require-spec --require-tasks --include-tasks
python3 .specify/tests/test_linux_tooling.py
```

Template resolution preserves project overrides, ordered preset composition, extensions and
the core fallback. A resolver failure still blocks the dependent command. Path inspection
does not rewrite the feature selector, and setup preserves existing plan/task content.
The tooling tests use disposable fixtures; they do not execute application tests or live operations.

Continue specification clarification/review. The presence of implementation commands and a future task backlog does not authorize running implementation or deployment workflows.

## Provenance and scope

Imported on 2026-10-29 from `Farrokhzadeh/AccAbad-Shop`, branch `feature/001-csharp-native-aot`, commit [`7fcc4a7`](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).

The original repository remains the source for legacy Django code, historical migrations and pinned supplier evidence. This repository contains the current redesign documents and spec-kit support, plus repository routing/provenance metadata. Legacy application/deployment code, environment files, databases and credentials were not imported.

The active [constitution](.specify/memory/constitution.md) is version 3.0.0. Its amendment records preserve the original 2026-10-15 adoption, the pinned 2.0.0 shared-core approval and the 2026-10-31 owner-authorized delivery recovery amendment. Governance alignment does not authorize implementation or release.

The user redirected this transfer to `telegram-multi-bot-shop`; `door-bell` was not modified.
