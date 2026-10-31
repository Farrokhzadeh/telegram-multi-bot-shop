# Offline foundation qualification

**Scope:** T002–T007 setup and the migration-only T013 reader, using public synthetic fixtures. No customer commerce feature or live integration is implemented by this batch. Task completion and observed results are recorded only after the corresponding CI checks pass.

## Reproduce

Use .NET SDK 10.0.403, a supported Linux x64 native compiler/zlib toolchain and a disposable PostgreSQL 18.6 instance. The repository pins Npgsql 10.0.3 and test dependencies through central versions and per-project lockfile hashes. CI uses isolated synthetic PostgreSQL identities from RELEASE_GATES.md.

```bash
dotnet restore Platform.sln -r linux-x64 --locked-mode
dotnet build Platform.sln -c Release --no-restore
dotnet format whitespace Platform.sln --verify-no-changes --no-restore
dotnet test Platform.sln -c Release --no-build --no-restore
bash ops/ci/native-smoke.sh
```

Integration/native probes require the explicit disposable-creation flag and a loopback test connection under the exact `shop_test_control` / `shop_test_runner` identity. Missing or other identities fail; there is no default production connection. The fixture owns a fresh random database and cleans up only that database. Qualification creates temporary probe tables there, not an executable target-schema migration or an additional target business table.

The three hosts accept `--qualify`; ordinary startup exits 78. The API's explicit `--serve-qualification` mode binds `127.0.0.1:18080`, has a liveness probe, returns 503 readiness and rejects the synthetic qualification route without its public test header. This tests the minimal HTTP/source-generated JSON pipeline; it is not an implementation of production administrator authentication.

## Evidence interpretation

- Unit fixtures cover all four mapped Fernet prefixes, direct/prefixed/derived keys, previous-key fallback, Unicode, wrong-key/tamper rejection and explicit source plaintext exceptions. These are synthetic interoperability tests, not proof that real legacy data has been migrated.
- Fake transports preserve the distinction between a committed remote effect and a lost response. Retrying an edit can target the same visible message; an unknown supplier purchase is reconciled through the original request. This is harness capability, not a completed durable production sender/procurement worker.
- PostgreSQL probes check the exact 18.6 server version, an exact .NET decimal/NUMERIC sample and transaction rollback in an owned disposable database. They do not qualify the unimplemented 28-table constraints, balance migration, allocator, coupon or job semantics.
- Native smoke publishes and executes the API, bot and job skeletons plus a dependency probe. The probe exercises source-generated JSON, AES-GCM authenticated-context rejection, the migration-only Fernet reader and Npgsql numeric access. The future target ciphertext envelope/keyring remains T011/T012 work.
- Treat compiler, audit, trimming and AOT warnings as errors. Secret scanning retains the default rule set with narrowly documented exceptions for public test data. Final restore is locked; bootstrap lock capture is not a release qualification.

## Current evidence

Build/test/native qualification is in progress on `impl/001-offline-foundation`. No task is marked complete from a queued or failed workflow. The local SDK archive passed Microsoft's published SHA-512 check, but CoreCLR startup fails in the restricted workspace with HRESULT 0x8007000E; local native/runtime success is not claimed. The isolated GitHub Actions runner supplies the executable evidence.

E1 remains closed for governance alignment. E2/E3/E5/E6 remain unqualified; E4 evidence is limited to the actual successful probes and does not imply full-platform readiness. The reviewer-owned requirements checklist is left unchanged. No production data/keys, real supplier/FX/payment/Telegram calls, staging deployment, customer messages, payouts or source mutations occur.
