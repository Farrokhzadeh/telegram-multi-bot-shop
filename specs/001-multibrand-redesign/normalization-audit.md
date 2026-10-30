# Normalization and consistency decisions — current status

**Reviewed:** 2026-10-30. The prior exploratory audit is preserved in Git history at the review input commit. This document is the current decision summary; it does not reopen owner approvals.

Authoritative fields: [schema-contract.md](schema-contract.md). Inventory: [database-table-review.md](database-table-review.md). Findings/gates: [consistency-review.md](consistency-review.md). Coverage: [requirements-traceability.md](requirements-traceability.md).

## Selected forward design

| Area | Decision |
|---|---|
| Customer/tenant identity | Membership users.id; no global customer wallet/identity; static database ownership guards |
| Monetary storage | Exact finite NUMERIC; USD-only wallet; quote denomination distinct from received network asset; legacy precision/denomination proof required |
| Public order number | orders.id only; no new public_ref; source aliases retained in legacy_id_map |
| Product routing | Typed local-first/immediate/local-preorder policies; one shared same-product source pointer |
| Source lifecycle | Disable may retain a selected pointer; changed selection requires enabled source; no automatic fallback/rebind |
| Account cardinality | One active protected configured account per supplier/one NOWPayments merchant; no account pool table |
| Supplier snapshots | Typed accepted identity, remote quantity and immutable cost baseline; explicit request-bound override; current observations are not paid facts |
| Remote operation evidence | Separate pre-call attempt intent and immutable terminal evidence; provider key/parent submit distinct from scheduler retries |
| Unit kinds | Keep local/supplier/manual kind: mixed actual provenance is meaningful |
| Complete delivery | Existing order generation/unit membership/typed delivery parts; no partial business-batch relation |
| Bonus storage | admin_held inventory with supplier identity guard; never customer unit or automatically available stock |
| Wallet origin | Typed payment/order/request/import links and exact posting/version evidence, not EAV or a generic source string as authority |
| Payment purpose | XOR order/topup origin; method and quote_currency distinct; legacy kind retained as evidence until proof |
| Prepaid source order | Explicit prepaid_units discriminator; one N-use coupon, no fake goods allocation/procurement |
| Coupon claims | Derive user from order; retain saved denomination; keep held/redemption facts separate from editable adjustment |
| Coupon revision | Keep immutable typed post-edit snapshots and real held/redeemed context |
| Customer vs staff sends | Customer-derived private target; checked staff-only configured destination exception; no customer group commerce |
| Public admin contact | One configured contact per brand, separate from global staff authorization and private group |
| Channel enforcement | Verified-staff exemption plus audited per-brand bypass; preserve configured channels |
| Holds | Explicit paid local-preorder head blocks; no priority rewrite, automatic resume timer or money/coupon side effect |
| Financial resolution | Explicit proven-safe supplier retry or linked wallet credit; bank USD input; external refunds/records excluded |
| Retention | Separate occurred_at and closed_at clocks; no broad cascades or expired analytics as replay authority |
| Migrations | Exactly 28 application tables; cyclic FKs attached after definitions; external operational manifests, no hidden checkpoint/history table |

## Conditional legacy compatibility work, not new business questions

T006/T029/T063–070 must establish actual one-product source cardinality, USD liabilities, precision, pricing/rounding, prepaid provenance/expiry exceptions, legacy payment denomination, identifiers and partial delivery history. Keep source evidence and fail affected imports if an approved target representation cannot preserve them. A real additional relation requires an explicit reviewed inventory change; none is smuggled into this audit.

Retain campaign language, send/edit action, source attempt chronology, optional historical sender evidence, declared payment quote denomination and ledger balance/version evidence. These carry independent semantics. No other speculative column removals are authorized here.

## Verification still required

Static documents can select a guard; they cannot prove executed PostgreSQL constraints, ciphertext roundtrip, replay-safe provider keys, cost-ceiling enforcement or native-host behavior. Gates E1–E6 and their task owners are explicit in the review report. All implementation tasks except completed repository setup T001 remain unchecked.
