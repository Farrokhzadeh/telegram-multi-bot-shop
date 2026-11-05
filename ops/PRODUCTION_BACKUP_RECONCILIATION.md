# Authorized offline production-backup assessment
2026-11-05. Source-only evidence for T063/T064/T066; **not full import, migration readiness, staging rehearsal or release qualification**. Baseline target main: `32702c265e50bf17b0a410d8f431dc001541bc02`, successful [baseline CI](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/42018100151). Legacy source main: `66371a23a1c8149dc5bdf89a0355aa689d454dba`. The supplied earlier readiness report was a hypothesis; the results below were independently recomputed from all three archives.

## Integrity and isolated restore
| Archive | Bytes | Independently verified SHA-256 |
|---|---:|---|
| prod1-20261105-131013.dump | 7419590 | `0e86433927fa420ad3e13bbff2098119d47dfbaa0b626573c552999902703667` |
| prod2-20261105-131016.dump | 259270 | `a61aaddbbd76a10aa6894aa02cffe861cb44a9602b1d085eecc981f822656071` |
| prod3-20261105-131017.dump | 1642702 | `4481980586074034e84ff11d867621a1aa847c18589576ab7dd89155a21d6245` |

All three PostgreSQL 16.14 custom-format archives were successfully listed and decoded by native `pg_restore 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)`. Each has 441 archive entries and 56 COPY table-data blocks. Original uploads remained unchanged; working copies and decoded data were kept outside the checkout in a mode-0700 directory with mode-0600 files. No source dump, extracted row, credential, key or media path is committed or sent to CI.

Native PostgreSQL server initialization was unavailable: this container cannot change to a nonroot database owner, and native initdb rejects root. As an independently identified fallback, each complete schema/data/sequence set was restored into a separate in-memory **PGlite 0.5.15 / PostgreSQL 18.6 / wasm32** database. Schema, indexes and constraints were created, all 56 COPY blocks were loaded through the engine's Blob COPY input in one deferred-constraint transaction, sequence statements were applied and COMMIT succeeded. Engine SQL independently confirmed users/orders/inventory counts below. All databases were closed after inspection. No network database or live integration was contacted. This is an actual isolated engine restore, but **not native Npgsql/production-environment, ACL/ownership, performance, cutover or rollback qualification**. Owner/ACL restoration was deliberately excluded with `--no-owner --no-privileges`.

## Schema and source contract
The three native schema-only outputs are identical after removing blank/comment lines and pg_restore's random `\restrict`/`\unrestrict` lines, joining remaining lines with LF and no final LF. SHA-256: `cc67f38fd3ec37d477da3ac83b920436467edefa9587b17c189d35840f26681d`. This explicit format differs from the earlier report's selected-TOC hash; it is not evidence of changed source schemas.

All applied migration sets are identical: 86 entries, including 68 project migrations and 18 Django migrations. Every applied project migration name exists at the pinned legacy revision. Counts: accounts 9, store 11, service_jobs 5, payments 10, common 33, contenttypes 2, auth 12, admin 3, sessions 1. Migration-set hash is `ebd90c700a78ac8859c06f2ab296c6bd4942496753fdbdbc52712e25d47283a5`, over compact UTF-8 JSON of sorted `[app,name]` pairs. Matching names do not authenticate deployed migration-file contents or the deployed image/revision.

The reader's four catalog queries also executed on each restored WASM database: 556 columns, 672 constraints, 188 indexes and 86 migration entries, all below the existing 1000-row bounds. The `accabad-source-schema-v2` BinaryWriter encoding independently reproduced the same fingerprint for all copies on that engine: `7a13b9bbec71991ead8c770b1f6addb09431a62b71b23777e9f5ac9dac4a1aad`. PostgreSQL versions affect catalog representations, including NOT NULL constraints; **do not pin this restored-18.6 fingerprint as the original-16.14 fingerprint**. Compute/review it on the exact approved native source copy before constructing an import manifest. This exercise did not run the .NET/Npgsql source adapter against actual rows.

No `order_items` table exists; source orders have one product FK and quantity. The copies contain no preorder batch/state/refund or supplier fulfillment/item/procurement/refund records. Their absence cannot qualify actual historical partial-order or supplier-recovery compatibility. Source user rows have original `banned_at`, no `last_name`; the existing optional JSON-field reader supports the absent name. Six original ban times were observed, five in prod1 and one in prod3. The importer previously substituted observation time; the continuation now binds and preserves a supplied original time.

