# Design decisions

| Decision | Rationale | Alternative deferred |
|---|---|---|
| Keep .NET 10 and change publishing to JIT | Isolates the runtime comparison and preserves the app. | Major-version upgrade or changing the role layout. |
| Use Swagger UI with the existing generated contract | One contract defines all 29 admin operations. Use the UI-only middleware documented for [ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/using-openapi-documents?view=aspnetcore-10.0). | A second OpenAPI generator or an admin frontend. |
| Reuse existing tests and synthetic workload | Same inputs make regression and memory comparisons meaningful. | Live requests, a new suite or an arbitrary RAM limit. |
