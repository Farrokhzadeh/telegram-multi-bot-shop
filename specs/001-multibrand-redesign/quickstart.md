# Offline validation quickstart — executable evidence and remaining gates

This quickstart runs only synthetic fixtures and owned disposable databases. It is not a deployment recipe. Normal Api, BotHost and JobHost executables fail closed without qualification mode. Do not use production/staging connection strings, real keys or source rows, live Telegram/payment/supplier/FX/AI services or actual recipients. The AI runtime belongs in a separate service; commerce contains only its optional read/advisory boundary.

The canonical baseline is constitution 3.0.0, 28 typed tables with no JSON columns, and the immutable financial, ownership, replay, coupon and complete-manifest contracts. [Requirements traceability](requirements-traceability.md) assigns all 84 FRs and 53 SCs to tasks. The [scenario catalog](../../ops/acceptance-scenarios.tsv) supplies exact executable witnesses and a digest of each current SC statement. A witness proves only its synthetic assertion subset; it never promotes a whole criterion, human result or environment to release-ready.

## Required environment

Use .NET SDK pinned by global.json, locked dependencies, Linux Native AOT build tools and the exact PostgreSQL version pinned by [CI](../../.github/workflows/backend-ci.yml). PostgreSQL must be an independently owned disposable service whose identity is approved for destructive synthetic database creation; its control role must have only the qualification provisioning permissions used by [DisposablePostgres](../../tests/Shared/DisposablePostgres.cs) and migration-role tests. Each test creates and drops its own synthetic database. Do not substitute a source copy, staging, production or shared database.

`SHOP_TEST_ALLOW_DATABASE_CREATION=1` and `SHOP_TEST_POSTGRES_ADMIN` must be set by the disposable test harness after identity verification. The latter is a private local/CI setting, never a command-line argument, artifact or copied real connection string. See the pinned CI service for the exact ephemeral role/membership provisioning and tooling. No local .NET/PostgreSQL/Docker availability or execution is assumed; the qualification record identifies the actual CI run that supplied proof.

## Complete offline checkpoint

From repository root, with that disposable harness already configured:

```bash
bash .specify/scripts/bash/check-prerequisites.sh --json --require-tasks --include-tasks
python3 .specify/tests/test_linux_tooling.py
python3 -m unittest discover -s ops/ci/tests -v
python3 ops/ci/check-acceptance-evidence.py --catalog-only
python3 ops/ci/check-secret-compose.py
dotnet restore Platform.sln -r linux-x64 --locked-mode
dotnet build Platform.sln -c Release --no-restore
dotnet format whitespace Platform.sln --verify-no-changes --no-restore
dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=qualification.trx' --results-directory artifacts/test-results/UnitTests
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=qualification.trx' --results-directory artifacts/test-results/IntegrationTests
dotnet test tests/Platform.ContractTests/Platform.ContractTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=qualification.trx' --results-directory artifacts/test-results/ContractTests
bash ops/ci/native-smoke.sh
python3 ops/ci/check-acceptance-evidence.py --results-root artifacts/test-results --output artifacts/acceptance/scenario-witnesses.json
sha256sum artifacts/acceptance/*.json
```

Stop on any failed command. The CI workflow also runs the pinned redacted secret scanner and uploads TRX, redacted contract/migration/acceptance artifacts and lock files. The native script publishes and runs Api, BotHost, JobHost, SchemaRunner and AotSmoke, verifies expected fail-closed host exits and loopback authorization/error contracts, and runs source-generated JSON and the database/encryption/financial/delivery probes. It does not activate live callbacks, schedule business runtime work, invoke real services or deploy.

