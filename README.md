# Telegram Multi-Bot Shop

AccAbad multi-brand redesign: the specification home for three independently branded Telegram shops sharing a commerce backend, product catalog and stock pool.

**Status: specification review only.** No application implementation, executable database migrations, deployment or live integrations are included.

## Start here

- [Conversation handoff](specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md)
- [Functional specification](specs/001-multibrand-redesign/spec.md)
- [27-table database review](specs/001-multibrand-redesign/database-table-review.md)
- [Logical data model](specs/001-multibrand-redesign/data-model.md)
- [Supplier and preorder review](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md)
- [Architecture plan](specs/001-multibrand-redesign/plan.md)
- [Future implementation tasks](specs/001-multibrand-redesign/tasks.md)

## Current approved decisions

- C#/.NET 10 Native AOT backend and PostgreSQL; no graphical admin UI in scope.
- 27 proposed relational tables, no JSON/JSONB/EAV business-state columns.
- Brand-isolated users, USD wallets, orders and payments; shared catalog/stock.
- Automated supplier products: ready-to-deliver accounts, keys and codes only, without buyer fulfillment inputs; activation/upgrade/custom supplier flows are excluded.
- Multiple supplier mappings per product; one account per supplier and one product-level active selection shared across all brands.
- Supplier choice binds at accepted payment. An unavailable source preserves payment and the paid obligation for administrator review, without automatic fallback or compensation.
- Paid local preorders have no fulfillment time cap or admin-defined availability dates/ETAs; they wait for complete stock or an explicit admin resolution, with no date-change notifications.
- Deliver the complete requested order quantity. Local preorder FIFO waits for the whole oldest order; incomplete supplier acquisitions remain protected internally with zero customer release.
- Nonfinancial bot activity events have 180-day rolling retention; financial, order, support and admin-audit history follow separate policies.
- Paid-preorder cancellation requests go through existing support tickets for manual staff review; support activity does not automatically cancel or pause orders. Once cancellation is approved, the admin handles repayment outside the shop; cancellation creates no wallet credit or external payout record. All held coupon uses return automatically with approved cancellation, keeping their original expiry and current eligibility.
- Supplier cost increases up to and including 10% of the acceptance-time supplier cost are allowed; larger increases require admin review. Customer prices stay fixed.
- Valid 100%-off supplier orders procure automatically when checkout commits; coupon uses stay held until complete fulfillment, with no wallet debit or external invoice.
- Verified supplier bonus items beyond the ordered quantity go to the admin, with protected content access; customers receive their ordered quantity only.
- External refunds and their records stay outside the platform.

The feature contains **74 functional requirements, 43 success criteria and 96 planned tasks**. Repository setup T001 is complete; the remaining 95 tasks are unchecked and application implementation remains deferred. Remaining schema/provider/migration decisions are listed in the handoff.

## Spec-kit

Existing spec-kit 1.2.0 configuration, templates, PowerShell scripts and ten generic integration skills are included under `.specify/` and `.agents/skills/`. Their scripts/skills are copied unchanged from the source revision. The active feature is selected by `.specify/feature.json`.

With PowerShell installed, inspect paths from the repository root:

```powershell
pwsh -File .specify/scripts/powershell/check-prerequisites.ps1 -PathsOnly -Json
```

Continue specification clarification/review. The presence of implementation commands and a future task backlog does not authorize running implementation or deployment workflows.

## Provenance and scope

Imported on 2026-10-29 from `Farrokhzadeh/AccAbad-Shop`, branch `feature/001-csharp-native-aot`, commit [`7fcc4a7`](https://github.com/Farrokhzadeh/AccAbad-Shop/commit/7fcc4a7a35337405dd69195fc6c92474748c4513).

The original repository remains the source for legacy Django code, historical migrations and pinned supplier evidence. This repository contains the current redesign documents and spec-kit support, plus repository routing/provenance metadata. Legacy application/deployment code, environment files, databases and credentials were not imported.

The source constitution is preserved unchanged in [`.specify/memory/constitution.md`](.specify/memory/constitution.md). It describes legacy fleet/schema governance; reconcile that context with accepted multi-brand decisions before implementation rather than treating this file transfer as a new constitutional ratification.

The user redirected this transfer to `telegram-multi-bot-shop`; `door-bell` was not modified.
