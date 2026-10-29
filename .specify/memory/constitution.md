<!--
SYNC IMPACT REPORT
Version: 1.0.0 -> 1.1.0
Reason: replace premature technology and benchmark mandates with non-negotiable production migration safeguards.
Scope: C# Native AOT feature documentation; no running application changes.
Updated: 2026-10-15
-->

# Account Abad Fleet Constitution

**Version**: 1.1.0 | **Ratified**: 2026-10-15 | **Last Amended**: 2026-10-15

## Core principles

### I. Behavioral parity and customer safety (NON-NEGOTIABLE)
A platform rewrite MUST preserve existing customer-facing and administrator workflows unless an explicitly approved, independently specified change replaces them. This includes purchases, balances, pricing, receipt approvals, inventory allocation, delivery, services, Telegram UI/callbacks, AI support, translations, notifications, and panel operations. The currently deployed Python application and its tests/documented behavior are the reference; every workflow MUST be inventoried with source anchors and measurable acceptance evidence. Historical records are never reconstructed from current prices.

### II. Preserve data, identities, and encryption (NON-NEGOTIABLE)
Existing PostgreSQL data and the per-instance database/stage binding MUST remain authoritative. Preserve actual table names, primary key types, relations, constraints, decimal precision, currencies, and identifiers; do not assume that similarly named replacement entities are schema-compatible. All existing Fernet ciphertext formats and tenant-specific FIELD_ENCRYPTION_KEY / previous-key rotation behavior MUST remain readable, including legacy/plaintext compatibility where the current readers permit it. New encryption formats require separately reviewed, reversible migrations with backup and round-trip evidence. Production databases MUST NOT undergo automatic schema mutation on service startup.

### III. Deployment isolation and recoverability (NON-NEGOTIABLE)
Preserve three separately deployable production instances and a separate staging instance, each with isolated process/container, bot token, DB role/database, encryption key, admin credentials, media, hostname, and release lifecycle. Shared infrastructure may continue to exist, but failure, upgrade, and rollback boundaries remain per instance. Use staging first, then prod1 canary, then prod2 and prod3 with the same validated immutable artifact. Take verified backups and preserve known-good rollback images; never deploy a feature branch to production.

### IV. Reliable financial and background side effects (NON-NEGOTIABLE)
A purchase or receipt approval MUST NOT charge/credit twice, allocate stock twice, deliver credentials repeatedly, or lose a committed job on crash. Keep PostgreSQL-backed durable jobs and recovery semantics (dedupe, transactional enqueue, leases, retries, persisted delivery markers). In-memory Channels MAY be used for bounded wake-up/dispatch, not as the sole record of outstanding business work. External callbacks/webhooks require verification and replay protection.

### V. Security, tenant scope, and privacy
Every administrative action requires authentication, authorization, CSRF defenses for state-changing browser requests, and tenant-bound object lookups. Telegram callbacks MUST authorize the actor and the target operation. Never put bot tokens or passwords in URLs, repositories, logs, or config templates. Retain scoped media permissions, secure session cookies, encryption key separation, least privilege, redaction, and secret-free backups/reports. Sensitive media and credentials remain accessible only to authorized users.

### VI. Evidence-based implementation and efficiency
Prefer simple, modular components and independently verifiable service boundaries. Target supported .NET LTS for a C# rewrite, with Native AOT as a goal subject to a successful Linux publish and real dependency/runtime checks. No library, ORM, templating system, process consolidation, single-file packaging, startup time, or memory target is mandatory without feasibility evidence. Document baseline and target measurements before asserting performance gains. Native AOT must not require dropping functionality or safety.

### VII. Focused verification and traceability
Maintain feature-to-code-to-test traceability for critical flows. Prioritize targeted unit/contract tests and a small set of realistic PostgreSQL/Telegram-sandbox integration and failure-recovery tests; avoid redundant test suites. Tests are required for money, encryption, tenancy, migration, and delivery. Require warning review and an actual published Native AOT smoke test before claiming AOT compatibility.

## Development and release gates

1. Establish current main/staging baseline, feature inventory, schema inventory, encrypted sample fixtures, and operating resource baseline.
2. Complete a dependency spike before choosing data access/HTML/Telegram implementations; record warnings and fallback options.
3. Run against disposable copies of staging data with distinct credentials and no live payment or customer messages.
4. Demonstrate parity, tenant isolation, restore/rollback, and a publish-and-run probe before moving any traffic.
5. Treat changes to schema, money flows, security, and deployment as review blockers until validation evidence exists. Keep the current Python fleet operational during development.

## Governance

This constitution supersedes feature plans. Any exceptions require a written rationale, risk assessment, approval, and a version amendment (MAJOR removes/weakens safeguards, MINOR adds principles, PATCH clarifies wording). A checked checklist means verified evidence, not an intention. The C# feature remains a **draft migration design**, not an authorized production cutover.
