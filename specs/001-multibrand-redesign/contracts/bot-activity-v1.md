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

## T087 replay gate before executable retention (historical prerequisite)

At the T087 source snapshot, financial/order/coupon/support operations already had independent idempotency, but UpdateRouter also used the activity row as an interaction dedupe marker. Removing that row could allow an old navigation update to be recorded again with a new timestamp. The present verified update type does not carry a durable original occurrence/replay fence that closes this case. **Do not activate activity deletion or declare SC-040 complete on the strength of this registry/index qualification.**

T088/T089 must first implement and qualify a bounded trusted ingress/domain replay design which survives activity deletion and target restore, does not retain raw private data or an indefinite shadow activity log, and handles Telegram identity/clock/reset behavior explicitly. Do not repurpose flow_revision, profiles, coupon/audit evidence or an unrelated table to hide a replay watermark. Any required typed field/constraint must be reviewed in the canonical schema before DDL, keeping the 28-table/no-JSON design and all existing guards. Then qualify cutoff boundary, restart, concurrency, replay and source/archive isolation with disposable synthetic tests. Actual legacy reconciliation still needs protected-source authorization/evidence.

No event purge, target import, activity history endpoint or complete instrumentation is claimed here. T088 and T089 remain unchecked. Human SC-008 and activity-retention SC-040 stay unexecuted; nearby subscription cleanup does not substitute for activity retention.

## T088 replay prerequisite — technical review, 2026-11-02

The existing canonical requirement for a per-bot processed-update checkpoint is now represented explicitly on brands by two typed fields: telegram_update_highwater (BIGINT, initially -1) and telegram_update_window (128-byte BYTEA). The bit at offset n records highwater-n. New high IDs shift the fixed window; unseen out-of-order IDs within 1024 positions can still be claimed. Older IDs fail closed. Claims and actual domain/flow transitions commit together under the existing single-bot consumer lock and an exact active brand/bot row lock, following the existing financial membership-before-brand lock order. Staff binding retains its own independent challenge/proof replay rules and is not coupled to customer analytics. Rejected or failed commands roll back their claim. The schema forbids reducing the highwater or clearing any retained seen bit. This is bounded runtime metadata without customer identity or private event data, not an indefinite event archive or a field repurposed from flow/profile/audit facts. Table count remains 28.

There is deliberately no automatic reset on idle time or a lower sequence number: old replay must not gain a new epoch merely by waiting. Actual Telegram reset/reordering/delivery limits, webhook credential rotation and any audited epoch recovery need separate qualified transport/operational evidence before activation. A reset outside the window stops safely and needs operator review; no recovery endpoint is added here. A target restore must preserve the checkpoint together with domain rows and activity. Restoring an older snapshot with a live writer/old queued updates remains fenced by the separate cutover/recovery protocol. Existing databases need an explicit reviewed bootstrap/migration before cleanup; this fresh-disposable baseline does not backfill them automatically.

Timeline cursors are short-lived, HMAC-authenticated and bound to the current administrator/session, filters and page size. The original database UTC as-of bounds the page sequence; each page rechecks current administrator authority and the current rolling cutoff. They contain only typed identifiers/times and a filter digest, never raw source text. Only a separately mounted optional administration extension exposes the timeline; the frozen 29-method core contract remains unchanged.

The first bounded maintenance implementation deletes only strictly expired Telegram-linked rows whose independent checkpoint demonstrably covers their update ID. It reports total and blocked expired backlog. Rows without an independently qualified replay fence remain blocked rather than being deleted unsafely; this is an incomplete FR-071 rollout, not completed SC-040. No scheduler, runtime grants, source reader or restore importer is activated. T088 remains open for full domain/workflow instrumentation, abandonment/error vocabulary, non-Telegram replay/restore and complete retention; T089 remains open for comprehensive retention and protected legacy reconciliation.

## Qualified optional timeline extension

The T088 prerequisite checkpoint qualifies `GET /v1/admin/bot-activity` only when explicitly mounted with the independent administrator guard and a protected 32-byte cursor signing key. Query fields are optional `brandId`, `memberId`, inclusive UTC `from`, exclusive UTC `until`, exact finite `action`, opaque `cursor` and `limit` (1–100; default 50). UTC filters must be ordered; unknown action, malformed/tampered/rebound/expired cursor, invalid identifiers or bounds are rejected. Each continuation is bound to the same administrator/session, filters and page size for at most 15 minutes. Current revocation/expiry is checked on every page.

The response has `Items`, `Next` and original database-UTC `AsOf`. Each item contains only Id, BrandId, MemberId, EventType, Source, Action, ProductId, OrderId, TicketId, Quantity, FlowRevision, Outcome and OccurredAt. No Telegram identity/update number, raw event key or private payload is returned. Tuple keyset ordering prevents offset-based duplication; `AsOf` bounds original event occurrence, not a promise of a cross-request MVCC snapshot. Each page applies the current strict rolling expiry cutoff. Empty/mismatched scopes return an empty page. Authentication failures are 401; invalid filters/cursors are 400; infrastructure failures follow the existing sanitized diagnostics policy. Optional timeline failures do not enter bot/checkout/support execution paths.

This endpoint and covered-row cleanup are qualified in ops/ACTIVITY_RUNTIME_QUALIFICATION.md. Full instrumentation, complete non-Telegram retention, target restore/import and SC-040 remain open. The earlier T087 descriptions of missing endpoint/checkpoint are historical; no whole-task or release completion is inferred.

## Registry version 2 — T088 workflow review, 2026-11-02

Version 2 preserves all eleven version-1 enum ordinals and event/action meanings, and appends two finite actions: checkout/checkout_created and flow/flow_expired. checkout_created requires checked product, same-member order and positive quantity; it records successful creation of frozen order terms, including awaiting-payment orders, and never asserts settlement or fulfillment. flow_expired requires the expired flow revision and records only an actual compare-and-set clearing of an expired non-idle customer UI flow. Its original occurred_at is the stored expiry, outcome cancelled, source system; it does not expire an order, close a support ticket, release a coupon or change money/stock.

The internal writer accepts only typed registry tuples and server-generated identities, validates current original membership/context, preserves the original database UTC occurrence and skips already-expired original events before insertion. Changed content under the same identity conflicts. Checkout-created events use orders.id and immutable orders.created_at; order replay returns before writing. Flow expiry uses a deterministic membership/revision identity and the independently monotonic users.flow_revision. Financial/flow transitions and events commit together; failed commits roll back both. Fresh successful quantity/quote preparation is a new observation with its own server nonce and original preparation time, never an order or payment. The expired-event rule prevents a restored/re-emitted original observation from acquiring a new clock; complete target importer/restore qualification remains pending.

Maintenance may additionally remove strictly expired checkout_created events matching the original same-member order/product/quantity/creation time, and flow_expired events whose membership revision has advanced beyond the expired revision. These independent domain fences survive analytics deletion. Unqualified non-Telegram rows remain blocked; this is not completed full FR-071/SC-040 retention. No new table, column, JSON or raw payload is introduced by this vocabulary extension. Full verified quantity ingress, all checkout/payment/error states, target restore/import, protected legacy reconciliation and live scheduling remain later T088/T089 work.