## Independently counted inventory
| Records or exceptions | prod1 | prod2 | prod3 |
|---|---:|---:|---:|
| Users | 9277 | 12 | 2993 |
| Orders | 4167 | 0 | 749 |
| Products | 18 | 3 | 10 |
| Inventory | 4397 | 0 | 791 |
| Assigned inventory | 4397 | 0 | 782 |
| Unassigned inventory | 0 | 0 | 9 |
| Transactions | 6572 | 0 | 930 |
| Zero-value transactions | 348 | 0 | 58 |
| Manual deposits | 3989 | 0 | 736 |
| Crypto attempts | 95 | 0 | 27 |
| Discounts | 117 | 0 | 6 |
| Nonzero IRR wallet records | 34 | 0 | 6 |
| Nonzero USD wallet records | 2 | 0 | 1 |
| Historical IRR orders | 4076 | 0 | 741 |
| Historical IRR transactions | 6455 | 0 | 920 |
| Empty order checkout snapshots | 3325 | 0 | 494 |
| IRR-wallet order snapshots | 149 | 0 | 25 |
| Coupons without assigned owner | 20 | 0 | 1 |
| 100%-off coupons | 70 | 0 | 4 |
| 100%-off coupons without assigned owner | 11 | 0 | 0 |
| Deposit receipt file references | 3498 | 0 | 691 |
| Product media references | 2 | 0 | 1 |
| Invoice-created crypto attempts | 81 | 0 | 25 |
| Deposits without fulfilled-order link | 3244 | 0 | 473 |
| Crypto attempts without fulfilled-order link | 83 | 0 | 27 |
| Delivery headers / parts | 693 / 693 | 0 / 0 | 206 / 206 |
| Delivery attempts | 694 | 0 | 207 |
| Service jobs | 42 | 0 | 4 |
| Encrypted service payloads | 31 | 0 | 2 |
| Wallet-funding authorizations | 284 | 0 | 98 |
| Activity events | 74766 | 95 | 13192 |

Inventory assigned to an order never exceeds or falls strictly between zero and that order's requested quantity in these copies. Forty-six orders have no assigned inventory, matching the count of service jobs; this count agreement does not establish completion, repayment or absence of obligations. Delivery attempts include 899 `sent` and two `retry_wait` outcomes. All 5188 inventory values have the `enctext:v1:` marker; all 33 nonempty submitted service payloads have `encjson:v1:`. Markers are structural observations, **not authenticated decryption proof**. One supplier connection, one supplier product mapping and one availability observation exist in prod1; the credential was not decrypted or used.

| Source pair | Shared numeric user IDs | Shared product IDs | Shared inventory IDs |
|---|---:|---:|---:|
| prod1 / prod2 | 4 | 3 | 0 |
| prod1 / prod3 | 339 | 10 | 764 |
| prod2 / prod3 | 2 | 3 | 0 |

No order, manual-deposit or payment-attempt primary IDs overlap between these copies. This does not relax source-scoped identity requirements. The payment writer's globally unique idempotency reference previously used only a legacy ID; it now uses the frozen target identity while preserving the original source ID in mappings/evidence. Synthetic cross-brand collision regression covers the unobserved collision case without claiming such a collision was found in these backups.

## Representation and reconciliation boundaries
| Source facts | Current safe treatment | Remaining blocker |
|---|---|---|
| Source identities, profiles, original times | Existing immutable source/brand/kind mappings; original ban-time preservation added | Operator-verified source-to-brand/bot bindings, frozen checkpoint/image identity and actual Npgsql import comparison |
| USD opening and USD history | Existing single opening/nonposting projection remains repeat-safe in synthetic tests | Full source accounting comparison; current source history is mixed-currency |
| 40 IRR balances; 7375 IRR ledger rows | Retain original snapshots and denomination; affected wallet import rejects | Explicit exact liability reconciliation and reviewed historical currency representation; no USD coercion or ledger replay |
| 4817 IRR-labelled orders; 174 IRR-wallet snapshots | Preserve original amount, label, method and snapshot | Existing terms reject IRR wallet orders; IRR-label/toman numeric semantics require reviewed source contract, not guessed division or relabeling |
| 3819 empty checkout snapshots | Keep immutable source evidence; current order projection rejects | Historical price/discount/FX/payment terms unavailable in these rows; do not reconstruct from current products |
| Remaining 1097 nonempty snapshots | Structurally available for exact historical validation | Not all are representable; per-record money, method, precision and ownership validation still required |
| 406 zero-value transactions | Separate zero-evidence reader exists | 336 are IRR; current zero-order projection is USD-only and cannot silently drop or relabel these rows |
| 4725 deposits / 122 crypto attempts | Source records and statuses remain immutable evidence | 3717 deposits and 110 crypto attempts lack a fulfilled-order link; existing bank/direct-checkout import cannot invent origins. Pending invoice state is not settlement proof |
| 123 discounts / 74 full-discount codes | Preserve original counters and ownership evidence | 21 unowned codes, including 11 full-discount codes; paid prepaid classification/source-payment linkage is not proved by percentage or possession |
| Delivery headers, parts and 901 attempts | Added attempt reader/coverage, encrypted archives, parent mapping and reconciliation; no runnable sends | Protected record-wise import, missing original media/content and explicit historical compatibility review |
| 5188 inventory ciphertexts / 33 service payloads / supplier credential | Original ciphertext retained; missing keys stop field roundtrip | Protected original and rotated Fernet keyrings; record-wise decrypt/re-encrypt/compare; no plaintext fallback enabled |
| 4189 receipt paths and three product media references | References counted without exporting their values | Original file bytes, integrity/access checks and protected recovery storage; Telegram references need correct bot custody |
| Nine unassigned source units | Remain in protected source copy | No stock is admitted as saleable; deliberate stock ownership/cutover decision remains separate |
| 88053 legacy activity events | Source totals retained; no raw detail published | T089 reviewed vocabulary/privacy/original-time mapping and retention reconciliation |
| Historical supplier/preorder partial obligations | Empty relevant source tables documented | These backups supply no actual partial/preorder witnesses; T096 cannot be closed by absence or synthetic target tests |
| Remaining source tables | Retained in original backup; unmapped counts explicit in aggregate artifact | Source sessions/auth, jobs, broadcasts, settings/identity, FX, AI/support and other records need reviewed archive/import/retention decisions; no claim that 20 reader families cover all 56 tables |

