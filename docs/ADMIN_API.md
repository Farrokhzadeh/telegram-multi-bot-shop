# Akanti admin API for frontend developers

The versioned admin contract is an OpenAPI 3.0.3 JSON file with 29 operations. Import `admin-api.json` into your API client or use it to generate TypeScript models and mock responses. [Contract source](../src/Platform.Api/OpenApiContract.cs)

## Get the contract

1. Open a successful [Offline backend qualification run](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/workflows/backend-ci.yml) for the commit you are integrating.
2. Download `offline-qualification-<commit>` from **Artifacts** and extract `artifacts/contracts/admin-api.json`. GitHub retains each artifact for seven days. The **Publish and run managed Linux backend** step records the file's SHA-256.
3. Use the file against mocks during frontend development. The current Docker Hub staging image runs a local qualification API and the isolated Telegram shop. It does not expose an admin API base URL or a Swagger UI. Live integration needs a separately configured admin host and administrator sessions.

## Browse locally with Swagger UI

Run `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Platform.Api -- --serve-qualification` and open [Swagger UI](http://127.0.0.1:18080/swagger). It reads the same 29-operation contract from `/openapi/admin-api.json`. The loopback qualification host shows the contract without enabling the admin endpoints. On an admin API host, calls still need a valid bearer session. The documentation routes are off outside Development.

## Plaintext runtime candidate

The isolated SQLite v2 API keeps the existing local service key and administrator bearer sessions. Synthetic stock imports use the same request and command identity through `POST /v1/admin/inventory/import`. The route checks the administrator session and accepts synthetic credentials only. PostgreSQL v2 keeps scoped writer registration, independent supplier credentials and the keyed bank-card fingerprint. The HTTP request and response contract is unchanged.

## Integrate the admin panel

| Screen | Start with | Follow-up operations |
|---|---|---|
| Brand selector and settings | `GET /v1/admin/brands` | Brand configuration, policy, exchange rate, channel settings, bot token and bank destination |
| Catalog and inventory | `GET /v1/admin/products` | Product and per-brand listing edits, inventory metadata and stock import |
| Orders and deposits | `GET /v1/admin/orders` | Order resolution, payment and deposit details, receipt decisions and wallet adjustments |
| Coupons | `GET /v1/admin/coupons?brandId=…` | Coupon revisions and edits |
| Operations and audit | `GET /v1/admin/operations` and `GET /v1/admin/audit` | Work backlog and audit history |
| Campaigns | `GET /v1/admin/campaigns` | Create a campaign, then send or cancel a batch |

Use the OpenAPI file for exact methods, request fields, response shapes and required query parameters. The contract covers administrator operations. Customer checkout runs through Telegram. Supplier catalog and bot activity have separate handlers outside this versioned OpenAPI export.

- **Authentication.** All 29 operations require an administrator session in `Authorization: Bearer <token>`. Keep long-lived tokens on the server side of the frontend and send admin requests over HTTPS. The contract has no login or session-issuance endpoint.
- **Brand context.** Pass the selected brand UUID where the operation requires it. Some routes take it in the path, some use the `brandId` query parameter, and some use `BrandId` in the body.
- **JSON.** Use `Content-Type: application/json` for JSON request bodies and match field casing exactly. For example, a standard list returns `Items` and `Next`, while errors return `code` and `requestId`. For lists with an `after` parameter, use `Next` as the next page's value and stop when it is null.
- **Writes.** Reuse the same `CommandId` when retrying the same action on operations that accept it. For revision-checked edits, send the current `ExpectedRevision`. If an edit returns `409`, refresh the record and let the operator review the new state. A `204` has an empty response body.
- **Errors.** Prompt for authentication on `401`, show a permissions message for `403`, and use the returned `requestId` to correlate an issue with the API logs.

[Auth boundary](../src/Platform.Api/AdminGuardMiddleware.cs) · [Contract tests](../tests/Platform.ContractTests/AdminApiSnapshotTests.cs)
