# Release gates and environment identity contract

**Defined:** 2026-10-31 under T007. **Authority:** constitution 3.0.0, the approved staging plan and the separate offline implementation instruction.

This document defines the release/identity contract. It does not provision staging, qualify a production server, record actual bot identities or authorize a release. Offline CI uses synthetic data and fake transports; public dependency downloads do not enable a real integration.

## Gate ownership

| Gate | Current scope | Evidence required before its stage |
|---|---|---|
| E1 Governance | Closed for constitutional alignment by the 2026-10-31 review | Preserve versioned owner authorization and risk/impact records; reanalyze material design changes |
| E2 Provider contracts | Unqualified; source inventory/fakes only | Per-provider exact response/currency/evidence timing, ready-item scope, remote key/item recovery and enforceable cost cap before real adapter enablement or unattended procurement |
| E3 Legacy compatibility | Pinned code/DDL manifest and public synthetic cipher fixtures only | Authorized protected schema/migration/image comparison, real precision/denomination, identity/alias/cardinality/partial/prepaid/date liabilities, source ciphertext/old-key and archive-only refund reconciliation before affected import/compatibility freeze |
| E4 Runtime | Foundation and forward-schema qualification are recorded in OFFLINE_QUALIFICATION.md and SCHEMA_QUALIFICATION.md; full business runtime remains unqualified | Exact PostgreSQL, .NET/Npgsql/dependency and native executable identities; actual constraints, money, encryption, tenant, allocation, delivery, retention and crash tests for each implemented scope |
| E5 Operational identity/privacy | No real bot, bank, provider-hosted checkout or staff destination inspected | Verified source/target bot IDs, public contacts, bank/merchant disclosure acceptance, staff authentication and private group routing before real messaging/payment activation |
| E6 Release/cutover | No deployment or cutover authorized | Qualified staging isolation/resources, immutable artifact, protected source restore/reconciliation, all old writers stopped for the migrating brand, one stock authority and explicit per-brand operator release decision |

Completing T007 means this contract is defined. It does not complete T075/T076/T080/T081, any E2–E6 evidence, or a real staging identity acceptance. The requirements checklist remains reviewer-owned and read-only during implementation.

## Exact environment identity requirements

Staging remains on the owner-selected production server. The following names are engineering defaults to be reviewed in deployment manifests; none is an observed installed resource.

| Resource | Planned staging identity | Acceptance boundary |
|---|---|---|
| Container project | `shop-staging` | Independent stop/start; no production project membership or container-control socket |
| Private network | `shop-staging-private` | No production database route or customer ingress fallback |
| PostgreSQL instance/database | Dedicated staging process, database `shop_staging` | Separate storage and roles; exact host/port/database/server identity must be recorded and checked before operations |
| Runtime/migration roles | `shop_staging_app`, `shop_staging_migrator` | App cannot apply DDL; migrator invoked only through reviewed operations |
| Persistent volumes | `shop-staging-postgres`, `shop-staging-assets`, `shop-staging-backups` | Independent paths/ownership; never select a production volume for reset/restore |
| Three brand aliases | `stage-a`, `stage-b`, `stage-c` | Each must resolve to a verified dedicated test bot ID/username before real Telegram use; no production token/update ownership |
| Public contact/staff chat | Test-only configured contacts and one test staff destination | Exact verified Telegram IDs, linked active admin identities and allowlisted testers required before activation |
| Keys and sessions | New staging brand keyrings plus independent inventory keyring | Distinct key IDs, restricted service mounts and no production/source keys in ordinary staging hosts |
| Admin/callback endpoints | Separate private staging origin and test-only routes | Exact DNS/certificates/routes recorded before deployment; no test-to-production fallback |

The protected operator identity manifest must bind environment, brand alias, source image/commit, source database/migration fingerprint, original source instance/stage, target bot ID/username, database/server identity, key IDs, staff/contact IDs, routing and artifact digests. Store references and digests in review evidence, not raw tokens, keys, session cookies, database passwords or customer payloads. Reject missing or mismatched identities; never infer them from a name containing “staging”. Actual values remain unavailable in this offline batch.

## Synthetic CI identity

CI provisions an ephemeral PostgreSQL 18.6 service with control database `shop_test_control` and role `shop_test_runner`, exposed to the test process on loopback. The disposable fixture requires `SHOP_TEST_ALLOW_DATABASE_CREATION=1`, the exact control identity and a loopback host. It creates only new unpredictable `shop_qualification_*` databases and drops only databases it created. It has no source/production connection or customer data.

Fake Telegram and supplier HTTP handlers execute entirely in memory. The qualification-only API binds loopback, returns not-ready for business use and exposes no shop/admin/payment endpoints. Normal host startup exits with configuration-required status. The public qualification header and deterministic cipher fixture keys are test data, never environment secrets.

## Immutable artifacts and promotion

1. Build from a reviewed commit using exact SDK, dependency lockfiles and pinned CI action/tool revisions. Record commit, tree, executable hashes, target RID, versions, test results and warnings.
2. Qualify CPU, memory, connections, disk/log limits and a staging-only stop procedure against measured production headroom before starting staging. The shared host remains a common failure boundary; perform no host-destructive tests there.
3. A manual staging trigger may use only the recorded reviewed artifact after separate deployment authorization. A task, merge or green CI result does not deploy anything.
4. Rehearse import/restore only on separately authorized disposable source copies and staging destinations. Simulate writer stop/ownership for rehearsals; do not stop actual production writers for a test.
5. Production promotion reuses the qualified immutable artifact and has a separate E6 decision for each brand. Stop every old writer for that brand, reconcile in-flight money/delivery/provider effects, capture verified checkpoints and prevent shared stock from being saleable in both systems.

## Recovery and hard stops

An identity mismatch, missing key/archive dependency, unreconciled balance/paid obligation, uncertain supplier effect, unsafe charge ceiling, failed restore or unqualified isolation blocks the affected operation. Source-preserving inspection and synthetic qualification may continue independently.

Rollback preserves all brands' accepted money, committed work, original bindings and actual delivered evidence. A binary rollback cannot erase already accepted external effects; reconcile them explicitly. Keep old keys/images while any protected archive, backup or rollback procedure depends on them. No deletion or key retirement is performed in this batch.

Customer delivery prefers editing the validated saved order message after complete-manifest authorization. Suppress known success; an uncertain new send retains an observable recovery state and protected manifest access. Missing/uneditable anchors and file/multi-part cases use explicit recovery; a release cannot silently turn uncertainty into a new purchase, charge or blind replacement message.
