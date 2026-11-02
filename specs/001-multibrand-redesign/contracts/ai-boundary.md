# Optional independent AI boundary — version 1

Phase 9 defines an opt-in read seam and advisory client policy. Normal commerce hosts do not mount the endpoint, configure a provider or enable AI. No SDK, model, prompt pipeline or real transport is included. Human support and checkout operate without this seam.

## Context read

`GET /v1/ai/context` is the only operation. A trusted host authenticator must establish a successful ASP.NET authentication ticket with the exact `ai-service-v1` scheme, one authenticated identity and one configured subject. A principal attached to the request alone, customer/bot/admin schemes, headers, cookies or AI-produced identifiers confer no authority. A host-configured expiring binding pins the subject to an existing verified bot/brand context, Telegram actor and optional exact order. Scope objects cannot be constructed by callers. Revocation invalidates cached scopes as well as future resolutions.

There are no request selectors. Query strings, request bodies and brand/user/Telegram selector headers are rejected. Every read rechecks the active brand, matching bot and current unblocked/unbanned member. An order must belong to that exact member; another member of the same brand is also excluded. Missing and foreign orders both yield a redacted 404. Unknown, expired or revoked service identities yield 401. Other methods yield 405. Errors expose only a stable code and correlation identity.

| Object | Allowed fields |
| --- | --- |
| Context | `version` (1), `brandId`, public `brandName`, normalized `language`, `humanSupportAvailable`, `publicSupportContact`, optional `order` |
| Order | `orderId`, public `productTitle`, `paymentStatus`, `fulfillmentStatus` |

The read uses an explicit PostgreSQL read-only transaction and an allowlisted projection. It does not update member activity or flow. It does not load wallet values, payment evidence, provider references, stock, entitlements, encrypted credentials, private staff chat, customer profile, support transcript or audit history. Public support contact is available only under current enabled support policy and a current bound active staff contact. No decryption is performed. This separate versioned seam does not expand the frozen administrator contract.

## Optional advisory turn

`AiTurn` contains the scoped version-1 context and one explicitly supplied question, bounded to 4096 characters. Future host composition must opt in, select an eligible customer support intent and supply only the current question; this contract does not authorize bulk/history export. The external adapter is trusted host configuration, never a user-selected destination. No production adapter or hosting/authentication setup is qualified here.

The adapter returns version 1, bounded plain text (2048 characters) and an optional human-request flag. Advice is untrusted display text. It is never parsed as a command, executed, granted administrator authority or passed into a money, coupon, payment, inventory, fulfillment or support mutation. Presentation must treat it as plain text with no markup parse mode. A human-request flag yields only a handoff disposition with no AI text. Actual support actions require the existing verified private Telegram customer flow; AI cannot forge that ingress or create tickets on its own authority. Administrative proposals require a separately authenticated human and the existing audited endpoint.

Disabled or absent clients return a human-support disposition without invoking the adapter. Invalid context, malformed advice, exceptions, timeouts, open circuits and concurrent outstanding work also return bounded reason codes without transport details. Caller cancellation propagates. One outstanding call per brand prevents stacking when an adapter ignores cancellation; an independent deadline returns promptly. Failures open a per-brand circuit at the configured threshold, successful timely calls reset it, and cooldown permits a later attempt only after the original call completes. Late replies are observed and discarded; they cannot become an answer or reset the circuit. Other brands remain independent. The policy holds no database or mutation capability.

The executable synthetic qualification covers service scope, method isolation, member/order ownership, unchanged commerce rows, actual read-only write rejection, AI-off checkout and verified human support, deadline/circuit behavior, malformed replies and Linux Native AOT serialization. It does not qualify live AI quality, hosting, provider authentication, transport security or rollout.
