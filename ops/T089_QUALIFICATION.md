# Activity verification and protected legacy reconciliation — T089

**Complete — 2026-11-15, for activity engineering verification and the approved frozen-source preservation/quarantine scope.** Project status is **83/96 complete; 13 unchecked**. Production release, intended-environment maintenance grants/scheduling, transport epoch recovery, human acceptance and fresh Native AOT qualification remain separately gated.

[Generated source-bound final review](evidence/t089-frozen-activity-reconciliation-2026-11-15.json) · [Offline tool](migration/legacy_activity/README.md)

## Requirement and evidence mapping

Existing passing evidence was mapped before implementation or repeated testing. The T068 runtime regression's original integration, corrected recovery and contract TRX files were checked against their published SHA-256 values. Their union supplies **35 passing activity/coupon cases**. A fresh comparison matches 397 current-main source inputs; four changed test helpers concern unrelated staging-host composition. All production inputs for those scenarios match the base main. This reuse does not qualify the new legacy lane: its actual source execution and independent full-record comparison are separate below.

| T089 requirement | Verified evidence |
|---|---|
| Duplicate Telegram updates and timeout workers | Independent replay checkpoint survives deletion/rollback; actual flow expiry is concurrent, repeat-safe and preserves original occurrence. Actual UI abandonment never cancels domain obligations. |
| Wrong-brand/member references and privacy | Foreign order rejection, registry context guards, exact fourteen-column allowlist and independent-admin/session-bound timeline/cursors. No raw DM, callback, coupon, receipt, credential or provider value enters operational activity. |
| Flood, rate and concurrent-insert bounds | Concurrent same-member dedupe; 100-event database-volume case; bounded 500-row target import/replay; bounded reads/cleanup and the independent coupon-lookup window. No new general database volume cap is claimed. |
| Exact180-day UTC cutoff and restart | Equality is retained; strictly older events are hidden/eligible for bounded cleanup. Failure, rollback, resumable cleanup, blocked backlog and independent domain provenance are covered. |
| Source/archive isolation and no domain deletion | Actual restricted maintenance role cannot read/mutate private domain data. All27 other relations remain unchanged; source/archive retention is separate. The new offline legacy lane has no database connection and runs with read-only inputs/runtime, isolated networking and private output. |
| Restore/import re-expiry and financial replay | Actual custom-format target backup/transactional restore, ACL preservation, original clocks, cutoff before reads, independent financial/update replay and atomic target-import failure/restart. These remain target-native scenarios. |
| Coupon settings, creation/edit history and true redemption | Actual owner overrides preserve original orders and actual redemption/held context; edit/redemption races serialize; deferred revision failure rolls back settings/audit/history together. Revision immutability and replay prevent history divergence. |
| Actual legacy activity | Every one of 88,053 frozen originals has authenticated encrypted preservation and an independently checked finite protected exception disposition. None was promoted to an invented operational tuple. |

The reused source witnesses are `BotActivityFloodTests`, `BotActivityRuntimeTests`, `BotActivityRegistryTests`, `WorkflowActivityTests`, `CouponRevisionTests` and `BotActivityTimelineTests`. The private generated reuse review records each passing case and its exact result hash. T089 closure does not promote a synthetic witness into whole-criterion or intended-environment acceptance; the scenario catalog's broader release/human gates remain in force.

## Actual frozen-source reconciliation

Base main is `d20a546cab07b8a5792dd970cb5cb79fcff6e998`; legacy producer revision is `66371a23a1c8149dc5bdf89a0355aa689d454dba`. Literal logger/producer inspection shows that legacy `start` includes menu/help and is logged before replying; shop/product observations lack verified Telegram replay/outcome provenance. Checkout logging does not establish the target's exact context and immutable event clock. Arbitrary event names/details do not establish payment, ownership or administrative authority.

The exact eight source fields are preserved: `id,actor_id,event_type,ref_type,ref_id,details,created_at,user_id`. Original actor/reference/details values remain only in protected original evidence. No actor is guessed to be the customer. Non-null original user references bind only through their exact frozen membership provenance; null references remain unbound.

