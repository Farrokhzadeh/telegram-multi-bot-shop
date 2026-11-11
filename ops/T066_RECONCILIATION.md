# T066 redacted reconciliation checkpoint — 2026-11-11

The shareable wallet reconciliation format contains only counts, match flags and an explicit scope. `ReconciliationReport.Redact` excludes batch identifiers and all monetary totals; private reconciliation retains those values for exact comparisons. The synthetic qualification artifact uses the same source-generated JSON format.

Amount equality is checked against the frozen expected total for both opening postings and destination wallets. Contradictory totals, a nonzero difference, incomplete evidence or missing, mismatched and unexpected mappings cannot produce a matched wallet projection. Negative counts are rejected. Matching wallet evidence does not certify financial obligations or release readiness; both flags remain false.

This checkpoint adds a report format and a managed qualification path. It does not claim a fresh protected-source reconciliation, Native AOT publication, financial settlement or cutover. Existing protected-import evidence remains described in [T064 qualification](T064_QUALIFICATION.md).

T066 remains unchecked pending the complete independent historical liability, ownership and partial-order matrix, including quarantined originals, original denominations, coupon ownership, delivery and media exceptions. T068 remains separately gated. The owner-approved preservation policy remains in force; unknown source facts stay unknown.

Validation:218 baseline unit cases,224 final unit cases and one focused PostgreSQL integration case passed; final builds have zero warnings/errors and changed-file whitespace checks pass. The first integration build failed because an untracked runtime symlink included generated assembly files; its logs were retained and the symlink was excluded during the successful rebuild. The synthetic JSON artifact was independently checked against an exact property allowlist. The test database was stopped and all three production bots retained their original IDs, images, start times and restart counts;13 original containers remain running. No CI was invoked. See [redacted evidence](evidence/t066-redacted-report-format-2026-11-11.json).
