# Offline validation

After implementation, use the existing CI fixture or disposable PostgreSQL with the same test environment. Run:

```sh
dotnet test Platform.sln -c Release
```

Run the managed publish and offline smoke commands from the updated workflow. Open `/swagger` and `/openapi/admin-api.json` on the local Development API, and confirm the same docs routes are absent outside Development. Reuse the existing admin contract and authorization checks.

Replay the same synthetic workload against the AOT and JIT builds separately. Record idle and peak memory using the same metric, process layout and resource settings. No live Telegram or provider calls.
