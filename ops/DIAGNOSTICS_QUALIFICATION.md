# T014 safe diagnostics foundation

Qualified at source commit 27c27ffd92b4504b91c39eddeb5faa7ff91fb397, tree 8ea91b17d092060e3c069eb8a25182b8ce0df372. No business runtime activation, real integrations or deployment.

The outer API middleware generates a fresh UUID for every request, ignores caller-supplied request IDs, exposes X-Request-Id and supplies a request-scoped typed accessor. Context is cleared in finally. Structured completion records contain only fixed Api component, generated request ID, response status, allowlisted error code and an aborted flag. No URL, route values, query, headers, request/response body, principal, brand/customer identifier, exception message/type/stack/Data or arbitrary message field is logged. Dependency-specific exception metadata remains at its existing safe domain boundary; no raw exception serializer is introduced.

Explicit SafeOperationException kinds map to generic 400/401/403/404/409/503 errors; unknown exceptions become internal_error with status 500. Caller cancellation maps to canceled; responses already started or disconnected are aborted without appending an error payload. Error responses contain only code and generated requestId, with source-generated JSON. A logging sink failure increments a safe dropped-event counter and never replays a handler or prints a raw fallback exception. Monitoring/alerts for that counter and audited privileged business events remain later operational tasks.

Health endpoints are isolated in DiagnosticsEndpoints.cs. Liveness identifies the qualification host; readiness stays 503. A successful key, database or HTTP probe cannot promote the incomplete platform to ready. Ordinary Api/BotHost/JobHost invocations still exit 78. Existing default logging providers remain cleared in qualification mode; the new sink serializes only the closed diagnostic schema.

Contract tests use synthetic private markers in URL, query, headers and exceptions, check that logs/responses exclude them, verify independent correlations, typed error mappings, cancellation and a failing sink without repeated effects. The actual Native AOT API fault probe checks a generic 500 body and matching correlation header; existing brand-context/readiness probes continue. No new package, schema, customer handler, secret or live operation is involved.

## Observed evidence

[CI run 41233767226](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41233767226), job `124633383460`, passed all workflow steps: 180 tests (96 unit, 46 integration, 38 contract), zero failed/skipped and zero build warnings/errors, secret scan, locked restore/audit, formatting, Compose rendering and five native executables. The actual API safe-500/correlation probe passed while readiness remained 503 and ordinary startup remained exit 78. T014 is complete for this foundation scope. No live operations occurred.

| Native executable | SHA-256 |
|---|---|
| Api | `093c7f718028358c7dcd62aac4c02cb4af4906d36183a7366acdcbad89619fba` |
| BotHost | `2c62da97b2ae365bee7ba4a0a3b870c562de6bb4393c2050964be6e8644a8b32` |
| JobHost | `9835d0e94b644ac5d5bc895509ea84fbbc9810c609eedc79aacee0a1f9c90022` |
| SchemaRunner | `349bebf51cd6662afc65bef0e43b85e1ef6019ca7cc1fc23928870b121e9c506` |
| AotSmoke | `41d7097b0daf01c88988daffa770d4819abdc8b901128998c3ae63b63f9a48da` |
