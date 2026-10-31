# T015 typed durable work

Qualified at source commit 41ed3ef0dd741de9447c2bf02f8e9cc6aabaf4eb, tree 9ffff7c0b70eab7175500551f2f814df9809903a. Trusted service persistence only: no worker loop, customer endpoint, remote call, live credentials or deployment.

All 18 canonical job kinds have explicit origin factories. Order/payment/supplier/delivery/campaign/coupon/support work has one typed origin plus its checked brand. Shared product/source work and platform cleanup never invent a customer brand. FX work is brand-owned without another origin. Supplier audit ID and expected origin revision occur together and remain immutable. Database FKs/cross-row guards remain authoritative. No table, JSON payload, EAV field or schema change is introduced.

Enqueue uses the caller's business DbSession; the caller explicitly commits or rolls back both together. A transaction-scoped advisory lock serializes each kind plus deduplication UUID, then checks for the original immutable intent before inserting. Duplicate terminal work returns its existing ID and is never rearmed. A different origin under the same identity poisons the transaction. Hash collisions only serialize unrelated work: the unique canonical dedupe key remains authoritative. Multiple enqueues in one business transaction must follow deterministic identity/lock ordering; deadlock/serialization failure requires the existing explicit whole-transaction recovery policy, with no automatic callback replay. Concurrent duplicate callers return the original under the default Read Committed isolation; stricter isolation may require an explicit whole-transaction retry after rollback.

Claims select one due job of a reviewed kind using SKIP LOCKED only on scheduler rows, never on paid FIFO heads. Each lease has a UUID worker identity, incremented durable attempt generation and server-clock expiry. Renewal, completion, failure and retry compare ID, owner, generation and strictly unexpired stored expiry in the update itself. Zero affected rows poison the transaction and stop the stale owner, rolling back any accompanying local business writes. Audited supplier work also compares the retained expected revision to its actual supplier request revision. Worker UUIDs must be fresh per worker lifetime; a lease is not a purchase/send permission.

Expired local work can be reclaimed. Supplier submit/reconcile/export and Telegram-send work cannot be reclaimed from an expired lease or generically retried by this store: explicit domain recovery/reconciliation must preserve original remote identity and authorization. Expired remote work stays leased as evidence for that later domain protocol. Local retry has an explicit 1–300-second delay, allowlisted failure code and caller-selected positive maximum attempt count; reaching the bound fails that original job. Crash recovery itself does not claim a remote failure or implement an autonomous retry loop.

Intent/status guards validate origins on every update. In particular, submit_supplier requires a pending request. Future domain handlers must fence/complete scheduler work before changing that origin's state in the same transaction, or use a reviewed explicit domain recovery path. Scheduler helpers never relax that guard, mark a paid order refunded/fulfilled, clear administrator review, change a cost ceiling, send content or resubmit a purchase. Local business transitions and authoritative remote evidence remain later service tasks. A scheduler lease or success row is not evidence that a supplier purchase/send succeeded.

Disposable tests cover atomic rollback, concurrent deduplication and claiming, immutable-origin collision, expired/replaced lease owners, poisoned stale-owner transactions, renewal, delayed retry/attempt exhaustion and expired Telegram work remaining blocked. A real native probe exercises enqueue/deduplication, server-clock reclaim, generation fencing and renewal against the disposable PostgreSQL service. All data is generated synthetic qualification data; no live messages or financial effects occur.

Callers must abandon the transaction on any operation/validation error and commit only after every required business write and enqueue/fence succeeds. This API cannot authorize an application to catch a rejected enqueue and intentionally commit incomplete work. Primary references: https://www.postgresql.org/docs/18/explicit-locking.html and https://www.postgresql.org/docs/18/sql-select.html.

## Observed evidence

[CI run 41235165153](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41235165153), job `124637278257`, passed all steps: 188 tests (98 unit, 52 disposable PostgreSQL integration, 38 contract), zero failed/skipped and zero warnings/errors, locked restore/audit, secret scan, formatting, Compose rendering and five Linux native executables. The actual native outbox probe passed enqueue/deduplication, server-clock reclaim, renewal and stale-generation fences. T015 is complete for this foundation scope. No remote sends/purchases or live financial operations occurred.

| Native executable | SHA-256 |
|---|---|
| Api | `3c9db4ac2f7fcbf7f225aa8cc9c4ed51a98b98981c04ef5fc92cd268624832e4` |
| BotHost | `6803861e5dc8915f7f7c270e076956782e58fe8b4163977fe0d40619af80f801` |
| JobHost | `3daa3aa2d1a911fa9138dd3ccbda0353e765c8964164782d729242de0a5a7d54` |
| SchemaRunner | `b9b6c82b42e7d6cb76327f68425a3c89c340dad42ce2e1d744100e24e1a140c0` |
| AotSmoke | `a2e80193e2ea0f4aa0871bae45a3624f4f8f44cb8b388e7508551dc37a2bccb6` |
