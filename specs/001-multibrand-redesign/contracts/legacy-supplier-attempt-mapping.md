# Legacy supplier history mapping — T090

Source authority is `Farrokhzadeh/AccAbad-Shop` revision `66371a23a1c8149dc5bdf89a0355aa689d454dba`, especially `apps/store/models.py` and `apps/common/services/suppliers/{live,procurement,reconciliation,refunds}.py`. Target field/type/key authority remains [schema-contract.md](../schema-contract.md), tables 13,14 and 26. This review introduces no schema,daemon,provider call or refund endpoint.

## Entity correspondence

| Legacy source | Target correspondence or preservation rule |
|---|---|
| `SupplierFulfillment` / `supplier_fulfillments` | An order-level supplier batch corresponds to `supplier_requests`, not one customer fulfillment unit. Require an exact mapped original order and verified immutable source/account/product/quantity/cost binding before any operational representation. |
| `SupplierFulfillmentItem` / `supplier_fulfillment_items` | A source unit may correspond to a same-order `fulfillments` row under its batch. Original encrypted content must authenticate and be re-encrypted for the purchasing brand and exact target row/field. Preserve unknown or unstable remote identities; unit position alone is not verified remote deduplication. |
| `SupplierProcurementAttempt` / `supplier_procurement_attempts` | Preserve original identity,sequence,operation,outcome and clocks. An operational `supplier_request_attempts` row requires the separately proven target lineage and evidence described below. |
| `SupplierRefundRecord` / `supplier_refund_records` | Protected archive only. No operational refund table,endpoint,payout or inferred settlement. |

The existing protected archive projections are `SupplierFulfillment`, `SupplierItem`, `SupplierAttempt` and `SupplierRefundArchive`. Their archive identity/proof is preservation evidence, not permission to dispatch a supplier call or manufacture a target operational row. The existing T064/T066 default selection remains unchanged.

## Attempt fields and semantic restrictions

| Actual legacy column | Canonical target field / rule |
|---|---|
| `id` BIGINT | Retain the original brand/table/key in protected provenance. Never treat a legacy integer as a globally scoped target UUID. |
| `fulfillment_id` BIGINT | Resolve the exact original batch to its target `supplier_request_id`; do not point at a unit's `fulfillments.id`. |
| `attempt_number` INTEGER | Preserve the exact positive number as BIGINT. Preserve gaps/conflicts for review; never renumber or synthesize missing remote calls. |
| `operation` VARCHAR | Candidate vocabulary:submit→submit,lookup→status_lookup,export→export. This is only a vocabulary correspondence; literal call provenance must establish the actual operation. |
| `outcome` VARCHAR | Source values are not the target's finite lifecycle. Preserve unknown/unrepresentable values in protected evidence. No automatic rename can establish target success, no-effect or safe retry. |
| `safe_error_code` VARCHAR | Preserve the original. Copy only an independently validated safe typed code into operational metadata; no raw error/provider body. |
| `provider_http_status` SMALLINT nullable | May correspond to `http_status` only when the original value is a valid 100–599 status and its exact attempt is proven. Missing status remains missing. |
| `worker_identity` VARCHAR | Protected original only. It does not establish an administrator,verified actor,claim lease or retry authorization. |
| `started_at` TIMESTAMPTZ | Preserve original UTC occurrence; do not stamp import/recovery time. |
| `finished_at` TIMESTAMPTZ nullable | Preserve original null/value and ordering. Never invent a terminal clock or restart an old call from an unfinished source row. |

Legacy rows have no equivalent of a verified target `parent_submit_attempt_id`,independent `admin_authorization_audit_id`,provider-qualified `purchase_idempotency_key`,immutable per-attempt cost ceiling or authenticated provider `evidence_ref`. A mutable request summary,local intent,worker name,HTTP failure or archived source record cannot fill these gaps by assumption. Unsupported nonempty originals must stay protected and blocked until their exact mapping/evidence is separately qualified.

Literal producer review finds stronger reasons than column-name differences:

- `live.py` chooses the recorded operation from the presence of `provider_order_id`; its unpaid-order operator-resume branch can nevertheless perform purchase work while the recorded label is `lookup`. Do not blindly translate that label into proof of a read-only lookup.
- `live.py` and `procurement.py` begin with `outcome='started'`,then write a fulfillment state into the attempt outcome. Queued/processing/partial/fulfilled/review states are not target remote-call outcomes.
- `reconciliation.py` and `refunds.py` can record lookup-shaped recovery or administrator assertions. `admin_confirmed_no_order` and `admin_confirmed_failure` do not supply the target's provider-verified no-effect evidence or an authenticated retry audit.

## Frozen-copy qualification boundary

The three approved November 5 backups contain zero rows in all four operational supplier-history families. Actual copied-source full-schema hashes and keyed full-table commitments must match the frozen plans before and after qualification. A count from a plan,missing archive or unsigned receipt alone is insufficient. Match the exact T066-sealed archive plans and their empty projection sets as well.

This empty-set reconciliation imports no supplier requests,attempts,units or refunds. It proves no original in these families was omitted; it does not qualify a future nonempty legacy importer,absence of external purchases,paid-obligation settlement or current provider/account behavior. A changed or nonempty source must fail this qualification and retain its originals for review. Configuration/mapping observations remain with T093/T094; live operation proof remains with T057/T091/T092.

Canonical target attempts retain exactly fifteen typed fields,unique request/attempt numbering,initial in-flight intent,immutable terminal evidence and same-request parent lineage for lookup/export. Requests remain order-level batches with positive remote remainder; unit content stays brand-encrypted and same-order guarded. Jobs retries never substitute for remote-call history. [T090 qualification](../../../ops/T090_QUALIFICATION.md) records the executed evidence and remaining gates.
