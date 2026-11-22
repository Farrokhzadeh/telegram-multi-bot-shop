# Feature Specification: Full EF Persistence and Encryption Removal
**Working branch**: `main` (no feature branch)
**Feature directory**: `specs/005-full-ef-no-encryption`
**Created**: 2026-11-22
**Status**: Specified; implementation not started
**Input**: Owner requests only items 1 and 2: full Entity Framework conversion and complete removal of application encryption from new Akanti. Owner will manage protection of stored data. This invocation creates the specification only.
## User Scenarios & Testing *(mandatory)*
### User Story 1 - Maintain the complete commerce model (Priority: P1)
As the maintainer, I can find and maintain the complete Akanti data model in one consistent entity-based persistence system. Completing one administrator query must never be presented as completing the conversion.
**Why this priority**: The current EF feature covers only the administrator product list; the requested scope is the entire application.
**Independent Test**: Compare the 28-table inventory and every maintained runtime read/write path against the converted model and behavior evidence; exercise catalog, administration, checkout, payment, delivery, supplier review and background recovery with synthetic data.
**Acceptance Scenarios**:
1. **Given** the canonical 28-table model, **When** a maintainer inspects persistence, **Then** every table has an explicit entity, relationships and validation rules in the conventional `Entities/` model, with no unmapped application table.
2. **Given** an existing customer or administrator operation, **When** it executes through the converted persistence system, **Then** its permissions, visible results, ordering and replay behavior remain equivalent except for explicitly approved encryption changes.
3. **Given** two purchases of the last item or a repeated payment callback, **When** they race or retry, **Then** at most one allocation and one corresponding financial posting occur; an incomplete operation rolls back without partial liability changes.
4. **Given** a cancelled write, stale administrator session or interrupted job, **When** the operation resumes or retries, **Then** authorization, transaction integrity and uncertain-outcome fences remain enforced.
### User Story 2 - Operate new Akanti without application encryption (Priority: P1)
As the owner, I can operate new Akanti without configuring encryption keyrings or encrypted-field services. Akanti stores and reads its operational content directly; I control protection of its host and storage.
**Why this priority**: The owner explicitly removed application encryption from the target product, including new writes and later reads.
**Independent Test**: Start a disposable target without application encryption keys, persist synthetic credentials, payment evidence, support content and configuration, restart all roles, and verify authorized reads and workflows still work without encrypt/decrypt calls.
**Acceptance Scenarios**:
1. **Given** a fresh target with no application encryption key files, **When** API, bot and worker start, **Then** their target workflows require no encryption services or key manifests.
2. **Given** authorized synthetic inventory, delivery credentials, prepaid codes, supplier evidence, support content or stored provider configuration, **When** Akanti writes and reads it, **Then** the stored operational value is directly readable without application decryption and is never replaced by an encryption-shaped placeholder.
3. **Given** another brand or an unauthorized actor, **When** it requests stored content, **Then** existing authorization still denies access; removing encryption does not publish credentials, receipts or customer data in logs or responses.
4. **Given** an existing encrypted target copy, **When** conversion is interrupted or a necessary old key is missing, **Then** the original remains recoverable and services refuse the incompatible target rather than silently dropping values or mixing representations.
### Edge Cases
- Missing, duplicate or cross-brand references must fail without partial writes.
- Exact money and original denomination survive conversion, including outstanding or quarantined obligations; USD and USDT must not be silently merged.
- SQLite writer contention, cancellation, job lease expiry and process restart must preserve accepted work and avoid duplicate external effects.
- Financial multi-row assertions and append-only audit/posting rules remain effective after conversion.
- Legacy source encryption, historical backups and target runtime encryption are distinct: existing source material must remain readable without reintroducing encryption into the target runtime.
- An old encrypted executable cannot safely resume against a converted plaintext schema; unsupported rollback must stop for recovery.
- Plain content containing Unicode, binary attachments, empty supported fields and large bounded evidence must roundtrip exactly, with unambiguous types.
## Requirements *(mandatory)*
### Functional Requirements
- **FR-001**: Akanti MUST use Entity Framework Core for the complete maintained target persistence layer, with explicit entities under `Entities/`, explicit mappings and a coherent context model for all 28 canonical tables. This framework requirement is an explicit owner constraint.
- **FR-002**: Conversion MUST cover every maintained API, bot, worker, administrator, commerce, audit, history, support and maintenance read/write path; a read-only catalog slice or parallel SQL implementation does not satisfy completion. SQLite is the current target; any retained Akanti PostgreSQL runtime must also use the converted persistence layer, or be explicitly retired from runtime use. Legacy source readers are separate.
- **FR-003**: Target schema evolution MUST be owned by reviewed EF migrations and model snapshots. Existing SQL baselines and pinned evidence remain preserved historical inputs; daemons MUST NOT silently apply migrations at startup. Existing-file adoption and schema changes require an explicit validated upgrade procedure.
- **FR-004**: Handwritten SQL MUST NOT remain the target's normal business query/write mechanism. Planning MUST inventory and justify any narrowly required provider setup, schema DDL or integrity primitive; such exceptions cannot hide an unconverted commerce store. Legacy read-only inspection/recovery tooling and historical SQL evidence are outside the runtime conversion.
- **FR-005**: Conversion MUST preserve tenant scope, intentional shared inventory, pagination, exact amounts and denominations, ownership/provenance, immutable financial history, idempotency, cancellation and transaction boundaries. Financial and cross-table guards must be demonstrated under contention and failure, rather than removed to fit the new persistence approach.
- **FR-006**: New Akanti MUST remove application-level encryption for its operational storage, including inventory/fulfillment content, prepaid discount codes, brand/provider secrets, support content, media/evidence and supplier attempts wherever currently encrypted. The runtime MUST NOT depend on encryption keyrings, encrypt/decrypt services, encrypted-field wrappers, key grants or encryption-specific IPC bridges.
- **FR-007**: Target model fields, schemas, interfaces, configuration, host wiring, deployment manifests and applicable tests MUST express the new unencrypted representation consistently. Retired ciphertext/key-version fields must not remain active compatibility requirements. Required existing operational separation and authorization must remain effective when encryption-specific machinery is removed.
- **FR-008**: Encryption removal MUST retain password/token hashing, signature verification, authentication, authorization, transport security, redaction and restricted file access. These mechanisms do not encrypt target application content and are not authorized for removal by this feature.
- **FR-009**: Existing encrypted target data MUST have an explicit conversion design with an immutable original, complete field/row coverage, repeatability, interruption recovery and exact content/financial reconciliation. Unknown or unreadable content must remain preserved and block affected activation; no silent plaintext fallback or destructive in-place reinterpretation is permitted. Protected-data execution requires its separately authorized scope.
- **FR-010**: Legacy encrypted source readers and required historical keys MUST remain isolated to authorized migration/recovery tooling while any source, archive, backup or rollback still requires them. They MUST NOT become target runtime encryption dependencies. This feature does not authorize source alteration, backup deletion or key retirement.
- **FR-011**: Compatibility changes MUST retain externally visible contracts unless an encryption-specific field or configuration dependency requires an explicit documented change. No new user-facing business flow, pricing policy, denomination policy or live provider activation is introduced.
- **FR-012**: Completion MUST include a table/entity inventory, runtime path inventory, remaining SQL exception register, encryption removal inventory and traceable synthetic acceptance evidence. Existing completed feature checklists remain historical and do not prove this feature complete.
- **FR-013**: The owner decision superseding mandatory target encryption MUST be recorded in the next planning phase's scoped governance amendment, including rationale and risks; legacy preservation and other safety requirements remain in force. This specification records the decision without silently rewriting historical approvals or pinned documents.
### Key Entities
The existing 28 logical entities remain the scope; encryption removal changes their content representation, not their business identities. Detailed fields and relationships will be derived from the current schema during planning.
| Area | Canonical tables |
| --- | --- |
| Brand and access | brands, brand_channels, users, admins, admin_sessions |
| Catalog and stock | products, brand_products, inventory_items, product_supplier_sources, product_stock_subscriptions |
| Money and orders | wallets, wallet_entries, orders, payments, fulfillments |
| Supplier and background work | supplier_requests, supplier_request_attempts, jobs, campaigns, telegram_deliveries |
| Support and history | support_tickets, support_messages, legacy_id_map, audit_events, bot_activity_events |
| Discounts | discounts, discount_uses, discount_revisions |
## Success Criteria *(mandatory)*
### Measurable Outcomes
- **SC-001**: All 28 existing tables and 100% of maintained operational read/write paths have a recorded disposition and verified coverage; no business workflow remains on an unconverted persistence path.
- **SC-002**: API, bot and worker can start and complete the applicable synthetic customer/admin flows without any target application encryption key, and authorized operational content roundtrips unchanged across full role restart.
- **SC-003**: Synthetic last-item contention, duplicate payment, cancelled transaction, stale admin, job lease/restart and uncertain delivery tests produce zero duplicate debits, credits, allocations or automatic uncertain resends, with zero cross-brand disclosures.
- **SC-004**: Before/after conversion checks account for 100% of fixture records and content fields, preserve exact balances, liabilities and denominations, and demonstrate interrupted conversion recovery without losing the original.
- **SC-005**: All affected existing behavior/contract checks and focused new acceptance checks pass at the exact candidate revision; every remaining provider-specific exception is individually recorded and justified. No completion claim relies on earlier catalog-only or synthetic staging qualification.
- **SC-006**: An incompatible old executable is refused against the converted target; the documented recovery path preserves all accepted writes or stops safely for operator recovery.
## Assumptions
- Scope is only full EF conversion and target application encryption removal. AccAbad table/column mapping, protected legacy import, remaining feature 002 tasks, provider activation and production release are excluded.
- This invocation performs `specify` only. Implementation planning, task generation, code changes, data conversion and deployment are subsequent work.
- Baseline is main `c44dc1bde2966d866cdcca771fd5d8921621bf57`; feature 003's completed catalog query and feature 004's completed synthetic SQLite staging do not establish full EF coverage.
- The owner has already chosen to manage host/storage protection. Planning must record plaintext exposure in database files, WAL, evidence files and backups and retain access/redaction controls without adding new application encryption.
- Existing source/backup key preservation is compatible with removing encryption completely from new Akanti; no request to destroy old source material or necessary recovery keys was made.
- Historical constitution 3.1.0 and features 003/004 still require target encryption. This newer explicit owner direction governs the proposed feature; a scoped prospective amendment belongs in planning before implementation, without changing pinned historical artifacts.
