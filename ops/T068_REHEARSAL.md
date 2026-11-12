# T068 isolated single-brand cutover rehearsal
Status: **NOT COMPLETE**;81/96 unchanged. **Step1 is implemented and synthetically qualified:** an isolated legacy/target runtime pair for acc-abad. The common writer fence, durable callback handoff, protected-copy rehearsal and rollback remain open.
## Authority and scope
The owner authorized isolated copied-data/staging work with generated secrets and fake transports, without production writer stops, customer messages, real financial/provider effects or release. This checkpoint uses generated data only. No T066 source/target container, protected archive, original key, media or quarantined record is an input.
Base main:`c46d75bdbbe2658a410e2797d3a53a8121f78d6e`. Current AGENTS, constitution3.0.0, active spec/plan/tasks/schema/handoff, staging plan, release gates, T064/T066 qualification and T067 runbook remain authoritative.
## Runtime pair
| Component | Implementation and boundary |
|---|---|
| Legacy | Pinned application image at revision`66371a23a1c8149dc5bdf89a0355aa689d454dba`; new PostgreSQL16 database; explicit migration/instance binding; the actual legacy`purchase_item` service |
| Target | Explicit`--cutover-stage` mode in the managed AotSmoke qualification executable; existing canonical28-table fixture and actual`CheckoutStore`; one active synthetic brand/membership |
| Identity | Fresh private run identity, exact container IDs/images/labels/users/mounts and one synthetic acc-abad mapping; other generated target brands are inactive |
| Secrets | Fresh database passwords, Django/Fernet material and generated target brand/inventory keys; retained in private staging storage across restart |
| Networking | Each database has network mode`none`; its runtime shares only that database namespace. No host ports, customer ingress or production filesystem/credential mounts |
| Resources | Total memory caps992MiB with a measured preflight reserve of at least200MiB; bounded CPU/PIDs/logs and64MiB file-size limits. Same-server host failure remains shared |
| Stop controls | Exact owned-container validation and independent`--side legacy`/`--side target` stop/start. Stop remains available after bundle drift; stale/unowned identities are rejected |
Normal Api/BotHost/JobHost activation is unchanged and remains fail closed. This is an operator-controlled qualification runtime, not a production storefront or a newly qualified Native AOT artifact. Database bootstrap uses generated synthetic owner/control roles; production least-privilege/runtime security remains separately gated.
## Local command and transport contract
Each runtime accepts bounded, owner-only request files in its private requests directory. The host publishes complete, correctly owned requests through an atomic rename under a controller lock. The descriptor-based target reader and legacy no-follow reader reject unsafe files; results contain counts/outcomes without credentials or amounts.
| Command | Behavior |
|---|---|
| `status` | Read actual synthetic order, allocated-stock and debit counts |
| `wallet` | Execute one synthetic local wallet purchase through the real engine's checkout code |
| `fake_payment`,`fake_supplier`,`fake_telegram`,`fake_fx`,`fake_ai` | Explicit count-preserving simulation stubs; no financial settlement, procurement, message, FX refresh or AI call |
| `stop` | Exit the selected runtime |
Unsupported commands fail closed. Simulation stubs are not real callbacks, provider adapters, delivery or integration qualification. No bot poller, general job worker, public admin server or sender is started.
The target uses the request identity as its checkout order identity and replays the original result. The legacy wrapper persists an attempt before calling its checkout service; if a result is absent after interruption, it reports`uncertain_do_not_retry` rather than submitting another purchase. Full cutover recovery and callback capture still need their own implementation.
## Validation and retained failures
The251-case unit baseline passed. The managed build passed with zero compiler warnings/errors; changed-file format verification passed. The formatter emitted a workload-verification advisory; no workload update was attempted. Twelve host guard tests passed, including identity/image/user/network/mount/auth-policy mutations, unsafe private files, empty directories, atomic request publication, optimized-Python rejection and independent stop after bundle drift.
The actual synthetic pair passed ten runtime phases: readiness, real local checkout, five fake transport responses, independent stop/restart and malformed-FIFO rejection/recovery for each engine. Both independently retained one synthetic order/allocation/debit. Target replay created no additional effect; legacy uncertainty remained paused. These are runtime-foundation checks, not an old/new shared-unit allocation fence or rollback.
Failures were retained and corrected:
- The first managed build rejected missing Linux platform guards. The corrected build passed.
- The first runtime probe exposed publication of a request before its ownership/content were complete. Both runtimes stopped safely; atomic publication gained a regression test, and fresh probes reused the preserved data without resetting accepted effects.
- PostgreSQL's generated localhost`trust` rules were discovered before authentication qualification. The owned staging policies were sealed to SCRAM/reject rules. The launcher now seals and verifies actual HBA paths, accepts the generated password and rejects an incorrect password before any runtime starts. Complete runtime checks were rerun under SCRAM.
The generated [step1 evidence](evidence/t068-paired-runtime-step1-2026-11-12.json) binds exact source, runtime bundle, images, logs, authentication probes and final gate flags. Private logs, results, failed attempts, staging databases and generated keys remain on the server. All four new pair containers are stopped after verification; all preexisting containers and four protected T066 copies retain their prior state.
## Operator use
Private checkpoint:`/var/lib/teleg-cli/t068-pair-20261112`. Read its execution ledger and validate exact resources before reuse. The public launcher intentionally accepts only private root-owned paths beneath`/var/lib/teleg-cli/t068-pair-*`; ownership fixtures require root on the owner server.
`python3 ops/staging/cutover_pair.py validate --root <private-root>` verifies the installed pair. Explicit`start`,`stop` and`status` actions support independent side controls. Initial`prepare` requires a fresh reviewed build/tools directory and measured headroom; it does not select existing production services. Never repoint this synthetic harness at production or protected import endpoints. An environment label or a saved checkpoint alone is not execution proof.
## Remaining T068 gates
| Gate | Reusable evidence | Remaining work |
|---|---|---|
| Frozen preservation/reconciliation | T063/T064/T066 qualified their exact frozen scopes | Bind new protected rehearsal clones to matching inputs and verify start/end commitments |
| Paired runtime | Step1 synthetic pair, actual checkout and independent stop/restart | Bind the eventual authorized-copy rehearsal without importing production secrets into ordinary hosts |
| All writer classes fenced | Existing consumer-lock boundary tests prove its limits | Enforce one common authority across old/new bot, callback, admin, job/import and stock writers, including stale connections and restart |
| Callback/update handoff | Local fake command channel only | Durable owner/epoch, freeze-window capture and safe late/duplicate/foreign/uncertain-event replay |
| Stock ownership | Generated units are distinct; original stock is not imported | Audited custody/segregation and actual competing allocation proof for the same unit |
| Handoff interruption/restart | Step1 runtime persistence and fail-closed malformed-input checks | Interrupt every actual handoff boundary and verify accepted money, stock, jobs and delivery obligations |
| Rollback | T064 frozen restore proof; T067 procedure | Execute paired rollback before acceptance, then preserve/reconcile accepted target effects before any old owner resumes |
| Independent final review | Source-bound step1 runtime evidence | Review complete actual staging execution before checking T068 |
Next: implement step2, the common enforced writer/stock fence, using this pair. T068, T075/T076/T080/T081 and production release remain separately gated. All original financial, ownership, delivery and media unknowns remain quarantined.