| Source ordinal | Originals | Proven membership references | Unbound membership | Unverified interaction | Unverified checkout | Unsupported producer |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 74,766 | 74,403 | 363 | 63,118 | 3,833 | 7,452 |
| 2 | 95 | 83 | 12 | 83 | 0 | 0 |
| 3 | 13,192 | 13,128 | 64 | 10,890 | 749 | 1,489 |
| Total | 88,053 | 87,614 | 439 | 74,091 | 4,582 | 8,941 |

These are disjoint typed protected exceptions, not successful customer actions or settled obligations. **Operational events imported:0.** Every original remains recoverable, including unsupported/private content and original microsecond timestamps. Nothing is discarded because it is old or cannot satisfy the target registry. No Telegram update,outcome,context,coupon edit or ownership is reconstructed from absent facts.

A fresh isolated PostgreSQL 16 copy was queried with explicit READ ONLY transactions. Full keyed commitments for `user_activity` and `users` match the exact frozen plans; all 12,282 original memberships are covered. The membership/archive plans match the T066 execution-sealed inputs. The earlier schema/count preflight remains insufficient by itself.

The current managed tool creates deterministic source/brand-bound archive identities and immutable manifests, then uses the existing authenticated per-brand archive format. Each source passed a second fresh-process replay with byte-identical manifest,index,ciphertext and report. A separate Python AES-GCM reader independently decrypted **all 88,053** archives,compared every original byte string,recomputed source/record commitments,and verified each disposition's original identity,time,membership and archive digest. Missing,duplicate,unexpected or mismatched originals/dispositions are not accepted.

`UserActivityRecord` appends a migration projection without changing previous ordinals. The existing three-argument candidate API and default twenty-family T064/T066 selection remain intact; activity selection is explicit. The archive-only `activity` kind does not insert a target map or operational event. Registry version 6,28 typed tables,fourteen activity fields,canonical SQL and target replay/retention behavior are unchanged.

## Checks, safety and remaining gates

Fresh checks passed: 12 baseline cases, 258 unit cases including seven new archive/disposition cases,and nine offline CLI empty/replay/rejection cases. Final builds have zero warnings/errors; formatting and the pinned offline locked restore passed. The operator and unit bundle use the identical current migration assembly. Thirty-five matching existing activity/coupon cases were reused; unrelated broad suites were not repeated.

All 244 preexisting containers retain their configuration,image,mounts and state. Original source bytes are unchanged; the original source was never started. Every new actor is stopped. Actual protected execution used no production database,bot,media,provider,Telegram or customer route. The three sources were included only for required final reconciliation. Keys,records,identifiers,raw activity and private receipts stay on the owner server.

Private checkpoint: `/var/lib/teleg-cli/t089-activity-20261115/`. `independent_review.py` verifies originals; `reuse_review.py` binds the reused scenarios; `final_review.py` combines those gates. Failed attempts remain retained:Linux platform analysis,reading a binary key as text,a protected synthetic-fixture output collision,and staged key copies rejected for writable permissions. The corrected copies use owner-read-only keys. Final review also detected four lockfiles whose restore had removed only an empty runtime section; complete dependency dictionaries matched,and exact main lockfiles were restored before the successful pinned check and review. No safety/financial guard was relaxed.

The reviewed operator bundle SHA-256 is `02c039c583a16ba8c9af5cd7f7a0a76aac66f11fd67e69e17920ad8bf001e5fc`. The independent review checker SHA-256 is `d299100e8c088b54a61fde6ca6eec9cf6d0e756496343599c39a935cc05311ba`; the final review checker is `a4793993d7e45ce8564bb3293da54ccacfcfad190c99c72f60ba700006ea57fe`. The generated evidence records source hashes and exact counts.

No CI was invoked. Continue on main with `[skip ci]` and verify all-event Actions after publication. Reviewer checklist markers remain unchanged at 92/126. Protected original retention/key dependencies remain separate from operational 180-day cleanup. `financial_obligations_verified`, `normal_host_activation_verified` and `release_ready` remain false. T075/T076/T079/T080/T081 and other remaining task/provider/human/release gates are not closed by this result.
