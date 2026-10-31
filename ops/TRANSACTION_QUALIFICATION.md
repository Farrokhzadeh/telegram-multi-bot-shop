# Transaction and exact money qualification

**Scope:** T009, authorized by the owner's "continue" after the T008 report on 2026-10-31. This implements reusable database/money boundaries and qualifies them only with synthetic disposable data. Customer commerce handlers, actual supplier/payment/Telegram calls, source import, production/staging operations and deployment remain gated.

## Exact amounts and currencies

`Platform.Core/Money.cs` keeps USD wallet amounts, USDT shop quotes and TOMAN bank amounts distinct. Currency codes are exact `USD`, `USDT` and `TOMAN`; network assets and provider-native currency identifiers are separate contracts. Arithmetic requires matching currencies, and binding a money parameter requires the caller's expected currency. No implicit FX or USDT/USD conversion is provided. The approved frozen quote mapping belongs to the later pricing/settlement services.

`ExactDecimal` parses canonical ASCII decimal text with an optional minus and decimal point. It accepts no exponent, grouping, locale-specific separator, plus sign, non-finite value or whitespace. Text is bounded to 128 characters before parsing. Integer coefficient/scale arithmetic proves exact representability before constructing a .NET decimal; excess precision, coefficient overflow, nonterminating division and underflow fail instead of rounding. Insignificant trailing zeros may normalize without changing the numeric value. Signed money supports ledger deltas; explicit positive/nonnegative guards serve their respective callers.

The schema's unbounded finite NUMERIC remains authoritative storage. Project monetary reads as `numeric::text` and use `DbValue.ReadMoney` so out-of-range or over-precise values fail explicitly. This introduces no accepted per-currency scale or legacy precision assumption: actual source limits/denominations remain E3 evidence. Rounding/discount/FX algorithms remain T029–T031 work.

## Parameter and session ownership

- `DbStatement` holds repository-authored SQL and explicit `$1` positional scalar bindings. Text, UUID, integer/bigint, boolean, NUMERIC, bytea, nullable scalar and finite UTC timestamp inputs have explicit types; arrays, JSON and arbitrary CLR mappings are excluded. Binary inputs are copied. UTC min/max sentinel values are rejected because they can map to database infinities.
- The supported SQL surface is one SELECT/INSERT/UPDATE/DELETE/WITH data statement, with complete positional bindings. Scripts, transaction-control commands, comments, dollar quoting and escaped SQL literals are rejected. This lexical fence is not a general SQL sandbox or authorization layer; SQL text/identifiers must be reviewed constants and all variable values must be bound.
- `DbSessionFactory` uses the caller-owned shared data source and creates a dedicated connection/transaction per session. It rejects ambient transactions, multiplexed connections and detailed server error output. Callers must also keep parameter logging disabled. No schema operation or startup DDL is invoked.
- Isolation defaults to Read Committed; Repeatable Read and Serializable are explicit options. Transaction-local UTC, statement and lock timeouts are parameterized. Current engineering defaults are 30 seconds for statements, 5 seconds for locks and 5 seconds for rollback cleanup; client command timeout has a 5-second margin. These are configurable defaults, not production performance qualification.
- A session has one operation at a time. Commands/readers/connection/transaction stay private. Synchronous projections receive typed `DbRow` access only; escaped row handles are invalidated. A single-row query rejects zero/multiple rows and mapping errors. Mappers only read fields and must not perform external effects.
- A command/projection/cancellation/fence failure poisons the session. It cannot commit later partial work. Explicit commit is required; disposal rolls back uncommitted work with an independent bounded rollback token, even when the request token is canceled. Cleanup errors are recorded as safe metadata and do not turn an acknowledged commit into a retryable result.

For example, an exact typed roundtrip uses:

```csharp
await using var session = await factory.BeginAsync(cancellationToken);
var value = Money.Parse("12.34", Currency.Usd);
var statement = new DbStatement("SELECT $1::numeric::text",
    DbValue.Money(value, Currency.Usd));
Money result = await session.QuerySingleAsync(statement,
    row => DbValue.ReadMoney(row, 0, Currency.Usd), cancellationToken);
await session.CommitAsync(cancellationToken);
```

## Recovery protocol

The factory never replays an application callback, statement, transaction, purchase or message automatically. Classification uses validated SQLSTATE, not localized error text or `IsTransient` alone. Safe exceptions retain category/recovery/SQLSTATE only; original errors, SQL, parameters and server details are not attached as inner exceptions.

| Failure | Required caller behavior |
|---|---|
| Connection failure while opening | Reconnect before starting work; configuration/authentication errors still fail |
| Serialization/deadlock/lock contention (`40001`, `40P01`, `55P03`) | Roll back/dispose, re-read authoritative state and make a bounded, explicitly authorized whole-transaction attempt if appropriate |
| Constraint, syntax/authentication or invalid data | Reject; do not blindly retry. Business idempotency may separately look up an existing original result |
| Cancellation before commit dispatch | Stop and roll back uncommitted work |
| Explicit backend rejection during COMMIT, including deferred constraints | Known rollback; use the mapped rejection/concurrency policy |
| Transport loss, cancellation or an ambiguous result after commit dispatch; `40003`/`08007` | Preserve OutcomeUnknown, discard the uncertain source connection/pool, and reconcile the original durable identity on a fresh connection before any possible retry |
| Lease fence affects zero rows | Stop that lease owner and roll back all pending local writes; no remote retry permission |

