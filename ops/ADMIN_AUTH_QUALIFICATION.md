# T016 independent administrator authority

Qualified at source commit 32ebaef3c3aef87d7af595fbd9d2792cc2a096f6, tree b852c4fa510785e47e6aa2b30b3a4efe5cd943ab. Offline/synthetic foundation only. No public account bootstrap, online login route, real credentials, Telegram calls/messages, host activation or deployment.

## Passwords, sessions and transport

Admins are independent global backend identities. Customer membership, bot identities, Telegram/group claims, usernames and cookies never grant backend authority. Password mode uses BCL PBKDF2-HMAC-SHA256 with a random 16-byte salt, 600,000 iterations and 32-byte output. New credentials require 15–128 Unicode characters, strict UTF-8, with no normalization or composition rules. Verifier format is bounded/versioned; unknown/malformed/downgraded formats fail after dummy work. Verification work is capped at 1,200,000 iterations. External authentication mode is denied until a separately verified provider integration exists; there is no subject-string fallback.

Session tokens are 32 CSPRNG bytes, exported explicitly as canonical 43-character base64url bearer values. Only SHA-256 digests enter admin_sessions; raw bytes are cleared on disposal, while caller/framework-owned transport strings cannot be guaranteed zeroed. Secret/session objects have redacted ToString and no raw-secret property. Publish a newly issued token only after the caller's transaction commits successfully; unknown commit requires reconciliation, never automatic credential callback replay. Session lifetime is explicit, 1 second–8 hours, with server-clock expiry and an immutable auth-version-at-issue.

Authenticate checks active account, non-revoked/unexpired session and matching current auth_version. Every privileged command revalidates that proof inside its business transaction, locking involved admin rows in UUID order before business locks/writes. Cached contexts are not authorizations. Failed validation poisons the session so earlier local writes cannot commit. Revocation increments auth_version, clears pending challenges, optionally disables/removes Telegram binding, and revokes target sessions atomically with an audit event. Logout revokes the current session. Existing in-flight authorized transactions serialize before revocation commits; later commands fail.

AdminGuardMiddleware requires server endpoint metadata and a single canonical explicit Bearer header; it ignores cookie/customer/bot/group/admin-looking claims. A supplied database authenticator returns the opaque actor; request context is cleared on exit. The qualification host intentionally supplies no positive online admin verifier and proves bot/header access is rejected. Native database tests exercise actual positive authentication. No cookie authentication or browser mutation path is enabled. Private TLS/routing, online rate limiting, bootstrap provisioning and any future cookie/CSRF policy remain required transport/release work before exposing credential endpoints.

## Immutable redacted audit

AdminAuditStore appends typed action/entity/scope/revision/channel flags, actor ID, request correlation and an immutable command commitment. Reasons are controlled codes; raw credentials, private message bodies and arbitrary before/after objects are not accepted. Free-form operator notes in later business services must use separately protected content rather than this ordinary audit text.

The command ID is the durable audit identity. The fingerprint binds actor, action, target/scope, reason, revisions, channel flags and a server-produced 32-byte commitment. Request correlation is excluded so a retry may have a fresh request ID. Callers must commit audit and business writes together; an already-recorded result MUST suppress those writes and return the original result. Different data under the same identity poisons/rolls back the transaction. Never derive commitments from low-entropy raw passwords or secret text; credential operations need their own reviewed idempotency protocol. Typed business services must commit to their complete nonsecret terms. Database reference guards verify actual entity/brand scope, and update/delete triggers preserve append-only history. One command per transaction and deterministic admin/business/idempotency lock order are required; the library performs no automatic retry or external callback.

## Telegram binding is two-channel possession, not backend login

An authenticated admin begins a short-lived challenge for its own account and candidate Telegram ID. Only a nonce digest bound to admin ID, current auth version, candidate ID and stage is stored. The challenge never belongs in a URL, ordinary log or activity-event body. A verified private-ingress adapter asserts the actual sender/private chat, rejects bots/groups and binds its proof to that message's nonce. This assertion type does not verify Telegram transport: T022 must establish the authentic update source before constructing it; ordinary request DTOs cannot substitute for that adapter.

The bot channel advances stage 1 to stage 2 exactly once, without granting backend privilege. The independently authenticated admin session must then present the confirmed challenge and expected epoch. Completion audits, sets the verified tuple, clears pending fields and increments auth_version, requiring fresh backend authentication. Replays with a fresh authenticated session return the existing completion without repeating it. Wrong sender, nonce, stage, epoch, expired challenge and revoked backend session fail. Public contact handle stays unset; later per-brand contact configuration must separately verify its display value. Staff lookup checks the live active backend identity and verified binding; explicit Telegram revocation or account disable removes its authority. Group membership alone does nothing.

## Schema and qualification

T008 already defined admins, admin_sessions, audit_events and binding fields. This task reuses/qualifies that canonical baseline rather than creating a duplicate 0002_auth table migration or a 29th table. No SQL schema digest/revision changes. The new Persistence-to-Security reference is acyclic; lockfile edits describe project edges only, with no package version changes.

Tests use generated tokens and synthetic password/verifier data in disposable databases. They cover salts/verifier rejection, canonical secret handling, session hash storage, disabled/external/unknown/wrong credentials, expiry/logout/current-epoch revocation, cached-context rollback, audit collision/immutability, actual positive bearer guard and rejected customer claims/cookies, two-channel completion, replay, wrong sender, expiry/epoch mismatch and immediate staff revocation. A published native probe repeats real database session/audit/binding/revocation operations; actual HTTP probes reject bot identity at the administrator boundary. E2/E3/E5/E6 remain unqualified; E4 is limited to recorded probes.

Primary references: https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html and https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2?view=net-10.0.

## Observed evidence

[CI run 41237167656](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41237167656), job `124643536964`, passed all workflow steps: 205 tests (101 unit, 62 PostgreSQL integration, 42 contract), zero failed/skipped and zero build warnings/errors, secret scan, locked restore/audit, formatting, Compose render validation and five Linux native executables. The actual native database probe passed session issuance/current-epoch validation, audit deduplication/collision, staged Telegram binding and immediate revocation. Native HTTP proved bot/admin-looking headers grant no backend authority. T016 is complete for this synthetic foundation scope; actual verified Telegram ingress and online credential/transport activation remain later tasks.

| Native executable | SHA-256 |
|---|---|
| Api | `a994beced9dbd3cc130767b1738c0f496f96fd94a250a834b34a688ae89027d6` |
| BotHost | `ffadb2a6e3ec55ebf3587c2ca13b5dc30a19797739ff2b374027e485dc7337c9` |
| JobHost | `ff8d89d095aa69b4113d756368f5f388fe49f8cd1a1940355ca0720f5c08a922` |
| SchemaRunner | `454b5c472d282e43d3e5bb4d8bcd055b2997663c94d24eb9e70116a0b7cc47bc` |
| AotSmoke | `406c40ba8c3f65b0b08a5df58f93ae20dc1cc0011383256582300914b80a6a94` |
