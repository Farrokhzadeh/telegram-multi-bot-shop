# Supplier attempt schema and frozen legacy qualification — T090

**Complete — 2026-11-15, for the canonical target schema and the exact approved frozen legacy snapshots.** Project status is **84/96 complete; 12 unchecked**. This qualification does not enable real provider operations or qualify a nonempty legacy supplier-history importer.

[Generated final review](evidence/t090-frozen-supplier-qualification-2026-11-15.json) · [Legacy field/producer mapping](../specs/001-multibrand-redesign/contracts/legacy-supplier-attempt-mapping.md)

## Requirement mapping

Existing implementation and passing evidence were mapped first. The canonical schema already uses an order-level supplier batch and one-to-many acquired units; no production code,SQL,migration or physical relation was added. T090's missing work was exact contract assertions and source-bound legacy qualification.

| Requirement | Verified representation and evidence |
|---|---|
| Typed submit/lookup/export and outcomes | Exactly fifteen attempt fields with checked types/nullability,finite operations/outcomes,initial in-flight intent and immutable terminal evidence. The previous permissive column-count assertion now checks the exact field contract. |
| Unique chronological attempt identity | Actual PostgreSQL cases reject skipped/duplicate sequence numbers,terminal insertion and immutable-intent changes. Request locking and `(supplier_request_id,attempt_no)` uniqueness remain intact. |
| Lookup/export lineage | Parent submit must be earlier and belong to the same request. A new export case verifies accepted lineage,rejected foreign lineage,unknown finalization and rejection of terminal evidence replacement. This is a schema test, not a remote export call. |
| Batch request and unit ownership | Exactly twenty-five request fields include `order_id` and positive remote remainder,without an obsolete unit-level `fulfillment_id` or duplicated attempt-summary columns. Existing same-order unit guards and immutable source/account/cost terms remain qualified. |
| Encrypted content and remote identity | Matching provider-neutral recovery/partial/contract/secret cases verify protected acquired content,brand scope,identity replay and review. Unknown effects never authorize another purchase. Optional stable item deduplication is not inferred for an unverified real provider. |
| Legacy mapping | Literal pinned model/producer review plus actual before/after copied-source full-schema and keyed table commitments. All four operational supplier-history families are empty in each frozen source; T066 execution-sealed archive manifests have exactly empty corresponding projections. |
| No provider JSON/refund expansion | The baseline remains 28 typed tables. No provider payload/credential content is added to attempt history, and legacy refund records remain archive-only. No refund table,endpoint,payout or automatic settlement is created. |

The local `max_charge_amount` field retains its reviewed price/audit meaning. This check asserts no remotely enforced cap and changes none of the owner-approved option-A rules.

## Actual source evidence

Source authority is legacy revision `66371a23a1c8149dc5bdf89a0355aa689d454dba`; base main is `cdf19b5b3a2330ee22870b4ca724973793f672eb`. A fresh isolated copy of the stopped PostgreSQL16 source ran with explicit READ ONLY transactions and the restricted reader. The original source was never started.

| Frozen source ordinal | Requests | Items | Attempts | Refund records |
|---|---:|---:|---:|---:|
| 1 | 0 | 0 | 0 | 0 |
| 2 | 0 | 0 | 0 | 0 |
| 3 | 0 | 0 | 0 | 0 |

These are actual complete empty streams, not assumed absence from a report or a sample. For every source,the full schema fingerprint and each keyed full-table commitment match its exact frozen plan before and after the check. The source attempt table's ten columns/types/nullability match the pinned model. Original source bytes also match the previously qualified T089 fingerprint. An independent reviewer validates the source receipts,the original T066 execution seal and exact source/archive-plan hashes; all corresponding protected archive projection sets are empty. Six mutation guards reject changed planned/observed counts,schema,archive coverage,plan identity or final schema.

No supplier request,attempt,unit or refund is imported or activated by this qualification. The empty-set proof establishes no omitted original in these families. It does **not** establish absence of external purchases or supplier-related paid obligations elsewhere; existing order/financial uncertainties stay preserved. A future changed/nonempty source fails this scope and requires its own complete mapping/reconciliation.

## Literal legacy semantics

The legacy batch named `SupplierFulfillment` corresponds conceptually to target `supplier_requests`; its child item corresponds to a target unit. Do not confuse their identities. Original numeric keys,numbers,clocks,refs and encrypted content require exact protected provenance.

The source's operation/outcome labels are not direct operational facts. `live.py` can record `lookup` from the presence of a remote order ID while its unpaid-order resume branch performs purchase work. Producers write `started`,fulfillment states and administrator assertions into the outcome field. They do not supply all target parent-submit,audit,idempotency,cost and provider-evidence fields. The mapping contract therefore prohibits guessed lineage,clocks, no-effect outcomes,authorization or safe retry. No actual nonempty history is claimed translated by this empty-snapshot qualification.

## Validation and safety

Fresh checks passed: 12 baseline cases and 16 PostgreSQL supplier schema cases. Final build/format passed with zero warnings/errors. Sixty-four matching recovery,partial-review,contract and credential cases were reused from their hash-verified passing TRX files. The 410 current-main build inputs were checked; only the intended test file differs. The eight older-runtime differences concern T089 migration/activity additions and unrelated staging helpers,outside the reused supplier paths. No broad unchanged suite or new Native AOT publication is claimed.

The pinned legacy producer files were independently matched by Git blob identity. The final review checks exact source/target runtime configurations,artifact and test-result hashes,authentication rejection,disposable database cleanup and stopped actors. All 266 preexisting container configurations/images/mounts/states are unchanged; original source bytes remain unchanged; all new actors are stopped. Additional source ordinals were used only for required final legacy reconciliation. No production bot/database/media,real supplier/payment/Telegram route or customer recipient was used.

Private checkpoint:`/var/lib/teleg-cli/t090-supplier-20261115/`. `source.py` performs copied-source verification; `reuse.py` binds existing results; `review.py` performs final provenance/safety and six negative checks. The initial setup failed on an already-created private checkpoint directory; that attempt is retained, and the corrected setup passed before any source execution. Sensitive inputs,keys and private receipts stay on the owner server. The public JSON records exact tool,artifact,test and source identities without customer records or private values.

Reviewer checklist markers remain unchanged at 92/126. No CI was invoked; publish directly on main with `[skip ci]` and check all-event Actions twice. T057/T091 retain real-provider capability/operation authorization and proof; T092/T093/T094 retain their own remaining supplier/source gates. T075/T076/T079/T080/T081 and other remaining tasks are unchanged. `nonempty_legacy_history_import_qualified`, `live_provider_operation_verified`, `financial_obligations_verified` and `release_ready` remain false. Normal hosts remain fail closed.
