# T010 trusted brand context qualification

Scope: the owner-authorized T010 foundation boundary, stacked on qualified T009. No production authentication handler, secret loading, Telegram ingress, customer commerce handler or deployment is enabled.

## Boundary

`BotBrandRegistry` is an immutable server-owned configuration snapshot. An exact authentication scheme and opaque subject map to one active brand/bot/deployment/revision. Invalid, duplicate or ambiguous registrations fail before use. Disabled registrations do not resolve. Authentication tickets must contain exactly one authenticated identity and one `NameIdentifier`, with no brand/bot/tenant/deployment selector claims. A customer Telegram ID is not a bot registration subject.

`BrandContext` has no public constructor or setters. `RequireBrand` rejects a foreign resource brand without identifying it. This is a foundation guard, not completed customer membership, repository filtering or admin authorization (later tasks).

Customer API endpoints must explicitly call `RequireCustomerBrandContext()`. Run middleware after routing and the future production authentication handler. Only a successful `IAuthenticateResultFeature` ticket can resolve context; `HttpContext.User` alone has no authority. Unmarked endpoints receive no implicit brand. Admin brand selection belongs to its separate authorization boundary.

On marked endpoints, headers (including X aliases), decoded query keys, route keys and JSON property names cannot select brand, tenant, bot or deployment, even when the submitted value matches the authenticated brand. JSON inspection is structural, including nested objects/arrays and escaped property names; ordinary message text remains ordinary text. Transport inputs never replace the registered context. Future normalized customer DTOs must preserve this policy; raw Telegram webhook authentication and payload normalization remain separate work.

Nonempty request bodies must be JSON objects. The scan is capped at 1 MiB and depth 64, counts actual stream bytes and uses memory only. Accepted bytes are replayed exactly to downstream readers. Owned buffers are zeroed and the original body stream restored on exit. Context is request-local and cleared after success, exception or cancellation. Generic empty 400/401/413/415 responses contain no identifiers or payloads. No request payload logging is added.

## Executable qualification

Pending CI: unit tests cover registry and identity ambiguity; contract tests compile the production middleware against ASP.NET Core and cover forged principals, every selector surface, JSON bounds, body preservation, concurrent brand isolation and cleanup. Native HTTP probes execute the actual published API with registered synthetic A/B identities.

The explicit `--serve-qualification` host binds only `127.0.0.1:18080`. Its public `X-Qualification-Bot: a|b` fixture constructs synthetic authentication tickets, only inside that mode. It is not credential verification, authenticated staging or a production fallback. Readiness remains HTTP 503 and `applicationReady=false`; normal host invocations still fail closed with exit 78.

No dependency version, schema, monetary/transaction helper, financial state or business workflow is changed. The 28-table SQL baseline SHA-256 remains `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`. E1 is governance-only closed; E2/E3/E5/E6 remain unqualified, and E4 is limited to the exact executable probes recorded after CI.