The combined T077 command, after build, is:

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName=Platform.IntegrationTests.AcceptanceSuite.OneDisposableRunRetainsMoneyStockDeliveryAndOriginalSupplierIntentAcrossRestarts
```

That single test owns one 50-member, three-brand synthetic shop. It races one last local item, reconstructs leased manifest/edit services after persisted lease expiry, verifies the original private edit target/generation and checkout replay, settles one original membership's crypto-only USD topup 100 times, switches the test product to a synthetic selected supplier, accepts a local-three/remote-two mixed order, reconciles its uncertain original submit without repurchase and delivers one whole five-unit manifest. It checks exact final balances, two completed orders, six released units, one topup posting and no invented compensation. This is service reconstruction and synthetic transport recovery, not an operating-system crash, live provider or staging durability claim.

AotSmoke reruns the same helper and emits `artifacts/acceptance/synthetic-commerce.json` only after all its assertions pass. The report contains aggregate counts, scope and ReleaseReady=false; it contains no member/order/provider identity, plaintext credential, message body, connection string or key. `scenario-witnesses.json` is emitted only from actual complete passing TRX suites. It checks mapped test definitions/results and records observed-synthetic-subset versus planned-unexecuted, exact SC digests and remaining evidence. It always retains fullSuccessCriteriaVerified=false and releaseReady=false. Missing witnesses, duplicate criteria, stale specification digests and failed/skipped/incomplete test results stop report generation. Unit tests exercise those negative paths.

## Commands and scope for every SC

Render the exact current per-project test filters with:

```bash
python3 ops/ci/check-acceptance-evidence.py --print-command SC-001
```

Replace SC-001 with any row below. This only prints commands; it does not run them or produce observed evidence. SC-008 and SC-040 print their explicit planned/unexecuted reason instead of substituting unrelated tests. Individual filtered reruns are diagnostic and do not replace the complete checkpoint. All witnesses also run in the unfiltered three-suite checkpoint above. Exact witness names are in the TSV; the following limits are part of the evidence, not exceptions to the specification.

| Criterion | Current executable scope | Evidence still unexecuted or outside the witness subset |
| --- | --- | --- |
| SC-001 | Synthetic witness subset; render its command | Actual deployed media/routes/recipient and security review under T075 remain unexecuted. |
| SC-002 | Synthetic witness subset; render its command | Synthetic last-unit race only; real hosting/load and representative baseline remain unqualified. |
| SC-003 | Synthetic witness subset; render its command | Synthetic authenticated lookup/replay only; real invoice creation, callbacks and provider lookup activation remain unqualified. |
| SC-004 | Synthetic witness subset; render its command | Reconstructed services and synthetic uncertain transport only; actual process/scheduler/production durability gates remain open. |
| SC-005 | Synthetic witness subset; render its command | Wallet-projection primitive only. Actual source schema, complete historical commerce import/reconciliation and cutover remain unexecuted T063/T064/T066/T068/T080. |
| SC-006 | Synthetic witness subset; render its command | Backend synthetic HTTP/service assertions only; end-user deployed UI and real environment acceptance remain unexecuted. |
| SC-007 | Synthetic witness subset; render its command | AI-off and synthetic commerce only. Separate AI service hosting and live integrations are not implemented/activated. |
| SC-008 | **Planned/unexecuted; no test substitute** | Human participant measurement is NOT RUN. T079 defines the preregistered protocol; automated tests cannot populate its result. |
| SC-009 | Synthetic witness subset; render its command | Synthetic verified ingress/staff replies only; live Telegram routing and deployed restart/durability remain unqualified. |
| SC-010 | Synthetic witness subset; render its command | Synthetic campaign/transport/throttling only; real recipients, credentials, independent process controls and staging separation remain unverified. |
| SC-011 | Synthetic witness subset; render its command | Service/callback representations only; actual participant-facing deployed quantity journey remains unexecuted. |
| SC-012 | Synthetic witness subset; render its command | Synthetic FX/quote assertions only; real Nobitex transport and representative source/deployment parity remain unqualified. |
| SC-013 | Synthetic witness subset; render its command | Expiry intents/evidence guards are observed; complete timer/instruction message editing coverage T082-T085 remains pending. |
| SC-014 | Synthetic witness subset; render its command | Synthetic typed member ownership only; actual imported identities and deployed routes remain unverified. |
| SC-015 | Synthetic witness subset; render its command | Typed synthetic balances/postings only; full historical totals/source reconciliation remain unqualified. |
| SC-016 | Synthetic witness subset; render its command | Synthetic private/staff routing only; actual recipient separation and credentials remain unverified T075. |
| SC-017 | Synthetic witness subset; render its command | Synthetic replacement guards only; complete user-visible replacement/expiry edit scenarios T082-T085 remain pending. |
| SC-018 | Synthetic witness subset; render its command | Synthetic topup origin/settlement only; actual merchant invoice creation and live provider verification remain unqualified. |
| SC-019 | Synthetic witness subset; render its command | Synthetic shared-account identities only; real merchant-hosted identity review/callback activation remain unexecuted. |
| SC-020 | Synthetic witness subset; render its command | Synthetic card data and immutable destination only; no actual cards/source records or deployed media review accessed. |
| SC-021 | Synthetic witness subset; render its command | Synthetic races only; complete end-user expiry/replacement scenarios remain pending T082-T085. |
| SC-022 | Synthetic witness subset; render its command | Synthetic authenticated settlement only; real provider transport/durability and user-facing messages remain unqualified. |
| SC-023 | Synthetic witness subset; render its command | Synthetic trusted-time/expired grant semantics only; deployed message wording and live payment timing remain unexecuted. |
| SC-024 | Synthetic witness subset; render its command | Synthetic paid ownership only; imported legacy paid-code provenance must be validated, never guessed T063/T064/T066. |
| SC-025 | Synthetic witness subset; render its command | Synthetic prepaid issuance/redemption only; historical paid liabilities and deployed customer journey remain unqualified. |
| SC-026 | Synthetic witness subset; render its command | Synthetic quote/owner override assertions only; full follow-up coupon revision task T086 and participant journey remain pending. |
| SC-027 | Synthetic witness subset; render its command | Synthetic late-money recovery only; live funds/provider evidence and final Telegram edit behavior remain unqualified. |
| SC-028 | Synthetic witness subset; render its command | Synthetic staff decisions only; actual off-bot settlements, complete historical reconciliation and production review remain unexecuted. |
| SC-029 | Synthetic witness subset; render its command | Existing immutable revision/override probes only; complete T086 revision-edit concurrency/provenance qualification remains pending. |
| SC-030 | Synthetic witness subset; render its command | Selected flow/support event probes only; exhaustive T087/T088 meaningful event and 100-replay/retention coverage remains pending. |
| SC-031 | Synthetic witness subset; render its command | Synthetic partial outcomes only; actual provider typed recovery/export capabilities and complete T090-T092 follow-up remain gated. |
| SC-032 | Synthetic witness subset; render its command | Synthetic original-intent lookup only; real provider lookup/export/max-charge guarantees remain gated T057/T091. |
| SC-033 | Synthetic witness subset; render its command | Synthetic selected-source snapshots only; authoritative real provider mappings, all-source rotations and T093/T094 qualification remain pending. |
| SC-034 | Synthetic witness subset; render its command | Acceptance/FIFO head protection only; complete drain worker and late-import historical queue cases T095/T096 remain pending. |
| SC-035 | Synthetic witness subset; render its command | Synthetic source-at-acceptance cases only; full T094 source/payment races and real account replacement proof remain pending. |
| SC-036 | Synthetic witness subset; render its command | Synthetic complete-manifest holds/redemption only; real provider/transport durability and full T092 mixed recovery remain gated. |
| SC-037 | Synthetic witness subset; render its command | Synthetic eligibility flags only; no actual provider ready-item wire capabilities inferred T057/T093/T094. |
| SC-038 | Synthetic witness subset; render its command | No-date/FIFO protection only; full drain/hold/cancellation/import worker follow-up remains pending T095/T096. |
| SC-039 | Synthetic witness subset; render its command | Synthetic same-member support lifecycle only; deployed transport and actual off-bot settlement reconciliation remain unexecuted. |
| SC-040 | **Planned/unexecuted; no test substitute** | Activity 180-day cleanup/restore/replay invariants T089 are planned-unexecuted. Restock subscription cleanup is different and is not substituted. |
| SC-041 | Synthetic witness subset; render its command | Synthetic attributable bonuses only; actual bonus offers/item identity and full T092 provider recovery/access proof remain gated. |
| SC-042 | Synthetic witness subset; render its command | Synthetic zero-due binding/partial hold only; real provider safety/charge guarantees and complete follow-up qualification remain pending. |
| SC-043 | Synthetic witness subset; render its command | Synthetic inclusive nonrolling ceiling only; real atomic maximum-charge enforcement/currency/fees need authoritative provider proof T057. |
| SC-044 | Synthetic witness subset; render its command | Synthetic stock/eligibility observations only; actual direct provider refresh and production availability remain unqualified. |
| SC-045 | Synthetic witness subset; render its command | Synthetic consent/recovery only; real stock/provider observations and Telegram delivery remain unqualified. |
| SC-046 | Synthetic witness subset; render its command | Synthetic direct-refresh seam only; real authoritative wire reads and full source mapping/race proof remain gated T057/T094. |
| SC-047 | Synthetic witness subset; render its command | Synthetic local-first mixed complete delivery only; full source/FIFO follow-up and real provider transport remain unqualified. |
| SC-048 | Synthetic witness subset; render its command | Synthetic audited hold/resume/cancel decisions only; complete queue-blocking drain/FIFO/import behavior T095/T096 remains pending. |
| SC-049 | Synthetic witness subset; render its command | Synthetic retry/credit/unknown proof only; actual provider no-effect guarantees and protected case routing remain unqualified. |
| SC-050 | Synthetic witness subset; render its command | Synthetic binding/policy only; real private binding and deployed channel/recipient security review remain unexecuted T075. |
| SC-051 | Synthetic witness subset; render its command | Synthetic public-contact/staff metadata only; actual shared staff chat, recipients and protected credentials remain unverified T075. |
| SC-052 | Synthetic witness subset; render its command | Synthetic collision/ownership cases only; actual historical source identifiers/import mapping remain unqualified T063/T064/T066. |
| SC-053 | Synthetic witness subset; render its command | Synthetic restock subscription cleanup only; actual scheduler/backup restore/environment verification remains unexecuted. This is not activity cleanup SC-040. |

## Current evidence, independent AI and release boundary

[Acceptance qualification](../../ops/ACCEPTANCE_QUALIFICATION.md) identifies the executable source/run and artifact digests. The catalog and commands themselves are planned mappings until their actual witnesses appear in passing TRX results; no table in this quickstart claims all SC clauses passed. Every future source/provider, activity/coupon/FIFO follow-up and live-operation case remains subject to its own task and evidence gate. Existing qualified synthetic tests can overlap an unchecked future task without completing that task.

[Human acceptance protocol](../../ops/USER_ACCEPTANCE_PROTOCOL.md) defines the 95% preselected-participant measure and an explicitly NOT RUN result template. Synthetic buyers and automated pass rates are not human acceptance evidence. [AI boundary qualification](../../ops/AI_BOUNDARY_QUALIFICATION.md) covers only the shop-side read/client seam; a separate AI service is not implemented or activated.

T075 actual data/secrets/routes/recipient security separation, T076 representative legacy CPU/RSS/startup comparison and same-server stop/resource controls, T080 authorized source-copy/staging rehearsal and T081 explicit release decision/manifests remain unexecuted. T057 provider capabilities and T063/T064/T066/T068 source/import/reconciliation/staging gates remain open. See [staging plan](staging-plan.md) and [release gates](../../ops/RELEASE_GATES.md). No result here authorizes real credentials/source access, live services, staging/production changes, migration or deployment. Reviewer checklists remain read-only at 92/126.