Unknown outcome cannot be relabeled as rollback by calling `RollbackAsync`. Session outcome survives disposal. Database reconciliation and provider/Telegram uncertainty are separate duties: a fresh connection or worker lease never proves that a remote purchase/send failed.

`ExecuteLeaseFencedAsync` enforces exactly one affected row for a repository statement that compares durable lease ID/owner, attempt/revision and real expiry in that same statement. It does not acquire/renew leases, enqueue work or inspect provider state. T015 owns the actual outbox scheduler and T056 owns purchase/reconciliation permission. PostgreSQL FIFO head selection and approved lock ordering remain service obligations; scheduler contention does not permit skipping a paid business head.

## Verification and scope limits

Targeted tests cover exact parser/arithmetic boundaries, explicit currency/type bindings, inert SQL injection text, transaction control exclusion, rollback/cancellation, deferred commit rejection, serialization and lock contention, pre-commit connection termination, stale/expired/replaced leases, typed projection lifetime/cardinality and a server-confirmed commit whose acknowledgement is deliberately lost.

The loopback protocol fault helper is compiled only into tests and AotSmoke. It accepts only the existing synthetic qualification database/user identity, forwards opaque frames without logging them, observes PostgreSQL's COMMIT completion and closes the client connection before forwarding that acknowledgement. An independent fresh connection verifies one committed row. The native probe repeats this test; it exercises the real Npgsql transaction path rather than an application mock.

No row-count fence or successful transaction is an authentication/brand-authorization mechanism. T010/T011/T012/T014–T017 and the later pricing/payment/fulfillment services remain unfinished. Full runtime, protected legacy compatibility and operational release gates remain open.

Reproduce with the existing pinned disposable harness:

```bash
dotnet restore Platform.sln -r linux-x64 --locked-mode
dotnet build Platform.sln -c Release --no-restore
dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --no-restore
bash ops/ci/native-smoke.sh
```

Primary API/behavior references: [Npgsql basic usage](https://www.npgsql.org/doc/basic-usage.html), [Npgsql error types](https://www.npgsql.org/doc/diagnostics/exceptions_notices.html), [PostgreSQL SQLSTATE](https://www.postgresql.org/docs/18/errcodes-appendix.html) and [whole-transaction serialization recovery](https://www.postgresql.org/docs/18/mvcc-serialization-failure-handling.html). Actual qualification uses the pinned versions below.

## Observed evidence

Qualification passed in [CI run 41212609660](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41212609660), job `124570578479`, at commit [`1b4649366e9f9bc3c8fd9a3f696f279164c4a862`](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/commit/1b4649366e9f9bc3c8fd9a3f696f279164c4a862), tree `e8eabd26c61c82767310e9cd75aea16659ce104c`. All workflow steps succeeded. T009 is complete for this offline scope; previous T008 evidence remains in `SCHEMA_QUALIFICATION.md`.

| Check | Observed result |
|---|---|
| Build / dependencies | SDK 10.0.403, Npgsql 10.0.3, 16 projects, locked restore, zero build warnings/errors and formatting passed |
| Security | NuGet audit and Gitleaks 8.30.1 passed with the existing narrow public-fixture exceptions |
| Database | PostgreSQL 18.6 / `180006`; image digest `sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4` |
| Tests | 107 passed, zero failed/skipped: 58 unit, 46 PostgreSQL integration and 3 transport contract |
| Transaction probes | Explicit commit, disposal/cancellation rollback, poisoned session, deferred rejection, serializable conflict, server lock timeout and pre-commit connection loss/reconnect |
| Boundary probes | Exact money/currency/type/SQL bindings, inert injection text, expired/replaced lease generations, escaped row handles and cardinality rejection |
| Lost acknowledgement | Server-confirmed COMMIT reply was dropped; the client retained OutcomeUnknown, rejected commit/rollback reuse, and a fresh connection observed exactly one committed effect |
| Native execution | All five Linux x64 executables passed; AotSmoke executed exact money, typed sessions and the real lost-commit-acknowledgement fault/reconciliation probe |

The 28-table SQL remains identified by SHA-256 `fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d`. These executable hashes identify the qualification binaries from the cited commit. They are not a production release bundle; later documentation commits do not change their source identity.

| Executable | SHA-256 |
|---|---|
| `Platform.Api` | `eba324edbd6b9059fc2159f0396fb26dacc2e204960043129cf494eb66e4b677` |
| `Platform.BotHost` | `f95b7bed06fb74f2296f0b8ef346ad786bf9b8f5501955ced5acb1270771132b` |
| `Platform.JobHost` | `08b714cdd9b466180a99c28905e5e90b67579f122afdac428568f818e2332d72` |
| `Platform.SchemaRunner` | `835ee8d29c2dac99a4934df2d34c02529deae333479340aa3f51809e4a10493d` |
| `Platform.AotSmoke` | `19a3ca077394d8fdba40bf6c1a232393793d7dcf7516b7ecba4cc0f6c215b39d` |
