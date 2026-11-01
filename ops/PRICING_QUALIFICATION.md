# T030 coupon persistence and FX qualification

2026-11-01. The owner instructed finishing T027 and moving forward after presentation of the concrete T029 policy matrix. Offline implementation follows that matrix and constitution 3.0.0; no new business decisions, production data/keys or live operations.

Qualification: **PASS** — 329 tests (121 unit, 149 integration, 59 contract), zero failed/skipped tests, zero build warnings/errors, clean formatting, secret scan, Compose render and five Linux Native AOT executables. Source `69b26cf8fa5e2652bdc7ca5580c3844284d4ce4e`; [CI 41286122762](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41286122762). All required checks passed; T030 is complete.

## Scope

PricingPolicy retains source-equivalent exact Nobitex USDT/RLS parsing and RLS/10 conversion, open-market/status/finite/bound checks, duplicate-key and response/depth limits, decimal/scientific input normalization without float conversion, legacy half-even cent discount rounding and final whole-total ceiling to 5,000 toman. Manual toman opt-out computes from its own price. Current-FX freshness rejects missing, wrong-source, future and stale observations while preserving equality at the configured cutoff. Historical totals are not reconstructed or stored on orders.

PricingStore implements:

- Independent current admin authorization and content-committed issuance audit/replay. Codes have a domain- and brand-bound HMAC lookup digest plus SC02 encrypted code/key ID tied to the actual discount row. Authorized membership display decrypts only its own brand and assigned/global scope; private buffers are cleared. Lookup case/whitespace normalization and persisted 20-attempt/minute membership budgeting contain no raw code in activity/audit records.
- One bound N-use prepaid code for one fully paid future-unit source, with required original member/product/order and 100% initial coverage. Database ownership/provenance and unique source guards remain authoritative. Campaign/goodwill batch identity stays separate; no transferable entitlement or new table.
- Whole-order held claims under brand/coupon/order locks, latest grant and current eligibility/cap checks, original immutable quote/version/rounding checks and source-preserving saved amounts. One order cannot consume two code identities. Orders and claims must be created in the caller's same transaction, so a rejected quantity leaves no failed order or financial effect.
- Exactly-once held-to-released or held-to-redeemed transitions. Fulfillment uses the original protected grant without rechecking edited cap/expiry/percentage. Genuine redeemed counters advance with real redemptions; owner usage adjustments do not rewrite them. Redemption needs the complete accepted obligation; release needs an expired/cancelled or explicitly resolved order. Customer text cannot authorize these transitions. Unrestricted owner editing APIs remain T046; their baseline guards are preserved and edits are exercised through synthetic fixtures.
- Per-brand hourly FX work with immutable typed origin and dedupe. The canonical jobs lease/attempt/expiry fences own refresh execution; brands retain only current rate/source/fetch/error state. Publication additionally compares current policy revision, advances it atomically and completes the same fenced job. Fresh data from another source or a future timestamp cannot suppress a Nobitex refresh. Failures retain the last valid rate and all manual prices, save a fixed safe error and use bounded local retry. No duplicate lease/history/rate table or DDL was introduced.

Claim/FX mutation failures poison an otherwise active session so callers cannot catch an error and commit partial work. All transport fetching remains outside transactions and outside this offline batch. No HTTP/provider client, runtime worker activation or credentials were added.

## Evidence and limits

The managed suite includes coupon encryption/scope, changed-input issue replay, persisted guess budget across reconstructed services, same-Telegram isolation across memberships, concurrent capped holds with rollback/no stock or wallet mutation, one-code-per-order, exact release, owner edits below outstanding claims with expiry/disable changes, original-grant replay, single prepaid-source issuance/foreign-owner rejection and complete-order redemption/idempotency. FX tests cover hourly dedupe, stale lease and policy revision rejection, manual price retention, wrong-source refresh and failure retaining the prior good rate. Pure arithmetic fixtures cover source cent ties, conversion order, zero totals, nonfinite/malformed/duplicate market data, freshness cutoff equality and signed/overcommitted capacity without integer overflow.

The native executable actually issues a keyed/encrypted coupon, resolves it under the correct membership and executes exact FX/cent arithmetic. Existing native atomic checkout, cipher, admin, outbox, schema and HTTP probes remain enabled. Normal hosts still exit 78 and readiness remains false.

The first run, CI 41285007864 at 6c3210c14637674a028680b1acd543184e2b8a47, passed all 121 unit tests and 145/146 integration tests. Its redemption fixture inserted a fulfillment manifest-generation reference before the referenced order tuple existed (SQLSTATE 23503); the setup now creates that tuple before the unit and authorizes release only after whole allocation. The schema constraint was retained. CI 41285290989 at b816834ae9f147103052a92ce7c2d03e73095181 then passed 327 tests and all native probes. CI 41285640793 at 4451851af32ac6b472659291c3228b7f3d8a836c passed 328 tests and all native probes with the one-code-per-order guard. Final review then added a regression for a coupon becoming active after the purchase transaction began; the immediate local checkout gate now checks current database time rather than the transaction-start timestamp. The final run above includes that regression.

Local .NET/PostgreSQL/Docker are unavailable; managed/native/Compose execution comes from the identified CI evidence. Local git diff --check passed. The canonical 28-table SQL SHA-256 remains fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d. No duplicate 0004 migration, source import, live FX/Telegram/payment/supplier call, staging/production mutation or deployment occurred. Reviewer checklists are unchanged.

T031 automatic best-coupon quote selection and T032 catalog/supplier availability integration are subsequent work. The initial immediate local-wallet T033 slice remains conservative: applicable coupons or unsupported routes return Unavailable rather than bypassing those missing integrations. T033, T034 delivery and T035 full dispatcher crash/race qualification remain unchecked. Pure pricing and persistence qualification do not claim completed customer commerce or live provider compatibility.

## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | 8cca654ecf510e412e0ea25e03b75fceaaa8f80258f8e9d10620299b03759b5f |
| BotHost | 1d7205bd78de8798d11d067113fec53e8c6daf45e0e27323cd4b586408826a22 |
| JobHost | aed06c4bb5995fd2b1c4506b49f3bb77f355d3b77194c09dbb8fc98c30b3cfc4 |
| SchemaRunner | d4ca6545f5622bd1075b1015d4b3343fae2924f043c08e24a53dfa6f1ea99b6a |
| AotSmoke | 1c6df6d9b50c19a535b78e187908957cfc4ffe0d9f83c5554cbc2b081f5c6fa7 |

The subsequent completion commit changes documentation only; executable evidence is bound to the source/run above.
