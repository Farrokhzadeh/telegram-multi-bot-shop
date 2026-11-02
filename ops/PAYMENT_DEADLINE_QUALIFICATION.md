# Offline payment deadline qualification

2026-11-02. **PASS for T082–T084 synthetic/disposable scope**. Qualified source `169ccf4d0b2238e4cf30b6946dce717fe12136f8`; [CI 41453333729](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41453333729). **587 .NET tests (134 unit, 343 integration, 110 contract)**, **20 Python checks (15 Spec Kit, five evidence guards)** and all five Linux Native AOT executables passed, with zero failed/skipped tests and zero build warnings/errors. Formatting, redacted secret scan, locked restore and Compose checks passed. **74/96 tasks complete, 22 unchecked. T085 remains open** for the full acceptance matrix and actual invoice/merchant/provider-hosted identity qualification. No real invoice, callback or Telegram activation is qualified.

## Schema, expiry and replacement

T082 qualifies the existing typed deadline/payment/claim/delivery fields, surrogate membership keys and one partial unique awaiting-order index. Same Telegram identity in different brands and different members in one brand can hold separate pending orders; a second pending order for the same membership is rejected. Original deadline mutation is rejected. No duplicate migration, new table, JSON column, index relaxation or alternate field authority was introduced; the canonical complete T008 baseline remains authoritative.

[PaymentExpiryService](../src/Platform.Payments/PaymentExpiryService.cs) uses the database clock and the existing member/product/brand/order/payment lock order. Only an overdue evidence-free awaiting order expires. Strict equality is retained; early local work defers within the existing 1–300 second outbox delay limit. Expiry, full held-coupon release and the durable expired status intent commit together. Receipt, first observation, review/confirmation, accepted money and original identities/quotes/clocks remain protected. Native/managed probes prove one expiry, no expiry-time debit/credit/allocation, one whole returned claim and original-invoice late USD recovery once without revival.

New valid checkout cancels only its own unobserved awaiting slot. Separate reviewed/confirming/paid orders retain original evidence, claims and liabilities and may coexist with one independent awaiting order. Invalid funds/quantity/source/current-policy validation still preserves the old order through rollback. Protected claims cannot donate capacity to another order. The localized first line prominently states payment validity for 30 minutes; the original order reference and persisted UTC deadline remain visible. This work does not recreate wire instructions, decrypt cards, create invoices or reset/reprice a quote.

## Status dispatch and recovery

[PaymentInstructionStore](../src/Platform.Persistence/PaymentInstructionStore.cs), [message persistence](../src/Platform.Persistence/PaymentMessageStore.cs) and [PaymentMessageWorker](../src/Platform.Telegram/PaymentMessageWorker.cs) use typed order_status intents for external orders with persisted deadlines. Bot, member, order, private destination and immutable saved target are checked. English/Persian status/deadline/reference text comes from a fresh authoritative read immediately before dispatch. A stale expiry lease cannot expire accepted payment; an older queued status cannot render Expired for an accepted order. Replaced orders keep a separate Cancelled — replaced result and original identity.

Known rate-limit responses preserve the same logical intent and defer the bot. Expired edit leases recover only their original target and current state; stale owners and wrong message receipts cannot finish current work. Unknown new sends pause rather than automatically resending. Late known receipts are retained under their original attempt fences. A current independently authenticated administrator must authorize/audit a new-message fallback; repeated commands reuse it, and known late success suppresses the queued fallback. Known successful duplicate sends are suppressed. Remote exactly-once receipt is not claimed.

Superseded unsent status projections are coalesced for that order without cancelling domain/payment/expiry/credential work. A newly learned saved anchor creates a fresh immutable edit intent rather than mutating an existing target. Unknown metadata state remains durable. None of these paths credits/debits money, allocates goods, issues entitlements or changes coupon claims.

## Complete-manifest message ownership

