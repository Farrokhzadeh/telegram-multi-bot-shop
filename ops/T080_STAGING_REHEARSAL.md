# T080 staging cutover and rollback rehearsal

## Isolated AccAbad checkpoint — 2026-11-18

**T080 remains open.** The isolated writer handoff and rollback passed on accabad-server. The frozen provider-account identity remains unresolved. The rehearsal used the qualified T068 managed artifact and generated commerce fixtures in disposable source and target copies. The reviewed repository head was `bf6acf286ad3f64bf30148b994fc96b26d6a008e`.

| Gate | Result |
|---|---|
| Synthetic backup and restore | Reused the November 16 database and receipt restore and rehashed both retained archives against the recorded digests. |
| Legacy image and frozen schema | The rehearsal source used `sha256:77523faca5ee34d640cb98b9132ab8f649b0adf95aa104d1a974308fef8fc23d`, whose OCI revision matches AccAbad-Shop `66371a23a1c8149dc5bdf89a0355aa689d454dba`. Independently re-encoded the before/after schema of all three retained protected copies against their exact frozen plans. All match the T063 schema-v2 digest, with 56 tables and 86 migrations per source. The four selected T093 table commitments also match each frozen plan. |
| Reconciliation | Rehashed the sealed [T066 receipt](T066_RECONCILIATION.md#complete-protected-copy-reconciliation--2026-11-12) and confirmed its independent review passed. It covers frozen balances, archive fidelity and preserved quarantine. The retained T068 protected copy and restore matched all 28 frozen target fingerprints. Historical settlement and operational admission remain gated. |
| Legacy key recovery | The retained restricted AccAbad key file authenticated a frozen supplier ciphertext. The recovered bytes round-tripped and a generated wrong key was rejected. The retained file contains one legacy key. Previous rotation keys and wider escrow dependencies were outside the check. |
| Writer handoff and rollback | Six phases and 52 guards passed. Separate verification matched the signed ownership journal to Docker events. Database starts were legacy, legacy, target, target, with zero overlap. Legacy resumed before target acceptance. After acceptance, the fence rejected legacy reopening, and recovery restored all 28 accepted target tables. Restart preserved one target allocation, zero legacy allocations and rejected replay. All four actors stopped and the final fence has no writer. |
| Provider identity | The frozen source has one Akunding mapping and no stored account ID. Its copied credential received HTTP 401. A separate owner-provided credential returned an account identity and the mapped product, with no evidence binding that account to the frozen credential. See [T093](T093_QUALIFICATION.md#owner-approved-closeout--2026-11-17). |
| Operator decision | A staging closeout with Akunding disabled and its frozen account unresolved requires explicit owner acceptance of that scope. Operational provider activation, production cutover and T081 release retain their separate approvals. |

Private checkpoint is `/var/lib/teleg-cli/t068-pair-t080-20261118-889ff59277`. Rehearsal result SHA-256 is `8af16de8601546c687ca2ff4716f83a1881d010e6aa09afae17509aee5369cd1`. Writer event stream SHA-256 is `9df71c72175d00f408c25ffc47fd0a03368601346a53ae681e05eb6870552e1e`. No live provider calls or production writer changes were made.

## Synthetic backup and restore result — 2026-11-16

The authorized synthetic staging database and receipt-media backup/restore slice passed. **T080 remains open.** This rehearsal does not prove actual source import, provider identity, old-key escrow, production writer ownership, financial reconciliation or a release decision.

The source was the stopped-writer, guarded and disk-bounded T076 synthetic staging copy on PostgreSQL 18.6. Before reading it, the operator verified the staging project label, bounded volume, private network, absence of published ports, matching database/deployment/schema marker and stopped state of all five synthetic service writers. No legacy production writer or database was stopped or accessed.

A PostgreSQL custom-format dump was written to a private owner-server directory with an enforced 64 MiB file-size bound. It was 301,961 bytes and had SHA-256 `24808114dc6162271da1681b00667d75097f1a7ff9e5428a462b2c913e767569`. The hash matched again after restore. The backup is synthetic but remains protected on the owner server.

The destination was a separate PostgreSQL 18.6 container, network disabled, with a fresh synthetic password, no production mounts or published ports, a 192 MiB tmpfs data directory, 0.1 CPU, 256 MiB memory and 55 processes. Only synthetic no-login placeholder roles were created there. The archive was restored with `--single-transaction --exit-on-error --no-owner --no-privileges`. Consequently this proves the restored database content and specified schema checks, not restoration of original owners, grants or login credentials.

All 28 shop tables matched between the quiescent source and restored database by independently computed complete-row SHA-256 commitments and row counts: 236 synthetic rows in total. A separate comparison of 1,264 column and constraint records matched exactly, SHA-256 `c9512bff86d05d1eda991b8bd9e9f73620613274f7584de396c4c7531a54fd42`. This comparison does not cover every catalog object, owner, grant or runtime behavior.

The three synthetic receipt-media files were archived separately under an 8 MiB bound and restored into a separate private directory. Contents, file modes and ownership matched. The archive SHA-256 is `bf0912f4610ae2bc38129ca25bc02337cc2151268341f76703ab120f90a58722`. This is a receipt-fixture check; it does not establish backup or recovery of real media, encryption keys or all assets.

The restore container was stopped and removed, its disposable tmpfs discarded, staging stopped through the guarded operator and the loop device detached. All 13 pre-existing legacy containers remained running. The original T075 staging volume was not changed. The protected synthetic backup, comparison checkpoints and logs remain under the owner-server T080 checkpoint.

## Remaining gates at the November 16 checkpoint

T080 still requires the applicable authorized source-copy and staging-destination rehearsal, exact source image/frozen schema and provider identity review, old-key escrow and retained dependency verification, brand writer ownership and operator approval under [release gates](RELEASE_GATES.md). Any identity or reconciliation failure remains a hard stop. The synthetic backup result does not authorize a live integration or production cutover.
