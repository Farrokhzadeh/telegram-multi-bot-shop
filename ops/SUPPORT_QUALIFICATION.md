# T022–T026 review and human-support qualification

Reviewed 2026-11-01 under the owner's instruction to inspect T022–T026, fix issues and continue offline implementation on main. No deployment, real credentials/source rows, live Telegram/payment/supplier calls or production/staging changes.

## Evidence

Qualification: **PASS** — 284 tests (101 unit, 124 integration, 59 contract), zero skipped/failed tests, zero build warnings/errors, clean formatting, secret scan, Compose render and five Linux Native AOT executables. Code snapshot: `31ebb4a721b8238320c171a63577744a05fa5362`; [CI run 41282780088](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41282780088). All required CI steps passed.

The starting snapshot `ca654328e0f330f3050dc670e201f6f6c74ea02d` passed CI 41281028275 but left T025/T026 unchecked. Its tests covered basic persistence and notices, not the gaps below. Earlier T022–T024 evidence remains valid for its recorded scope. The new run exercises the unchanged full suites plus support regressions and commerce-schema probes. No duplicate support or commerce migration was created; the canonical 28-table baseline is unchanged (SQL SHA-256 `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`).

## Review fixes

| Finding | Correction |
|---|---|
| HumanSupportRouter accepted unrelated verified commands and stored a fabricated asset instead of customer content. | Route only /support, a same-owner order reference on the existing /support surface, or ordinary text/media while the membership's flow is Support. Persist real text under SC02 brand/table/message/field authentication; zero temporary plaintext bytes. Accept bounded verified photo/document references without downloading files. |
| Replaying support entry after ticket closure could create another ticket; repeated staff replies could send duplicate notices. | Recover inbound identity from durable support_messages with membership and Telegram message ID before opening work. Staff retries use a caller-stable message/request UUID and compare original ticket, actor and content; changed-input replay fails. Neither identity depends on expiring bot activity. |
| Customer messages had no durable staff notification. | Atomically enqueue typed support_notice staff-queue work per customer message. Staff replies atomically create the existing customer_dm delivery plus send_telegram job. No customer body is routed to a staff group; the canonical staff-chat branch is reserved for approved supplier metadata purposes. |
| Customer persistence accepted blocked customers or replies to closed tickets. | Revalidate membership/bot/brand and reachability, lock the membership and ticket, and deny closed/foreign tickets. Public support entry checks current enabled policy and active configured contact. |
| Closed/open lifecycle no-ops returned before checking current staff authorization. | Revalidate the independent admin session before every lifecycle path, including no-ops. Take ticket locks after admin locks and append redacted lifecycle/reply audit rows. |
| Support dispatch lost activity and current flow revision. | Keep safe support_open/support_reply events without DM content and return the current membership flow revision. |

T022 transport authentication, private sender/chat/bot checks, bounded structured selector rejection, consumer ownership and restock callback anchors were reviewed. T023 response isolation and T024 canonical support constraints remain covered by the full regression run. No additional standalone defect was found in their unchanged code during this review.

## Managed regression scope

HumanSupportTests exercises AI-independent creation/reply, same-member paid-preorder discussions, assignment/closure/reopening, actual encrypted text and wrong-brand decryption rejection, central update dispatch/current flow revision, media references, restart and replay after closure, durable staff work claim, unrelated commands/wrong flow/foreign orders, blocked and disabled support, stable staff reply identity/changed-input rejection, revoked lifecycle no-ops and rollback of message-plus-job writes. A digest of existing order, wallet, ledger, inventory, fulfillment, coupon/redemption/revision and supplier-request rows is identical across preorder support creation/reply/assignment/closure/reopening. Support never performs cancellation, queue advancement, coupon release, stock allocation or credit.

Staff notice durability is a typed internal queue contract; live notification transports, sender throttling/uncertainty and admin HTTP endpoints remain later tasks. This qualification does not activate a bot listener or grant group members backend authority.

## Native and local checks

The published AotSmoke executable uses generated disposable secret files and a disposable PostgreSQL database to ingest /support and real support text, decrypt the saved SC02 body, reconstruct the router and verify exactly two durable staff jobs after replay. Existing native ingress/auth/outbox/schema/cipher/HTTP probes also run. Normal hosts continue to exit 78 and readiness stays false.

Local Spec Kit Bash tooling: 15 tests passed. Local git diff --check passed. .NET SDK, PostgreSQL and Docker are unavailable locally, so managed/Native AOT execution and Compose rendering rely on the identified CI run; no local execution of those checks is claimed. Reviewer-owned checklists remain unchanged at 92/126.

## Continued work

T028 adds four CommerceSchemaTests for exact normalized representation/sole public UUID, field-specific listing override constraints, new-order foreign-brand rejection and immutable snapshots/payment method/currency across catalog edits. Existing schema tests retain exact immutable USD ledger posting, coupon-brand checks, complete manifests and paid-preorder hold coverage. Schema qualification does not implement the checkout service; T027 race tests and T030 onward remain pending.

T029 records the current legacy-main code at `66371a23a1c8149dc5bdf89a0355aa689d454dba`, nine pinned file blob identities and 14 checked source anchors in [legacy-commerce-policy-matrix.md](../specs/001-multibrand-redesign/contracts/legacy-commerce-policy-matrix.md). Seven synthetic decimal calculations passed. The source currently disallows new direct crypto checkout, permits bank wallet funding and discounts partial quantities; the approved target intentionally differs. FR-034 requires operator approval of this concrete retained/changed matrix before new catalog-pricing implementation. No such approval is inferred from writing it, and no new pricing code was added.

## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | 1ceb67a79c0da6a4748fa0f3c068984371df8fe463f7ba5be8c2912f5778eb24 |
| BotHost | ca7328a0be0de0a6ef68a8e84721c9b0423a7aece4b3ba91df4f4f8870537f98 |
| JobHost | 045f6c93dc52429c6bab7f04b69ec5b105744082884d06d967a378ce0a6986b6 |
| SchemaRunner | acaf718c6b6308a88791bd896b2ee44a847a15ef66bf5a279a2d2fe87411fee5 |
| AotSmoke | 3e33736c4fea094339a0bab4cf3a8b3320710c50a7965b5d95c4941fa6415979 |

Intermediate review pushes superseded/cancelled their earlier runs; their partial managed successes are not substituted for native qualification. The final run above passed the complete pipeline. This subsequent documentation-only completion record does not change its code or SQL.
