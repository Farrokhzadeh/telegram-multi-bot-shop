# Supplier adapter quarantine — 2026-11-04

The five provider adapters introduced by `d7bda6d1e54748d7f1d5adf97731fbd07ed82138` and patched through `511bfe6e2bc688b9e0b29dbcb98fed367be39664` have been removed from the build with their self-authored happy-path mock tests. Their source remains available in Git history. The provider-neutral worker, contracts, original-account reconciliation, cost checks, protected acquisition and complete-order release gates are retained.

The removed adapters asserted maximum-charge enforcement while ignoring the ceiling argument, inferred no-effect from generic HTTP failures, assumed unverified currency/identity/recovery shapes, and interpolated unescaped JSON. Mock responses did not qualify the actual provider contracts. Do not restore or enable them merely to make T057/T091 appear complete.

Reintroduction requires sanitized authoritative evidence for the exact account/product/variant, ready-item eligibility, currency, a provider-enforced maximum charge, stable purchase/item identities, uncertain outcomes and original credential recovery. Follow `specs/001-multibrand-redesign/contracts/supplier-provider-matrix.md`; keep all provider-dependent task gates open. No real supplier calls, credentials, production data, schema changes or deployment are authorized by this repair.

Recent offline schema tests are retained with corrected cost ceilings, unknown-outcome assertions, SQL parameters, foreign-reference fixtures, independent retention checkpoints and cross-brand card uniqueness fixtures. Formatting follows the enforced repository settings. Full exact-commit CI is required before claiming qualification.
