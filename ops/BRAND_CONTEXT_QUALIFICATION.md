# T010 trusted brand context qualification

Scope: the owner-authorized T010 foundation boundary, stacked on qualified T009. No production authentication handler, secret loading, Telegram ingress, customer commerce handler or deployment is enabled.

## Boundary

`BotBrandRegistry` is an immutable server-owned configuration snapshot. An exact authentication scheme and opaque subject map to one active brand/bot/deployment/revision. Invalid, duplicate or ambiguous registrations fail before use. Disabled registrations do not resolve. Authentication tickets must contain exactly one authenticated identity and one `NameIdentifier`, with no brand/bot/tenant/deployment selector claims. A customer Telegram ID is not a bot registration subject.

`BrandContext` has no public constructor or setters. `RequireBrand` rejects a foreign resource brand without identifying it. This is a foundation guard, not completed customer membership, repository filtering or admin authorization (later tasks).

Customer API endpoints must explicitly call `RequireCustomerBrandContext()`. Run middleware after routing and the future production authentication handler. Only a successful `IAuthenticateResultFeature` ticket can resolve context; `HttpContext.User` alone has no authority. Unmarked endpoints receive no implicit brand. Admin brand selection belongs to its separate authorization boundary.

On marked endpoints, headers (including X aliases), decoded query keys, route keys and JSON property names cannot select brand, tenant, bot or deployment, even when the submitted value matches the authenticated brand. JSON inspection is structural, including nested objects/arrays and escaped property names; ordinary message text remains ordinary text. Transport inputs never replace the registered context. Future normalized customer DTOs must preserve this policy; raw Telegram webhook authentication and payload normalization remain separate work.

Nonempty request bodies must be JSON objects. The scan is capped at 1 MiB and depth 64, counts actual stream bytes and uses memory only. Accepted bytes are replayed exactly to downstream readers. Owned buffers are zeroed and the original body stream restored on exit. Context is request-local and cleared after success, exception or cancellation. Generic empty 400/401/413/415 responses contain no identifiers or payloads. No request payload logging is added.

## Executable qualification

Qualified unit tests cover registry and identity ambiguity; contract tests compile the production middleware against ASP.NET Core and cover forged principals, every selector surface, JSON bounds, body preservation, concurrent brand isolation and cleanup. Native HTTP probes execute the actual published API with registered synthetic A/B identities.

The explicit `--serve-qualification` host binds only `127.0.0.1:18080`. Its public `X-Qualification-Bot: a|b` fixture constructs synthetic authentication tickets, only inside that mode. It is not credential verification, authenticated staging or a production fallback. Readiness remains HTTP 503 and `applicationReady=false`; normal host invocations still fail closed with exit 78.

No dependency version, schema, monetary/transaction helper, financial state or business workflow is changed. The 28-table SQL baseline SHA-256 remains `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`. E1 is governance-only closed; E2/E3/E5/E6 remain unqualified, and E4 is limited to the exact executable probes recorded after CI.

## Reproduction and framework contract

Use the pinned disposable harness: `dotnet restore Platform.sln -r linux-x64 --locked-mode`, `dotnet build Platform.sln -c Release --no-restore`, all three `dotnet test` suites, then `bash ops/ci/native-smoke.sh`. The native script checks the actual published endpoint metadata/middleware/authentication-ticket boundary in addition to the source-linked contract tests. No additional package is introduced.

Primary framework references: [authentication result feature](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.iauthenticateresultfeature?view=aspnetcore-10.0), [middleware ordering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0), and [JSON content type recognition](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.httprequestjsonextensions.hasjsoncontenttype?view=aspnetcore-10.0). These API contracts do not establish production credential verification.

## Observed evidence — 2026-10-31

[CI run 41216628580](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41216628580), job `124582397971`, passed at source commit [`0ce28f764a417ba700cc1d5a2b1c043e56ea942d`](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/commit/0ce28f764a417ba700cc1d5a2b1c043e56ea942d), tree `aee9073e296083eaa5aae1d485d0c0aa81010b40`. Every workflow step succeeded. T010 is complete for this synthetic foundation scope. Later evidence/status documentation does not alter that source identity.

| Check | Observed result |
|---|---|
| Restore/build | 16 projects, SDK 10.0.403, existing pinned packages, locked restore and audit; zero warnings/errors |
| Source checks | Gitleaks 8.30.1 and formatting passed; no new scanner exceptions |
| Tests | 146 passed, zero failed/skipped: 70 unit, 46 PostgreSQL integration, 30 transport/middleware contract |
| Identity boundary | Exact registered bot scheme/subject; disabled/unknown/admin/unauthenticated/ambiguous identities rejected; no public context constructor |
| HTTP boundary | Header, decoded query, route, root/nested/escaped JSON selectors rejected; malformed/deep/oversized/non-JSON bodies rejected; exact body replay and cleanup verified |
| Isolation | Concurrent A/B requests retain their registered brand; a bare authenticated User has no authority; unmarked endpoints get no implicit context |
| Native execution | All five Linux x64 executables passed; actual API A/B mapping and rejected switching probes passed; readiness stayed 503 and ordinary startup stayed exit 78 |
| Regression | Existing schema, exact-money, transaction/lease and real lost-COMMIT-reply reconciliation tests and native probes still passed |
| PostgreSQL image | 18.6, digest `sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4` |

| Qualification executable | SHA-256 |
|---|---|
| Api | `a0c47a8268b3d29d1d9bc906984ad2793d838328aa91bc431c21b163775febfa` |
| BotHost | `ef698914ecd355b233d2e4d2202d8458a76b49de7b08a2d3b42758afd14c6bb0` |
| JobHost | `ef4dcb6e2aa8d650e1f508126c419297596f6b9823661c673507f83d2254b5aa` |
| SchemaRunner | `6a2d840f0ee1940cf2113cf92c0cb63b187f64b2be7eccc3662078970f3e6b9a` |
| AotSmoke | `d14b6cc38e7a556eecd0d78775cdcbf665984ec400538a830d612e9e4ee7068f` |

These are qualification binaries, not a production release bundle. No real credentials, Telegram calls/messages, source rows/keys, live financial effects, staging/production changes or deployment occurred. E2/E3/E5/E6 remain unqualified; T011 is next.
