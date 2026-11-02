# Optional AI boundary qualification

2026-11-02. Qualification: **PASS** — **571 tests (129 unit, 332 integration, 110 contract)**, zero failed/skipped tests, zero build warnings/errors, clean formatting, secret scan, locked restore, Compose render and five Linux Native AOT executables. Qualified source `93ac422ffc7be846d7762d4dc06ab995d3d18514`; [CI 41399216474](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41399216474). T071–T073 are complete: **69/96 tasks, 27 unchecked**. Reviewer checklist remains read-only at 92/126; constitution 3.0.0 and approved business decisions are unchanged.

## Qualified scope

The opt-in [version-1 context/handoff contract](../specs/001-multibrand-redesign/contracts/ai-boundary.md) has opaque expiring/revocable service bindings and a read-only allowlist. A trusted host authentication ticket with the exact service scheme and configured single subject pins one brand member and optional order; HTTP selectors cannot change it. The store rechecks active/unblocked membership and order ownership and does no decryption or activity/flow writes. The context excludes wallet values, payment evidence/provider identity, stock, entitlements, credentials, private staff chat, customer profile and support history.

Shared managed/native probes compare every column of all 28 tables before/after context reads and rejected HTTP requests, prove PostgreSQL rejects an actual UPDATE with SQLSTATE 25006, reject foreign-brand and same-brand foreign-member orders, invalidate cached revoked scopes and qualify actual loopback service/method isolation. Unknown/expired/revoked/duplicate identities, raw request principals, wrong schemes, cookies, query/header/body selectors and attempted administrator wallet mutation fail. Only the exact context/order field allowlists serialize. Existing paid checkout and verified private Telegram human support succeed with AI disabled and zero adapter calls. Previous support/payment/recovery suites also remain part of this complete checkpoint.

The optional client bounds question/advice text, uses an independent deadline and one outstanding call per brand, isolates circuits and exceptions, propagates caller cancellation and returns a human-support disposition for disabled/missing/unavailable clients, invalid context/replies, timeouts, busy/open circuits and explicit handoff. Tests prove cooldown recovery, cross-brand isolation and late-reply suppression even when cancellation is ignored. Cancellation callback failures cannot escape the timeout path. Advice remains untrusted plain text and cannot execute a command or create a support ticket. Actual support requires verified customer ingress; administrator actions retain their independent authentication/audit checks.

No SDK, model, prompt pipeline, real transport or normal-host activation was added. The frozen 29-method administrator contract remains unchanged. The read-only option is transaction control in DbSessionFactory; the data-only statement guard and existing mutation/replay/ownership/complete-manifest constraints are unchanged. Canonical typed/no-JSON 28-table SQL is unchanged, digest `872ba3f44d7ec2a7fc8c4cd0ffc1946d07ad1bf4045d0bdd93abcf111ca5d36c`.

## Checkpoint corrections

[CI 41398239582](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41398239582) built without warnings/errors but failed the whitespace check before tests; the observer catch formatting was corrected. [CI 41398624444](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41398624444) found an escaped separator literal in the synthetic snapshot helper; the existing strict statement guard correctly rejected it. The separator now uses an explicit bound parameter. No statement, financial, ownership or replay guard was weakened. The qualified run above reran the entire pipeline successfully.

## Artifact digests

The qualified run produced the following SHA-256 digests. The Native AOT helpers include the new read-only context/HTTP and AI-off support probes.

| Artifact | SHA-256 |
| --- | --- |
| Api executable | `8850520ba606dd3f402a9b2769de47300185d3a6cb6b773d6c68b0bda8a50f6b` |
| BotHost executable | `050a45b129adc4144b1ecb6d73463cd64d22f27704efd98c69675e56ac70b07f` |
| JobHost executable | `a2f6b96004f074fff046264b9d7083885d16a515996386a90cfbd32f5e0d711a` |
| SchemaRunner executable | `318adf005447e2a628171697916e310c3ef0937118adb6db29f42efb7a0f5cb5` |
| AotSmoke executable | `a41cb2ea4e761c494d28840a8feff62d192de4891ff1db776c51ca9026be4598` |
| Generated administration contract, 29 methods | `7677e2439a9ead3a27b443f25891ce4f11ed70f20297aa6ead66cff3ea6fff46` |
| Synthetic wallet-projection reconciliation | `3a35e255df3ed52e6412619a6b24fe48fc63af65d9611b3030075fe82c1fb90c` |

The wallet-projection report still has ReleaseReady=false and does not close T066. No real AI adapter, provider authentication, hosting, production authorization grants, quality evaluation or rollout is qualified. Normal hosts remain fail closed. No real keys/source rows or live Telegram/payment/supplier/FX/AI calls, staging/production mutation or deployment. T057 and T063/T064/T066/T068 remain incomplete/gated; T074 onward is pending. Local .NET/PostgreSQL/Docker remain unavailable; GitHub Actions supplied execution evidence. Spec Kit tooling passed 15 tests; prerequisites and git diff --check passed; no extension hooks exist. Reviewer checklist digest remains `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc` at 92/126.
