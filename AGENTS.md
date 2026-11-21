# AGENTS.md

## Authority and scope

- Akanti (آکانتی) is the project name. AccAbad (`acc-abad`) is the first migration brand. Use the project name for general product documentation and the brand identifier for brand-specific configuration and evidence.

- Use Spec Kit. Resolve the active feature from [.specify/feature.json](.specify/feature.json) using `.specify/scripts/bash/check-prerequisites.sh --paths-only --json`.
- Before implementation, read the [constitution](.specify/memory/constitution.md) and the active feature's spec, plan, tasks and handoff when present. Consult its contracts, referenced feature specifications and relevant qualification evidence for affected work; do not treat historical checkpoints as current status.
- Latest main and current owner decisions are authoritative. Use tasks and qualification records for status; dated plans, research, branch names, test counts and checkpoint notes describe their original scope.
- Continue eligible authorized offline implementation without routine task-by-task confirmation. Existing approvals retain their exact data, instance, account and recipient limits. Installed commands and task completion do not expand authorization.
- Legacy `Farrokhzadeh/AccAbad-Shop`, its pinned revisions and historical paths are source evidence; inspect context before treating them as target implementation.
- Preserve approved decisions and constitutional amendment provenance. Governance changes require explicit owner authorization and the constitution workflow. The [previous approval/qualification log](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/blob/ba7785e0f9da331cfa426470a21928adad1ba13f/AGENTS.md) preserves historical approval/evidence provenance; consult it only when needed. Its superseded workflow and status statements are not current instructions.

## Execution

- Work directly on main; never create branches. Batch related validated changes by task. The owner authorizes build and test CI whenever the work requires it. Use `[skip ci]` for changes that do not need a new run. Deployment and cutover retain their separate authorization.
- Work without subagents unless the owner explicitly requests delegation.
- Map existing implementation and evidence to the task's remaining gates before coding. Reuse functions, dependencies, qualified builds, pinned test images and isolated restores only when inputs, scope and freshness match.
- Before expensive work, check access, tool availability, disk/memory, backup readability, required keys, isolated database connectivity and workspace hazards. Validate representative synthetic cases with the intended image, UID, mounts, authentication, resource limits and empty-data/directory cases before protected execution.
- Use one isolated staging brand by default for implementation and recovery checks. Include additional staging brands only for affected shared-state/tenant-isolation scenarios or required final gates; reuse matching prior evidence. Do not involve production bots in routine detail checks. The owner is the product manager: derive technical evidence from available copies/tools and ask only concrete product decisions that cannot be resolved from approved requirements.
- Run requested baseline checks, focused checks while changing code and full checks at applicable phase/final gates. Rebuild when code/dependencies change; repeat broader passing checks only for changed inputs, failures or unresolved concerns. Keep tests meaningful and report unavailable checks honestly.
- Run long work in bounded, resumable background jobs with private logs, exit codes and progress. Bound CPU, memory, connections and concurrency. Run independent jobs concurrently only within those limits; avoid concurrent mutation of shared files or databases.
- Bind checkpoints to input identities, scope and tool/runtime version. Revalidate them as required; caller counts, unsigned receipts, sampling or checkpoint presence alone do not prove full checks ran. Investigate discrepancies, retain failed attempts and record their corrections.
- Keep a concise execution ledger: current commit, validated artifact hashes, completed gates, private evidence references, blockers and exact next action. Prefer evidence-generated redacted summaries over manually transcribed counts.
- Preserve empty directories during staging. Validate managed .NET JIT backing-file limits separately from memory limits using a representative runtime probe; reuse qualified settings only where applicable.

## Data and operational safety

- Use accabad-server only for authorized isolated copied-data work. Inspect ownership, names/labels, image, mounts, authentication and network isolation before restarting a copied instance. Never touch live databases, bots or production media as part of copied-data qualification.
- Use explicit read-only transactions and restricted database roles for reconciliation. Verify source identity/commitments at start and end and required target/archive/quarantine coverage. Index creation is a write and is permitted only on an explicitly authorized isolated copy; reconcile its schema effects before qualification.
- Keep customer records/identifiers, monetary values, keys, private receipts and protected evidence on the owner server. Publish only authorized redacted evidence; secrets stay outside source control. Check file ownership, permissions, symlink/hard-link safety, input hashes and staging boundaries.
- Preserve every ambiguous original under the approved quarantine policy. Never infer settlement, ownership, delivery, financial relationships or release readiness from missing facts. Preserve denominations, historical money and original references; never guess FX, activate imported discounts/stock or waive liabilities. Retain keys while any archive, backup or rollback dependency remains.
- Staging/cutover, real supplier/payment/Telegram/FX/AI calls, production mutation, deployment and key retirement retain their separate authorization/evidence gates. Prevent old/new simultaneous saleable stock and preserve one-brand writer/callback ownership, rollback and independent stop controls. Same-server staging shares the host failure boundary; never run host-destructive tests there.
- Keep unfinished tasks unchecked and normal hosts fail closed until their specific gates pass. Preserve read-only reviewer checklists and their recorded hashes. Synthetic success, governance review and partial evidence qualify only their stated scope.
- For legacy migration work, consult [T064 import/preservation](ops/T064_QUALIFICATION.md), [T066 reconciliation](ops/T066_RECONCILIATION.md), and the affected feature's schema, contracts and release evidence. Their conclusions apply only to their recorded scope.
- Preserve approved product and operational decisions in the applicable specifications and contracts. For shared commerce and supplier work, consult the [schema contract](specs/001-multibrand-redesign/schema-contract.md), [supplier matrix](specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md) and [API inventory](ops/API_INVENTORY.md); documentation alone does not authorize live calls.

## Code, environment and communication

- Choose the simplest correct solution. Maximize compactness and prefer single-line code where valid, while preserving syntax, behavior and enforced formatting. Use one literal tab per indentation level wherever language/tooling permit; never mix indentation styles within a block.
- Add no code comments; preserve required licenses and tooling directives. Prefer concise meaningful names, small changes and existing utilities/patterns; avoid duplicate logic, speculative abstractions and unrelated reformatting.
- Default to existing local/home-network services, localhost binding and configurable hosts/ports. Inspect available tools rather than repeating historical environment assumptions.
- Spec Kit uses the installed Bash/Linux scripts in `.specify/scripts/bash/`, with `--json` and long-form options; do not invoke removed PowerShell scripts. Preserve pinned tooling, full template composition and fail-closed resolution.
- Keep responses short, accurate and accessible. Explain unfamiliar concepts plainly, report milestones and actual blockers, and omit raw diffs and internal deliberation.
