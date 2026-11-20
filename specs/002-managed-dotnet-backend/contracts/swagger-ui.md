# Development documentation contract

| Route | Development behavior |
|---|---|
| `GET /swagger` | Opens Swagger UI using the existing admin contract. |
| `GET /openapi/admin-api.json` | Returns the existing OpenAPI document with its 29 admin operations and bearer security scheme. |

Both routes are disabled by default outside Development. Requests to `/v1/admin/*` still use existing bearer authorization and retain their responses and status codes. The documentation routes do not issue tokens or enable disabled admin endpoints.
