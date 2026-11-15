# T093 canonical supplier-source qualification

**Offline checkpoint — 2026-11-15. T093 remains unchecked; 84/96 complete, 12 unchecked.** The canonical implementation and copied-source verification pass their stated scope. Current real provider/account/cost/eligibility evidence is still missing.

[Generated source-bound review](evidence/t093-offline-source-qualification-2026-11-15.json) · [Selection implementation and historical evidence](SUPPLIER_SOURCE_SELECTION_QUALIFICATION.md) · [Canonical schema qualification](T090_QUALIFICATION.md)

## Requirement and evidence mapping

Existing implementation and passing evidence were mapped before execution. No production code, SQL, dependency or table change was needed.

| T093 requirement | Evidence and scope |
|---|---|
| One shared selected source | Only `products.active_supplier_source_id`; same-product composite FK. No selected flag, brand override or account pool is introduced. Existing PostgreSQL schema evidence is reused from exact current compile inputs. |
| Enabled selection, later disable | Fresh selection/administration cases reject disabled, foreign and stale choices; disable retains the pointer and blocks new remote work. Re-enablement preserves accepted review. |
| Null-aware mapping uniqueness | Existing create/replay/uniqueness cases verify canonical identity indexes for absent/present variants and immutable referenced identities. |
| Immutable accepted binding | Fresh payment/source races cover wallet, zero-due, crypto and bank acceptance; accepted source/account/product/variant/cost snapshots survive switches and replay. The previously qualified 25-field batch and 15-field attempt schema remains unchanged. |
| One protected account | Fresh account-replacement tests reject a different account before transport; same-account recovery preserves original intent. Existing credential-file/grant tests are mapped in prior qualification. These use generated credentials and do not prove the live account. |
| Constraints and locking | Fresh 36 PostgreSQL selection/race/administration cases, plus 12 baseline cases, pass. Six private-evidence mutation guards reject altered body, key, count, schema, plan identity and final receipt. Reused 16 T090 schema cases remain input/artifact/TRX bound. |
| Exact legacy provenance | Complete before/after streams for connections, mappings, availability snapshots and linked products match keyed frozen commitments in all three sources. Full schema hashes and exact plans are independently bound to the successful T066 execution seal. |
| Actual provider observation/cost/account | **Open.** Saved local configuration and expired availability cannot establish current provider identity, native cost/currency or exact ready-item eligibility. No real request was made. |

The 410 current compile inputs exactly match the reused T090 build. Immutable artifact manifests and TRX hashes match; no rebuild or broad unchanged suite is claimed. The fresh target uses authenticated disposable PostgreSQL18; correct credentials pass and incorrect credentials fail. Extra brands appear only in shared-source/account isolation and final three-source reconciliation.

## Actual copied configuration

| Source ordinal | Connections | Mappings | Availability snapshots |
|---|---:|---:|---:|
| 1 | 1 | 1 | 1 |
| 2 | 0 | 0 | 0 |
| 3 | 0 | 0 | 0 |

The one saved provider is Akunding, with an encrypted nonempty credential and one exact numeric product mapping without a variant. Its local connection is enabled, but the account-identity field is empty. The availability snapshot is expired; its frozen schema has no supplier-cost or currency column. Product retail prices are not supplier costs. A local enabled flag is not target activation or proof of current provider readiness.

Literal source authority is `66371a23a1c8149dc5bdf89a0355aa689d454dba`, including `SupplierConnection`, `SupplierProductMapping` and `SupplierAvailabilitySnapshot`. The old one-to-one product mapping is historical configuration, not evidence that the new many-source model may select it automatically. Preserve the original source/configuration and retained keys; no credential, selected pointer, observation or operational row is imported or activated. T066's configuration commitments are distinct from its selected archive projections. T090's empty operational supplier histories remain separately qualified.

## Safety and remaining gate

An isolated copy of the stopped PostgreSQL16 source was queried only through explicit READ ONLY transactions and the restricted reader. Original source bytes match the earlier qualified fingerprint. All 270 preexisting container configurations/images/mounts/states are unchanged; all four new actors are stopped. No original production database, bot or media was started or modified. Private records, identifiers, prices, encrypted credentials, keys and receipts remain on the owner server.

Private checkpoint: `/var/lib/teleg-cli/t093-source-20261115/`. Checkers: `source.py`, `review.py`; exit codes: `source.exit`, `tests.exit`, `tests-review.exit`, `review.exit`, all zero. The source-bound final review independently recomputes complete keyed streams and validates source-plan execution provenance, linked identities, build inputs, runtime artifacts, TRX results, authentication and final host/source safety.

The concrete next operation is saved privately as `proposed-provider-read.private.json`, initially unauthorized. It binds the exact copied connection/mapping and proposes at most three authenticated GETs, 15 seconds each, a 120-second total deadline, bounded responses, no retry/redirect, and only the existing account identity and exact mapped product. Official endpoint documentation must be verified first; missing keys, undocumented endpoints, ambiguous identity/currency or non-ready eligibility stop the check. No purchase, order export, credential rotation, mapping activation, deployment or production database/bot access is included.

AGENTS.md retains separate authorization for real supplier calls, including read-only calls. Request that authorization only after this offline checkpoint is published. A successful read would qualify only its observed account/product scope; no remote idempotency, stable item recovery, purchase or release capability is inferred. Owner-approved option A remains unchanged: a fresh exact-source same-currency check within the inclusive 10% allowance may permit uncapped purchase under its separate gates; no false remotely enforced cap is asserted. Other providers retain their own gates.

Normal hosts fail closed. No CI, live provider operation, fresh Native AOT publication, financial settlement or production release is claimed. Reviewer checklist remains read-only and unchanged.
