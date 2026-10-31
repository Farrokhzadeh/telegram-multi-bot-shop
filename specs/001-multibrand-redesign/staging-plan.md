# Staging plan — same production server

**Decision date:** 2026-10-30. **Owner choice:** same production server.
**Status:** documentation design only; no environment has been created or inspected.
Constitution 3.0.0 is active: the [2.0.0 shared-core amendment](constitution-ratification.md) and the owner-approved delivery recovery amendment are applied. The 2026-10-31 alignment reanalysis passed and closes E1 for governance only; E2–E6 remain stage-specific gates. No application implementation, infrastructure
change, migration, real payment, supplier call or Telegram message is authorized by this plan.

This fills the environment-planning gap behind T007/T080/T081. The approved business baseline
remains 28 tables, FR-001–084, SC-001–053 and 96 tasks. The later offline batch completed T001–T007 and T013; [RELEASE_GATES.md](../../ops/RELEASE_GATES.md) defines staging identity requirements, while actual staging provision/verification remains future work.
See [the evidence gates](consistency-review.md#evidence-gates-owners-and-required-proof),
[the implementation plan](plan.md) and [the constitution proposal](constitution-alignment-proposal.md).

## Environment boundary

Run one staging instance of the shared commerce backend on the production server, representing
all three test brands. This is a separate environment, not a fourth production shop or a new
shared customer identity. Containers separate services and data access; the host, kernel,
storage hardware and resource capacity remain a common failure boundary.

| Component | Planned separation |
|---|---|
| Runtime | Distinct container project, service identities, private network and explicit environment identity; no privileged containers, host container-control socket or production filesystem mounts |
| Database | Dedicated staging PostgreSQL container/process, database, roles and volumes; no production database connection or shared data directory |
| Bots | Three dedicated test bot identities, one per test brand; never reuse production tokens or consume production updates |
| Money and stock | Synthetic wallets, orders, coupons and inventory in staging only; no production allocation or financial writer |
| Secrets | New staging-only brand and shared-inventory keys, sessions and credentials; no copied production keys, tokens or admin sessions |
| Files | Separate media, encrypted assets, backups, logs and restore destinations |
| Routing | Separate private admin endpoint and test callback routes; no fallback from a test route to a production handler |
| Staff notices | Test destination and allowlisted testers only; never route to the production staff chat or customer recipients |
| Integrations | Fake payment, supplier, FX and Telegram transports initially; future test-network access is separately enabled and reviewed |

These are selected planning defaults, not deployed or verified controls. Exact host/runtime
versions, names, ports, endpoint certificates and secret references belong in the future
operator-reviewed manifests. Do not put real values in this specification.

## Resource and failure limits

Before starting staging on the shared server, measure production baseline and available
headroom. Qualify CPU, memory, process-count, connection, storage and log-growth limits, with
production capacity reserved. Do not invent limits from an uninspected server.

Staging must be stoppable without restarting production services, its database or the host.
Exercise application/container failures and bounded load within those limits. Do not perform
host reboots, global OOM, disk exhaustion, host firewall experiments or other host-destructive
tests on this server. Any necessary host-level recovery proof needs a separately authorized
disposable environment. Shared hosting cannot prove independent physical failure isolation.

## Data and external effects

Start with deterministic synthetic fixtures for all three brands and the approved business
flows. Reset and restore only staging-prefixed resources after checking the environment
identity and exact destination. No cleanup or restore command may select a production volume.

Later migration rehearsals may use only separately authorized protected source copies, under
E3/E6 controls. Preserve provenance and source evidence; masked fixtures do not by themselves
prove actual legacy financial or encryption compatibility. Any approved source-key access is
confined to the reviewed migration/recovery process, not mounted into ordinary staging hosts.

Payment/supplier simulations cover successful, failed, late, duplicated, partial and unknown
outcomes without real invoices, purchases or transfers. Simulations cannot close E2.
Future real-provider probes need explicit scope, protected credentials and exact provider
evidence; do not assume a provider offers a sandbox or create an extra merchant/account pool.

Real Telegram transport, if later authorized, uses only the dedicated test bots and allowlisted
tester DMs/test staff destination. It must pass the relevant E5 identity/privacy controls before
activation. Public customers and production staff recipients are excluded.

## Validation and promotion sequence

1. **Offline qualification:** after implementation authorization, publish/run Native AOT hosts
   with disposable PostgreSQL and fake transports; collect E3/E4 evidence as applicable.
2. **Shared-server readiness:** review the exact staging resources, resource limits, secret/data
   boundaries, routes, reset targets and independent stop procedure before deployment.
3. **Staging acceptance:** exercise all three brands together, including foreign-brand denial,
   shared stock/FIFO, wallet/callback replay, source binding, mixed complete-order release,
   staff-recipient guards, retention and restart recovery. Verify the full mapped criteria in
   [requirements-traceability.md](requirements-traceability.md), not just these examples.
4. **Restore/import rehearsal:** use disposable source copies and staging destinations. Simulate
   stopping old writers and routing ownership; compare balances, obligations, mappings,
   ciphertext and restore/recovery results. Do not stop actual production writers for a
   staging rehearsal.
5. **Controlled promotion:** proposed deployment policy is a manual staging trigger for a
   recorded immutable artifact from a reviewed main commit. Preserve its digest and redacted
   evidence; later production promotion reuses that qualified artifact. A merge/task completion
   alone grants no deployment permission. Real cutover remains a separate E6 operator decision,
   one brand at a time, with actual old-writer freeze and reconciled accepted external effects.

The manual staging-trigger policy is a planning default, not a newly approved CI workflow.
No deployment configuration or pipeline is created here.

## Evidence owners and open prerequisites

| Owner | Required staging evidence |
|---|---|
| T007 | Exact environment identities, this plan, release rules and E1 governance resolution |
| T075 | Test/production routing, credentials, data and recipient separation |
| T076 | Published-host runtime measurements and qualified shared-server resource limits |
| T074/T077/T079 | Relevant automated acceptance/recovery proof and human acceptance protocol/results |
| T080 | Staging restore/reconciliation, protected source/key evidence and verified writer ownership |
| T081 | Reviewed manifests, immutable artifact identity and separate release decision |

All these tasks remain unchecked. Actual host inventory/headroom, test bot/contact identities,
runtime versions, deployed separation, provider behavior and restore results are unverified.
The latest consistency review determines E1 governance status; E2–E6 retain their stage boundaries. Writing this staging plan closes no runtime/provider/release proof gate.
