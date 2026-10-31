# Offline foundation qualification

**Scope:** T002–T007 setup and the migration-only T013 reader, using public synthetic fixtures. No customer commerce feature or live integration is implemented by this batch. Task completion and observed results are recorded only after the corresponding CI checks pass.

**Later batch:** The separately authorized T008 baseline and operator now passed qualification. See [SCHEMA_QUALIFICATION.md](SCHEMA_QUALIFICATION.md) for current schema evidence. The project/test counts and not-yet-implemented statements below describe the initial cited source snapshot, not a reversal of the later T008 result.

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

Qualification passed on `impl/001-offline-foundation` in [GitHub Actions run 41187355132](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41187355132), job `124496869419`, at commit [`a74d8d8f208f1b36ae6a122216eace07c85411a0`](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/commit/a74d8d8f208f1b36ae6a122216eace07c85411a0), tree `78d85c8f5fef3655df0e26aa10bdaebf27ca9240`, on 2026-10-31. All workflow steps completed successfully.

| Check | Observed result |
|---|---|
| Toolchain / dependencies | SDK 10.0.403; Npgsql 10.0.3; locked restore succeeded for all 15 projects |
| Database | PostgreSQL 18.6, exact `server_version_num=180006`; pulled image digest `sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4` |
| Build / formatting | Release build: zero warnings and zero errors; whitespace verification passed |
| Security | Gitleaks 8.30.1 scan and NuGet dependency audit passed under warnings-as-errors |
| Unit tests | 5 passed, 0 failed/skipped; includes 9 Python-generated Fernet vectors, tamper/wrong-key rejection and plaintext/mapping regression cases |
| Integration tests | 2 passed, 0 failed/skipped; exact decimal mapping, rollback and server identity in disposable databases |
| Transport contract tests | 3 passed, 0 failed/skipped; lost-send, repeated-edit and original supplier-purchase reconciliation simulations |
| Native execution | All three hosts and AotSmoke published as Linux x64 ELF executables and ran successfully; default host startup exits 78 |
| Native API / dependencies | Loopback liveness, 503 readiness, 401 without the public test header and source-generated JSON passed; native AES-GCM, Fernet and Npgsql probes passed |

The run recorded these executable SHA-256 values. They identify qualification probes from the cited commit, not a production release bundle.

| Executable | SHA-256 |
|---|---|
| `Platform.Api` | `ff572ed3a510d3302ae50f05a51a9ed2003ef5d5cacaaaac4f152f370ae5eaaf` |
| `Platform.BotHost` | `0fa95709a51e60b2613bc223121dc071dcd2665a02b3d6b43c25aab3af1bdf32` |
| `Platform.JobHost` | `ace00b74535fe9460c320afc5192d6e04958032a751390a677d091fc7b9ed604` |
| `Platform.AotSmoke` | `1ff3733ed9c54c21d4869c26af052445e9267c7769b732e476a1697d3ab51239` |

T002–T007 and independent migration-only T013 are complete for this scope. T007 defines requirements/default identities; actual staging provisioning, measured headroom and real identity acceptance remain future work. T013 pure reader vectors live in the unit suite, with independent native execution; the transport contract suite owns the HTTP failure simulations. The target schema, new keyrings/envelope and durable commerce workers are not implemented.

The local SDK archive passed Microsoft's published SHA-512 check, but CoreCLR startup fails in the restricted workspace with HRESULT 0x8007000E; local native/runtime success is not claimed. The isolated GitHub Actions runner supplies the executable evidence. Later documentation commits do not change which commit these recorded hashes identify.

E1 remains closed for governance alignment. E2/E3/E5/E6 remain unqualified; E4 evidence is limited to the actual successful probes and does not imply full-platform readiness. The reviewer-owned requirements checklist is left unchanged. No production data/keys, real supplier/FX/payment/Telegram calls, staging deployment, customer messages, payouts or source mutations occur.
