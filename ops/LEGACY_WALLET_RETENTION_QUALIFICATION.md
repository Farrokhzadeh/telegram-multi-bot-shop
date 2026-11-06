# Legacy wallet retention qualification
The owner chose to exclude legacy IRR balances from operational wallet postings and keep an exact private per-account register for manual handling. This preserves the original IRR currency and amounts; it authorizes no conversion, cancellation or waiver. The owner also chose read-only old-order history with unknown missing terms. This checkpoint implements wallet retention, not that order-history representation.

## Actual protected evidence — 2026-11-06
`preserve-legacy-irr-register.py` reads only the three approved immutable archive files through an isolated output-only native `pg_restore`. Each archive is size/hash-pinned before decode and hash-checked afterward; embedded production instance identity must match the source mapping. The decoder has no network or volume mounts, a read-only root filesystem, UID 70, dropped capabilities, no new privileges, 192 MiB memory/no extra swap, half a CPU and 64 processes. It does not connect to a database or read keys/media.

The owner-server private root is `/root/.local/state/teleg-history-qualification-20261106`. `manual-irr-balances.json` is the original 40-account handling list; `wallet-history-v2.json` additionally preserves all account/native-IRR bindings and all original ledger rows, including original IDs, currency, exact amount text, clocks and descriptions. Keep both files private: they are protected migration inputs, not reports to publish or upload. Directory/file permissions are 0700/0600. Exclusive atomic publication and byte-identical replay reject changes without overwriting evidence. The original backups remain immutable.

| Source | Accounts | Nonzero IRR balances | Original history | IRR history | Zero history |
|---|---:|---:|---:|---:|---:|
| acc-abad | 9277 | 34 | 6572 | 6455 | 348 |
| kuala | 12 | 0 | 0 | 0 | 0 |
| zikotech | 2993 | 6 | 930 | 920 | 58 |
| Total | 12282 | 40 | 7502 | 7375 | 406 |

IRR/zero counts overlap; they are not additive obligations. Source preservation and exact replay passed. Only counts and original archive hashes are published in [aggregate evidence](evidence/legacy-wallet-retention-2026-11-06.json). No actual target import or re-encryption is claimed here.

## Import and reconciliation contract
`LegacyWalletRetention.OpenAll` validates the frozen version-2 policy, source/brand/archive identity, exact per-account IRR bindings and complete native ledger, once per source. Import manifests bind the register reference/digest and original IRR amount into each wallet fingerprint. Before writing, the importer rereads the protected register and compares the complete original native history from its read-only source snapshot. Unsupported currencies, changed ownership/amount/time, omitted zero entries and mismatched archives reject the affected import.

Only the frozen authoritative USD opening posts. Only nonzero USD historical entries enter the existing non-posting USD ledger. All original history, including IRR and zero entries, remains in the protected register. No IRR amount is coerced to USD, no history replays a balance and no new live non-USD wallet is introduced. The wallet audit durably records the protected register reference and digest; target/mappings/audit commit atomically and replay must match. Without a verified register the earlier non-USD hard-stop remains.

Wallet and complete-record reconciliation reread the retained artifact, verify its target audit binding, and emit only manual-account/history/IRR/zero counts plus mismatch counts. Missing/changed/unavailable retention makes `Matched=false`. Financial USD projection reports remain private; the aggregate record reports expose no customer IDs, original amounts, references or source rows. A matched subset never implies full migration or release readiness.

The shared managed/native path covers one USD opening alongside retained IRR liability/history and a zero entry, repeat imports, retained counts and missing/changed register rejection. Managed regressions additionally cover owner/currency/amount/clock/source mutation, discarded IRR metadata, archive/brand/policy mismatch and deferred transaction rollback. Existing coupon routes keep both discounts and revisions disabled. Exact-commit CI supplies the executable evidence.

## Remaining gates
T064/T066 remain unchecked. Actual source-to-target wallet import/reconciliation, native target re-encryption and complete record coverage still require execution evidence. Read-only incomplete-order history, orphan payment/deposit representation, prepaid/unowned obligation quarantine/ownership, complete delivery/service/refund compatibility and the three unresolved Telegram media references are not implemented or qualified by this register. Historical photo exceptions remain individual retained exceptions. T068 is unexecuted and separately authorized; no deployment, live database access or cutover follows.

The read-only register inspector is explicitly invoked on Linux with `SHOP_ALLOW_PROTECTED_WALLET_RETENTION_INSPECTION=1 Platform.SchemaRunner --inspect-wallet-register --register-file ABSOLUTE_PRIVATE_FILE --receipt NEW_PRIVATE_FILE`. Both paths must be in the same private `teleg-history-qualification-*` directory. It emits aggregate native-format validation only and performs no database access, target writes, key loading or decryption. Actual native server execution passed with the exact CI-built binary at `3895dce1262836034b577fcbdde648e95e700b87`, CI `42237488539`: 12282 accounts, 40 nonzero native IRR balances, 7502 retained history rows, 7375 IRR history rows, 406 zero history rows and 57 nonzero USD history rows. These overlapping counts are validation of the protected register, not actual target postings, re-encryption or complete import. See [native evidence](evidence/legacy-native-history-validation-2026-11-06.json).
