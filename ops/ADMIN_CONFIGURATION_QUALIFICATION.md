# T043–T044 administration configuration qualification

2026-11-01. Owner-authorized offline continuation directly on main with task-grouped commits. Qualification: **PASS** — 501 tests (129 unit, 278 integration, 94 contract), zero failed/skipped tests, zero build warnings/errors, clean formatting, secret scan, Compose render and five Linux Native AOT executables. Qualified source `587aa6b0354801ab707c8bf1ef65f19590d2b970`; [CI 41330776656](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41330776656). T043 and T044 are complete. T001–T044 and T078 complete: 45/96, 51 unchecked. Reviewer checklist remains read-only at 92/126. No real keys/source data, live Telegram/payment/supplier/FX calls, staging/production mutation or deployment.

## Independent authorization and write-only credentials

BrandEndpoints provides opt-in authenticated metadata, token rotation and required-channel configuration routes. The trusted host resolves the selected brand to its existing registered bot identity; customer claims/cookies do not grant administrator authority. Authentication rechecks independent sessions on every request, and transactional writers recheck active authorization at commit. Normal hosts do not mount these routes and remain fail closed.

BotTokenVault validates bounded token syntax, binds the numeric bot identity to its original registration and encrypts with the existing brand/brands-row/bot_token_ciphertext SC02 context. Only the trusted transport interface can open the protected token; ordinary reads return metadata without plaintext, ciphertext or key IDs. Disposable token buffers and parsed request bytes are cleared; diagnostic string representations redact credentials. Request DTO strings necessarily remain subject to managed-memory lifetime; no claim of guaranteed erasure of immutable strings is made.

Rotation requires a matching bot profile from an injected trusted verifier outside database transactions. The same command can replay without verification or a repeated write; a changed token, revision, actor or scope conflicts. Failed/mismatched verification and concurrent configuration changes preserve the previous encrypted credential. Live Telegram verification, runtime credential reload and provisioning remain unqualified adapters/composition work.

Required-channel changes canonicalize the complete bounded set, validate negative chat IDs and HTTPS links, verify bot eligibility outside transactions, then replace the active list under the original policy revision. Removed channels remain inactive historical rows. Every committed change advances policy_version, invalidating old channel/checkout proofs. Stale writes, changed-input replays and verifier failures preserve the existing list. The transport verifier is synthetic in qualification; no channel membership or bot credential was checked against Telegram.

Both writers append the existing typed administrator audit and configuration mutation atomically. Command UUID/content, actor, brand, before/after policy revision and reason remain durable; token values and channel verification payloads do not enter audit text. No extra business table, JSON column, startup DDL or staging/production migration is introduced.

## Owner coupon-edit contract

T043 exercises a narrow T046 prerequisite, PricingStore.EditAsync. Current independent administrator authorization, original brand scope, command commitment and expected coupon revision govern edits. Percentage, total/per-member caps, effective used count, dates, activation and scoped eligibility remain owner-editable, including reductions below held/redeemed units and changes to paid prepaid entitlements. Effective used count changes usage_adjustment_units; immutable discount_uses and actual redeemed_count remain authoritative. Original held grants and paid-source provenance remain unchanged.

Each successful edit appends one typed discount_revisions post-state and linked actor/reason/before-after audit. The prior immutable revision supplies the old state. Replay adds neither revision nor audit; changed-input replay, foreign membership, stale revision and invalid values reject. No cap floor, prepaid settlement veto, fake refund or off-bot financial inference is added. The broader T046 order/wallet/resolution and coupon HTTP endpoints remain pending; these service tests do not complete T046.

## Executable coverage

Contract tests exercise actual free checkout, complete-manifest redemption, a second held grant, unrestricted cap/expiry/effective-use edits, paid-source entitlement edits, immutable redemption rows and matched old/new audit facts. Loopback HTTP tests cover unauthenticated rejection, token-redacted writes/reads, idempotency, changed-input conflicts, malformed/unknown fields and immediate session revocation.

Integration and Native AOT probes run actual encrypted rotation/reopen, channel replacement, command replay and audit counts against disposable databases. Additional integration tests cover stale channel proofs, removal history, unchanged foreign brands, wrong bot, failed/racing verification, foreign-scope/tampered ciphertext and cleared/disposed token buffers. All provider callbacks are synthetic functions; no external transport is composed.

The initial build/format checks passed, then integration tests found one new assertion expecting a raw FormatException instead of the existing redacted DbOperationException(InvalidData) for changed audit content. Assertions now check that precise safe failure; the new HTTP adapter maps it to conflict without modifying the database abstraction or weakening audit/financial constraints. The test step stopped before contract/native execution, so no complete first-run pass is claimed.

Local .NET/PostgreSQL/Docker are unavailable. GitHub Actions supplies the source-bound executable evidence recorded here. Local Spec Kit tooling passed 15 tests and git diff --check passed. Current 28-table typed no-JSON fresh-disposable SQL SHA-256 remains `09c59085bae3a4d8540e15352e8f3ddec879d2b35eec36188e79e9f444da6ea2`.


## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | 6ba3bf32f4e991589dac4928fa683797a1b671a032deb3d88fbbf8ce8ee7217a |
| BotHost | c1826963162fd256d70702b055e3a9ebb2622416e67b4f8abae3f32fe8ba597e |
| JobHost | 56d925be10fbda6c61f36a9efd4c27088d7dc99b194b1ebbc7055f3492cbc3da |
| SchemaRunner | ccde56e2626fd8b88e6fb93bc1a3377aab85b93f4c18537ae8019176f599fe6a |
| AotSmoke | bf524f5678aa7d271f7f665385eb30c51ff671a302ce56102d0f3fb920c66a1d |

Native smoke executes encrypted token rotation/reopen, synthetic channel verification/replacement and audited replay against a disposable database alongside all earlier qualified probes. The subsequent completion commit changes documentation only; executable evidence remains bound to the source/run above.
