# T094 supplier source selection checkpoint

## Status

Implemented on 2026-11-03; execution qualification is pending. Automatic approval review initially rejected the GitHub upload. The owner subsequently explicitly approved uploading these ten files to Farrokhzadeh/telegram-multi-bot-shop on main and running CI. Upload/CI are now proceeding; no passing execution is claimed yet. Local .NET, PostgreSQL and Docker are unavailable. Main at `63a048d699793d389129c2170d640db0fc835363`, CI `41611401617`, remains the last verified remote state; its 650 tests do not qualify these changes.

## Implementation

`SupplierSourceSelectionStore.SelectAsync` authenticates the current independent administrator and appends the existing content-bound audit before product/source locks. It requires supplier fulfillment, exact product/source revisions, enabled state and same-product ownership. One transaction advances the shared product pointer/revision and queues existing restock evaluation. Invalid commands roll back the audit and work. Committed replay returns the original revision without restoring an old pointer. Changed-content replay and revoked administrators fail closed.

Selection applies across all brands without rewriting accepted request bindings, orders, money or coupon claims. Ready-item eligibility, direct provider refresh, binding at accepted payment and procurement charge ceilings retain their existing authorities. Mapping creation/enablement, protected account configuration and new HTTP endpoints are outside this checkpoint. No schema, dependency, host startup or live service change is made.

## Pending probes

| Probe | Required result |
|---|---|
| Shared managed/native qualification | Three brands use the selected source/variant/account; paid requests keep their original binding; replay never restores an old pointer |
| 100 concurrent command replays | One product revision, audit and restock job; altered source revision conflicts |
| Disabled/foreign/stale source, stale product, local policy | No selection audit, pointer change or queued work |
| Competing commands at one revision | One success and one conflict without an orphan audit |
| Revoked administrator replay | Authentication rejection before replay or mutation |
| Switch during direct refresh | Old evidence rejected without order/debit or observation mutation |
| Unpaid invoice, switch and settlement | Binding at settlement uses fresh selected-source observation; later switching/replay preserves it |

Eleven integration cases and one shared native path are authored but unbuilt/unexecuted. All 20 local Python checks, Spec Kit prerequisites and whitespace checks pass. Full .NET, formatting and five Native AOT executable qualification remain mandatory.

## Changed paths

- `src/Platform.Persistence/SupplierSourceSelectionStore.cs`
- `tests/Platform.IntegrationTests/SupplierSourceSelectionTests.cs`
- `tests/Shared/SupplierSourceSelectionQualification.cs`
- `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`
- `tests/Platform.AotSmoke/Platform.AotSmoke.csproj`
- `tests/Platform.AotSmoke/Program.cs`
- `specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md`
- `specs/001-multibrand-redesign/CONVERSATION-HANDOFF.md`
- `specs/001-multibrand-redesign/tasks.md`
- `ops/SUPPLIER_SOURCE_SELECTION_QUALIFICATION.md`

T094 remains unchecked. Mapping administration, remaining payment/source/race coverage and provider/account capability proof stay open. Total remains 78/96 complete, 18 unchecked. All earlier legacy/provider/environment/human/release gates remain unchanged. Constitution 3.0.0, the canonical 28-table/no-JSON baseline, frozen administrator HTTP contract and read-only reviewer checklist remain unchanged. No deployment, live credentials/source rows, external service calls or staging/production mutation occurred.
