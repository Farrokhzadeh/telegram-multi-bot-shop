# Implementation Plan: Managed .NET Backend

**Branch**: `main` | **Date**: 2026-11-20 | **Spec**: [Managed .NET Backend](spec.md)

## Summary

Publish Akanti’s existing ASP.NET Core backend as regular .NET 10 (JIT). Serve development-only Swagger UI from the existing admin OpenAPI contract.

## Technical context

- **Runtime and platform**: .NET 10 LTS, Linux x64 and Docker. Use framework-dependent publishes in the ASP.NET Core runtime image. Keep a self-contained JIT publish for any executable still launched directly on the staging host, such as the current Telegram bridge dependency, so the deployment does not require installing .NET on the host.
- **Dependencies and storage**: Keep Npgsql, Telegram.Bot, PostgreSQL and existing encryption. Use only the Swagger UI middleware for viewing the existing contract.
- **Verification**: Reuse the existing unit, contract and disposable PostgreSQL integration suites and applicable offline publish smoke checks. Compare idle and peak memory under the same synthetic workload and role layout. No new suite, live traffic or RAM target.

## Constitution check

Pass before and after design. The runtime and developer docs change preserves shop and authorization behavior, data and encryption, brand boundaries, and durable job semantics. Keep the normal host gates. Use synthetic fixtures only for this change. Native AOT measurements remain the baseline, with no new Native AOT compatibility claim. Production cutover stays separate.

## Implementation outline

1. Adjust [backend CI](../../.github/workflows/backend-ci.yml), [staging image](../../ops/staging/Dockerfile) and deploy helpers to publish and launch managed outputs. Preserve role entry points, bundled schema, health probes and rollback. Preserve the current staging shop and its imported-data views when replacing its runtime; the server-only views are not part of the stock image.
2. Wire development docs in [Platform.Api](../../src/Platform.Api/Program.cs). Mount Swagger UI and JSON only in Development, using [OpenApiContract](../../src/Platform.Api/OpenApiContract.cs). Keep the existing admin authorization boundary. A dev docs host without a configured admin backend can browse the contract while admin requests remain unavailable or rejected.
3. Run the [offline quickstart](quickstart.md) and record AOT/JIT memory results. Update the [admin API guide](../../docs/ADMIN_API.md) with the development URL and authentication behavior.

[Decisions](research.md) · [Data model](data-model.md) · [Swagger contract](contracts/swagger-ui.md) · [Validation](quickstart.md)