## Implemented checks and reproducibility
`ops/ci/inspect-legacy-backups.py --pg-restore /approved/path/pg_restore protected-copy-1.dump protected-copy-2.dump protected-copy-3.dump` decodes archives without a database connection and emits only finite aggregate fields, schema/migration hashes and collision counts. It hashes inputs before/after inspection, rejects malformed/truncated COPY records and nonfinite money, and suppresses raw subprocess errors. It never performs a restore or grants release readiness. Its [redacted output](evidence/legacy-backup-inventory-2026-11-05.json) deliberately says `restored=false`; the separate engine restore above supplies bounded restore evidence. Input order defines source numbers. Run only on protected offline copies; no production endpoint is accepted by this tool.

Nine Python regression cases cover null/escape/COPY termination, malformed/schema shapes, currency/snapshot/ownership exceptions, partial allocation, collisions, unknown-table redaction, immutable/output-only decoding, secret-free failures and invalid money. Existing CI discovers them automatically. The C# continuation preserves original ban times, scopes payment idempotency, and includes delivery attempts in coverage/archive/parent/reconciliation paths. The shared native fixture preserves original attempt generations, actor/time/outcome and known failure/success evidence without creating stock, fulfillment, payment, jobs or customer sends. Existing partial-price, missing-key, schema-drift, replay, financial and rollback tests remain mandatory.

Local Spec Kit prerequisites, 15 tooling checks, 14 evidence/inventory checks, all 53 bounded witness mappings and whitespace checks pass. Local .NET/Docker/Compose remain unavailable; exact-main GitHub Actions is required for managed suites, formatting, secret scan, locked restore, Compose and all five Native AOT executables. Do not infer those results from local Python or WASM source restores. No source rows or private keys are used in CI.

T063 is partially qualified for archive integrity, applied-name/schema consistency, isolated WASM restore and catalog-query execution. T064 adds bounded representation fixes and historical attempt evidence; no full actual source import ran. T066 has verified redacted source inventory and explicit exceptions; no complete source/target financial or obligation parity is asserted. **All three tasks remain unchecked.** T068, E3/E6, release and cutover remain gated. The reviewer checklist stays read-only; no approved business decision or canonical 28-table schema changed. Missing evidence blocks affected qualification only; eligible offline implementation remains authorized.

## Owner-confirmed mapping and server-local follow-up
Subsequent owner clarification maps prod1 to acc-abad (Iran), prod2 to kuala (outside Iran), prod3 to zikotech (Iran), confirms possession of original encryption keys/photos and notes some acc-abad photos are missing. This resolves the earlier missing human source-to-brand mapping, not actual bot/image/key/media verification. The owner wants the 40 IRR balances preserved exactly; operational discount activation is not part of this follow-up, while original records/paid obligations remain retained. Use the [local verifier](LOCAL_LEGACY_ASSET_VERIFICATION.md) against existing fleet files and immutable archives. Upload no photos or `.env` files; return only its redacted summary. Missing files are individual protected exceptions and do not blanket-block independent crypto or other-brand checks. No actual owner key/media run or cutover has occurred.