After the complete manifest is release-authorized, later status dispatch is suppressed so it cannot replace credential content. Credential edits of a saved message wait for an active metadata edit of that same target. An expired or uncertain old metadata edit blocks automatic reuse of the original credential target; the existing explicit audited credential recovery can send the same protected complete manifest to a distinct new message. It does not allocate again, redeem twice, change a generation/digest, release partial goods or alter financial terms. Original uncertain evidence is retained.

Managed and Linux Native AOT tests exercise both known completion and uncertainty, alongside public instruction delivery, replacement edits, current-state payment reread, rate-limit backoff, audited fallback, late receipt suppression, expired lease recovery, foreign scope, stale owner, wrong target and timeout cases. The metadata/credential ordering test was added after review of an earlier green checkpoint exposed the possible overlap; the final qualified source includes and passes that regression. Normal hosts remain uncomposed/fail closed.

## Checkpoint corrections and artifacts

[CI 41449539115](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41449539115) found a too-large early-expiry delay and an obsolete assumption that no initial instruction existed before settlement. The retry is now bounded to 300 seconds without changing the outbox guard. The rollback assertion now proves the initial instruction remains while **zero** settlement status intents, credits or coupon releases commit after the injected failure. [CI 41450813335](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41450813335) passed before the additional message-ownership review; the final checkpoint above reran the complete pipeline with those extra guards and regressions. No financial, ownership, replay or complete-manifest constraint was weakened.

| Qualified artifact | SHA-256 |
| --- | --- |
| Api executable | `4f0994874e65d7f66e04653059d4434c0a5ad17dd4431b15607d1972a39b0157` |
| BotHost executable | `996f1952d1d9de3cc3319774797738c88a2835a8102ac7a5fc950e02457f7c7b` |
| JobHost executable | `f666f72e3244df22cb18b41232b4010fd553ac36e3e76b34c6c57d33472149e5` |
| SchemaRunner executable | `cb395ea6139412369619bb37df631055974a9f85c481b97fc6fd41396b554926` |
| AotSmoke executable | `43c2f2a8cbcfaba21c932f0e9c4f27c4eaf8ee569c3466c4e96762927733a66d` |
| Generated administration contract, 29 methods | `7677e2439a9ead3a27b443f25891ce4f11ed70f20297aa6ead66cff3ea6fff46` |
| Scenario witness report | `edec3791136b92e18bc9ca8415af9ef3ea1b3134364f0f55bba390d24adc254c` |
| Combined synthetic commerce report | `b3f00232aacc4ca0991a1b017a4bbf26aca104e05a106ee84bd2b7017ef7afce` |

The scenario report remains synthetic-witness-only, with fullSuccessCriteriaVerified=false and releaseReady=false. SC-008 human acceptance and SC-040 activity retention remain unexecuted. The combined report still has releaseReady=false. No actual source/provider/environment or complete acceptance claim follows from overlapping synthetic witnesses.

## Open gates

T085 has expanded regression coverage but remains unchecked for the complete matrix and actual invoice HTTP creation/merchant/provider-hosted identity qualification. Normal callbacks, provider lookup/Telegram transports, runtime routing/scheduling and archive production durability remain unqualified. T057 and T063/T064/T066/T068, plus T075/T076/T079/T080/T081, remain incomplete/gated. T079's protocol exists but no human result was run or published. Next available offline follow-up is T086.

No real credentials/source rows, live Telegram/payment/supplier/FX/AI services, staging/production mutation, migration/cutover, release or deployment. The independent AI runtime remains a separate future service. The canonical typed/no-JSON 28-table SQL is unchanged, digest `872ba3f44d7ec2a7fc8c4cd0ffc1946d07ad1bf4045d0bdd93abcf111ca5d36c`. Reviewer checklist remains read-only at 92/126, digest `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`. Constitution 3.0.0 and approved business decisions remain active. Local .NET/PostgreSQL/Docker are unavailable; CI supplies execution proof. Spec Kit prerequisites, 15 tooling tests and git diff --check passed; no extension hooks exist.
