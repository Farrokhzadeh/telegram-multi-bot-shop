# Bot activity registry and retention design — version 1

T087 fixes the version-1 vocabulary, field allowlist, ownership, privacy, index and permission design for the canonical bot_activity_events baseline. This document and the executable registry do not complete T088/T089 or activate reads, cleanup, imports, scheduling or a bot runtime. The table count stays 28; there is no new relation, JSON column or payload field. Original statement authority remains schema-contract.md and constitution 3.0.0.

## Stable vocabulary

Version 1 is the exact finite existing schema vocabulary. Versioning is a code/contract release identifier; no unreviewed per-row version column or reinterpretation of historical tuples is introduced. Future additions require a new reviewed registry version with explicit schema compatibility; existing codes keep their meaning. `BotActivityRegistry` supplies the fixed strings and typed context validation. No model/user-supplied action string defines an event.

| Action code | Event type | Required typed context |
| --- | --- | --- |
| start | navigation | Membership |
| catalog_open | navigation | Membership |
| product_open | navigation | Membership, product |
| quantity_enter | quantity | Membership, product, positive selected integer quantity |
| checkout_open | checkout | Membership, product; checked order when one exists |
| support_open | support | Membership; checked order/ticket only when relevant |
| support_reply | support | Membership, support ticket |
| subscribe | subscription | Membership, product |
| unsubscribe | subscription | Membership, product |
| discounts_open | discount | Membership |
| policy_check | policy | Membership; checked flow/context only when relevant |

Sources are exactly command, callback, flow and system. Outcomes are exactly accepted, rejected, unavailable, blocked, cancelled and duplicate. The event describes the actual allowed action/transition, not its raw text. Rejected inputs do not gain permission by becoming an analytics event. A policy check remains a policy check; no new timeout/error action is silently overloaded into it. T088 must qualify complete meaningful-action, abandonment, timeout and error instrumentation under an explicit mapping before claiming coverage. If a required distinction cannot be expressed faithfully by these tuples, add a reviewed finite vocabulary extension rather than encoding a subevent in a free-text key.

A checkout callback's product is read from its exact same-member order before recording; no foreign-order product is exposed or guessed. Product context is validated against the membership's brand listing. Order and support context must belong to that exact member, including the same-brand other-member case. `telegram_update_id` and flow revision are evidence, not authority. Only verified ingress/current workflow code determines actor, brand and action. AI context, customer selectors and staff-looking headers confer no event-write or history-read permission.

The Telegram event key is the bounded server-generated `telegram:<nonnegative update number>` within one membership. No message text, callback text, coupon, receipt, external URL, error string or secret is accepted as a key. Duplicate updates preserve their original row/context/clock. Domain command idempotency remains in domain records and may not be moved into optional analytics.

## Field allowlist and access

The physical allowlist is exactly 14 fields: id, user_id, event_type, source_kind, action_code, product_id, order_id, support_ticket_id, quantity_selected, flow_revision, telegram_update_id, outcome_code, idempotency_key and occurred_at. Context links are optional except where required above and must be minimized to the relevant action. No redundant brand/Telegram identity, user profile, DM/callback content, coupon/code digest, credential, bank card, payment evidence, provider secret, generic error body or JSON/EAV payload is stored. The registry types do not contain those values. Original UTC occurrence belongs to a trusted server/source path and cannot be updated.

Future history reads require a current independent administrator session on every page, bounded pagination, brand/member/time/action filters and a matching membership/brand join. Customers and AI services get no history endpoint. No admin output contains raw private content. A cursor must bind filters and stable `(occurred_at,id)` ordering with a trusted as-of time; new concurrent events may not silently reorder an existing page. Empty/foreign scope must not expose another membership. The future endpoint is `GET /v1/admin/bot-activity`; it is not mounted or added to the frozen administrator contract in this checkpoint. Optional analytics-read failure cannot block purchases or human support.

## Selected index and privilege design

- Unique `(user_id,idempotency_key)` preserves membership-scoped event dedupe while a row exists.
- `activity_retention(occurred_at,id)` supports oldest-first bounded maintenance and lag inspection.
- `activity_member_time(user_id,occurred_at DESC,id DESC)` supports bounded scoped timeline pages. Brand scope is derived by the indexed membership relation; no duplicate brand field or array index is added.

Only a trusted ingress/workflow writer may INSERT explicitly validated fields. There is no customer-controlled generic insert/update/delete endpoint. Runtime grants for UPDATE and TRUNCATE remain absent; immutable-history triggers reject both. Administrator history identity gets the projected read scope through its authenticated service. A dedicated maintenance identity is limited to SELECT needed event IDs/times and membership scope plus DELETE of bot_activity_events; it has no financial/order/support/audit/inventory/coupon or source/archive privileges. The existing delete trigger independently rejects unexpired rows. No role is provisioned on staging/production here. Actual runtime identity/grant verification remains a separate T075/T088 deployment gate.

## Exact 180-day UTC policy

Use a single trusted transaction/as-of UTC instant for each bounded read/cleanup decision. The cutoff is exactly 4320 elapsed hours (180 × 24), not six calendar months or local-midnight arithmetic. Reads include occurred_at at or newer than the cutoff and hide only older rows. DELETE is permitted only strictly older than the same cutoff; equality is retained. No update can retimestamp a row. Maintenance is bounded, resumable and safe under concurrent inserts; deleting a batch must not cascade into domain facts, clear financial replay authority or consume a receipt/claim/obligation. Technical job leases are distinct from occurrence time.

Target restore/import preserves the original source event time, uses validated membership/context provenance and applies the cutoff before any row is exposed. Expired target activity is discarded/re-expired; it must not be stamped with import/replay time. Protected original source archives, historical migration proofs and backups keep their separate owner-approved retention/access rules. This policy never deletes or opens them. Unexpected or unreconciled legacy user_activity facts remain migration exceptions rather than invented events.

Operational monitoring reports only aggregate backlog count, oldest eligible time, batch result/retry/error counts and lag. It must not duplicate event bodies or create an indefinite shadow event archive. Scheduling and cleanup-role bindings remain disabled/unqualified until their own execution tests and release gates pass.

## Replay gate before executable retention

Current financial/order/coupon/support operations already have independent idempotency, but UpdateRouter currently also uses the activity row as an interaction dedupe marker. Removing that row could allow an old navigation update to be recorded again with a new timestamp. The present verified update type does not carry a durable original occurrence/replay fence that closes this case. **Do not activate activity deletion or declare SC-040 complete on the strength of this registry/index qualification.**

T088/T089 must first implement and qualify a bounded trusted ingress/domain replay design which survives activity deletion and target restore, does not retain raw private data or an indefinite shadow activity log, and handles Telegram identity/clock/reset behavior explicitly. Do not repurpose flow_revision, profiles, coupon/audit evidence or an unrelated table to hide a replay watermark. Any required typed field/constraint must be reviewed in the canonical schema before DDL, keeping the 28-table/no-JSON design and all existing guards. Then qualify cutoff boundary, restart, concurrency, replay and source/archive isolation with disposable synthetic tests. Actual legacy reconciliation still needs protected-source authorization/evidence.

No event purge, target import, activity history endpoint or complete instrumentation is claimed here. T088 and T089 remain unchecked. Human SC-008 and activity-retention SC-040 stay unexecuted; nearby subscription cleanup does not substitute for activity retention.
