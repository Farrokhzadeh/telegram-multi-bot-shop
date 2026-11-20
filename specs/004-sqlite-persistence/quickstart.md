# Offline qualification

1. Use the schema operator to initialize an empty SQLite file in an isolated local directory. Seed one synthetic brand, admin, catalog and stock. Start the staging roles through the existing launch flow using the shared path in the [runtime contract](contracts/sqlite-runtime.md).
2. Run the existing .NET suites through [backend CI](../../.github/workflows/backend-ci.yml), adapting the relevant fixtures to SQLite. Verify catalog permissions and cursor pagination, then run concurrent checkout and replay a synthetic payment callback. Assert one settlement, exact wallet amounts and no duplicate allocations.
3. Restart the worker after a synthetic interruption. Verify jobs remain durable, stale leases are rejected and committed work is not repeated. Deploy through the existing staging workflow after these checks pass.
