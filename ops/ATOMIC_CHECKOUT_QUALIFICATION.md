# T027 atomic wallet-checkout qualification

2026-11-01. Owner instruction: finish T027 and move forward. [CI 41283929989](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41283929989) passed at `5a865c33bddddcfa195eddfbe510b9ad43413897`: 298 tests (101 unit, 138 integration, 59 contract), zero skipped/failed tests, zero build warnings/errors, formatting/secret/Compose checks and five Linux Native AOT executables.

T027 tests exercise the actual CheckoutStore transaction, not test-only purchase SQL. The prerequisite immediate local-wallet slice of T033 was implemented to make these tests meaningful. T033 remains unchecked: automatic discounted/free checkout, pending-order replacement, channel-gated checkout, mixed/supplier, preorder and prepaid dispatch are not claimed. Unsupported paths fail closed, including applicable coupons until the pricing/dispatcher integration is qualified. Normal hosts remain disabled; no customer endpoint or transport was activated.

## Verified behavior

- A synchronized 50-buyer race across three brands and one shared credential produces one order, one USD debit, one allocation, one protected fulfillment and one durable prepare_manifest job. Other buyers get OutOfStock without effects.
- Insufficient/zero balances and incomplete whole quantities leave order, wallet posting, allocation and work unchanged.
- Failure after the purchase writes rolls all effects back. A ciphertext failure poisons the session so catching it cannot permit a partial debit to commit.
- Reconstructing the service or concurrently replaying the same request returns its durable result without another debit/allocation/job. A dropped server-confirmed COMMIT acknowledgement requires reconciliation; replaying the original identity finds the one committed purchase. Changed quantity and foreign-brand reuse fail.
- Unsold inventory is decrypted only under the independent inventory grant and reencrypted for the winning brand and fulfillment identity. No content is released; manifest preparation/delivery are later tasks.
- Blocked customers, hidden listings, changed quotes and unsupported dispatcher routes cannot enter the local debit path.

The request UUID is the new sole orders.id reference. An immutable, brand/member-bound audit commitment protects its original product, quantity, quote/revisions and inputs independently of activity retention. Database product/member/wallet locks serialize acquisition; no remote call or retry callback runs inside the transaction. SQL and the approved 28-table inventory are unchanged. This is offline qualification, not live readiness or a completed dispatcher.

## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | 4a7a58b557f2306e7ffc14e991127b9498bcbdca166939c8b8bbaaab7bc329c3 |
| BotHost | 460052a4d7fc9679439a51685ea8c62c166428ca1811a15263ae4a9beedb1478 |
| JobHost | 48375496df1ca5315866a4e1501bfae2ae192bfbc06efd289a396c97fbf22429 |
| SchemaRunner | d2a75953b8c6e8a694ecf52bb715c1fde0911af5c0930e6812c03478a5de400c |
| AotSmoke | bfd6c58c3ef1503d5a58e862a2a499494180cb2325ee6d97e79ee4fcbad1e419 |

The native probe executed an actual purchase, encrypted allocation, durable work and replay against a disposable database. .NET/PostgreSQL/Docker are unavailable locally; execution evidence comes from the identified CI run. Local git diff --check passed. No deployment, real credentials/source rows, live Telegram/payment/supplier/FX calls or production/staging changes occurred. Reviewer checklists remain unchanged.

The owner's continuation after presentation of the T029 matrix authorizes moving into its documented retained/changed policies for offline T030 implementation. It changes no business decisions and authorizes no live operations.
