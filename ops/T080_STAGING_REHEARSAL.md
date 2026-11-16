# T080 synthetic staging backup and restore rehearsal

## Result — 2026-11-16

The authorized synthetic staging database and receipt-media backup/restore slice passed. **T080 remains open.** This rehearsal does not prove actual source import, provider identity, old-key escrow, production writer ownership, financial reconciliation or a release decision.

The source was the stopped-writer, guarded and disk-bounded T076 synthetic staging copy on PostgreSQL 18.6. Before reading it, the operator verified the staging project label, bounded volume, private network, absence of published ports, matching database/deployment/schema marker and stopped state of all five synthetic service writers. No legacy production writer or database was stopped or accessed.

A PostgreSQL custom-format dump was written to a private owner-server directory with an enforced 64 MiB file-size bound. It was 301,961 bytes and had SHA-256 `24808114dc6162271da1681b00667d75097f1a7ff9e5428a462b2c913e767569`. The hash matched again after restore. The backup is synthetic but remains protected on the owner server.

The destination was a separate PostgreSQL 18.6 container, network disabled, with a fresh synthetic password, no production mounts or published ports, a 192 MiB tmpfs data directory, 0.1 CPU, 256 MiB memory and 55 processes. Only synthetic no-login placeholder roles were created there. The archive was restored with `--single-transaction --exit-on-error --no-owner --no-privileges`. Consequently this proves the restored database content and specified schema checks, not restoration of original owners, grants or login credentials.

All 28 shop tables matched between the quiescent source and restored database by independently computed complete-row SHA-256 commitments and row counts: 236 synthetic rows in total. A separate comparison of 1,264 column and constraint records matched exactly, SHA-256 `c9512bff86d05d1eda991b8bd9e9f73620613274f7584de396c4c7531a54fd42`. This comparison does not cover every catalog object, owner, grant or runtime behavior.

The three synthetic receipt-media files were archived separately under an 8 MiB bound and restored into a separate private directory. Contents, file modes and ownership matched. The archive SHA-256 is `bf0912f4610ae2bc38129ca25bc02337cc2151268341f76703ab120f90a58722`. This is a receipt-fixture check; it does not establish backup or recovery of real media, encryption keys or all assets.

The restore container was stopped and removed, its disposable tmpfs discarded, staging stopped through the guarded operator and the loop device detached. All 13 pre-existing legacy containers remained running. The original T075 staging volume was not changed. The protected synthetic backup, comparison checkpoints and logs remain under the owner-server T080 checkpoint.

## Remaining gates

T080 still requires the applicable authorized source-copy and staging-destination rehearsal, exact source image/frozen schema and provider identity review, old-key escrow and retained dependency verification, brand writer ownership and operator approval under [release gates](RELEASE_GATES.md). Any identity or reconciliation failure remains a hard stop. The synthetic backup result does not authorize a live integration or production cutover.
