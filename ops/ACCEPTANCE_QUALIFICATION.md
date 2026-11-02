# Offline acceptance qualification

2026-11-02. Qualification: **PASS for T074/T077 offline scope**. Qualified source `37494d77986a8ad6d778d5a72b03f7dc7825e587`; [CI 41412847231](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41412847231). **572 .NET tests (129 unit, 333 integration, 110 contract)**, **20 Python checks (15 Spec Kit, five evidence guards)** and all five Linux Native AOT executables passed, with zero failed/skipped tests and zero build warnings/errors. Formatting, redacted secret scan, locked restore and Compose checks passed. **71/96 tasks complete, 25 unchecked. Phase 10 remains incomplete.** T079 has a prepared protocol/template but remains open for actual human result publication.

## Commands and actual scenario witnesses

The [quickstart](../specs/001-multibrand-redesign/quickstart.md) contains exact build/full-suite/native/evidence commands. The [catalog](acceptance-scenarios.tsv) covers all 53 current SC statements, pins their digests and maps exact declared executable test methods or explicit planned/unexecuted states. Every row retains its specific full-criterion/evidence limits. [Requirements traceability](../specs/001-multibrand-redesign/requirements-traceability.md) still defines task ownership, not a blanket execution claim.

The qualified scenario report, inspected from the actual CI artifact, contains **51 observed-synthetic-subset entries and two planned-unexecuted entries (SC-008 and SC-040)**. Human participant evidence is not inferred from automated tests. Subscription retention is not substituted for activity retention. Mapped witnesses are only a subset of each full SC, not proof of every clause or a live deployment. The report always has fullSuccessCriteriaVerified=false and releaseReady=false. Observed suite totals are 129/333/110.

Catalog-only validation emits no observed evidence. Actual report generation requires complete passing TRX summaries, matching passed rows/definitions, all three suites and every mapped witness. Missing, failed, skipped, incomplete or duplicated execution evidence fails rather than generating a successful report. Five Python tests exercise parsing, missing suites/witnesses, failed/skipped/incomplete evidence and nonpromotion of synthetic/human/release states. The parser was additionally exercised on the first checkpoint's real TRX: unit/contract witnesses matched, and the failed integration suite was rejected. No financial, ownership, replay or data-statement guard was weakened.

## Combined managed and native acceptance

[AcceptanceSuite](../tests/Platform.IntegrationTests/AcceptanceSuite.cs) invokes the same [shared helper](../tests/Shared/AcceptanceQualification.cs) as AotSmoke. One owned disposable three-brand shop contains 50 independently funded memberships and one local stock item. Concurrent checkout yields one purchase/debit, 49 out-of-stock results and the exact remaining balance. Persisted prepare/edit leases expire and reconstructed services recover the same logical work, original private target and manifest generation. Known-result and checkout replay preserve the one debit/allocation.

The same shop receives one invoice-matched synthetic topup and 100 authenticated-provider-fixture settlement attempts: one settled result, 99 replays and one exact 10.25 USD posting to its original member. A later synthetic mixed order allocates three eligible local units and binds only its two-unit selected-supplier remainder. Unknown submit outcome releases no partial customer content; a reconstructed worker looks up the original intent once, never resubmits it, clears only the transient unknown blocker and prepares/delivers one complete five-unit manifest. No supplier/late/shortage compensation is invented. A foreign member cannot read the completed local manifest.

The inspected native aggregate report records **28 tables; 50 last-unit buyers; one successful last-unit purchase; 100 settlement attempts; one topup posting; two completed orders; six released units; one supplier submit; one lookup; verified restart identity/attempt recovery**. Final aggregate wallet balance is exactly 4950.25 USD from synthetic initial funding of 5000, a 10 USD local purchase, 10.25 USD topup and 50 USD mixed purchase. The report contains no customer/order/provider identifiers, credential content, message bodies, connection settings or keys, and has scope=synthetic-commerce-only and releaseReady=false.

This is service/session reconstruction against persisted state plus synthetic transport uncertainty, not an operating-system kill/restart, live provider maximum-charge capability, Telegram delivery, runtime routing/scheduling or production archive durability qualification.

## Correction and artifact digests

[CI 41412040475](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41412040475) passed the 571 existing .NET tests but the combined test failed opening its bounded replay pool: a data-source connection string had redacted password material. The pool now uses the disposable harness's protected control settings with its database explicitly pinned to the test-owned destination, matching the existing qualified replay fixture pattern. No secret was logged and no connection or financial guard was relaxed. The complete checkpoint above then passed in managed and native execution.

| Qualified artifact | SHA-256 |
| --- | --- |
| Redacted scenario witness report | `44036e839768e3b2b07737a93a4fdbf81d26453a1be934d669f18c74fe2df298` |
| Redacted combined acceptance report | `b3f00232aacc4ca0991a1b017a4bbf26aca104e05a106ee84bd2b7017ef7afce` |
| Api executable | `726984593e6b76071eb7946dae213079ef2aaf21f8f210e5caf62b1dfde1c207` |
| BotHost executable | `9cea26ee1c9fb67ae7a201ce4aef80024a1ed1f566674404f74a21e71df5b48b` |
| JobHost executable | `5b0fa7b43fe15d0bcc5632003c5869c65105c805ca673aa5aff0a17b01a4785c` |
| SchemaRunner executable | `782b2371c711dd31607875589165177834ce5e164b81a20bc1c9bc433c3f10df` |
| AotSmoke executable | `745de2c0c5b446097edce54f9e42534f71964446029fca1167e0d101dba8f7ba` |
| Generated administration contract, 29 methods | `7677e2439a9ead3a27b443f25891ce4f11ed70f20297aa6ead66cff3ea6fff46` |

Native executable hashes identify this exact source/run. Neither report asserts all SCs passed or release readiness. Migration wallet-projection evidence remains separately scoped and cannot close T066.

## Open gates

[Human acceptance protocol](USER_ACCEPTANCE_PROTOCOL.md) preregisters cohort/strata, first-attempt independence, the exact 95% integer criterion, no outcome-driven exclusions, uncertainty reporting and a NOT RUN template. **No participants or human outcomes were accessed or created. T079 remains unchecked until an actual reviewed result is published.** A passing future cohort alone cannot authorize release.

T075 actual data/secrets/routes/recipient security separation, T076 representative legacy performance and same-server resource/stop controls, T080 actual source-copy/staging rehearsal and T081 explicit release/manifests remain unexecuted/gated. T057 real provider capabilities and T063/T064/T066/T068 actual source/full import/full reconciliation/staging remain incomplete. T082 is the next available offline follow-up. The SC-040 activity-retention and other future follow-up limits remain explicit in the catalog; no unchecked feature is silently qualified by nearby probes.

No real credentials/source rows, live Telegram/payment/supplier/FX/AI services, staging/production mutation, migration/cutover or deployment. The independent AI runtime remains a separate future service; commerce has only its optional boundary. Normal hosts stay fail closed. The canonical typed/no-JSON 28-table SQL digest remains `872ba3f44d7ec2a7fc8c4cd0ffc1946d07ad1bf4045d0bdd93abcf111ca5d36c`. Reviewer checklist remains read-only at 92/126, digest `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`. Constitution 3.0.0 and approved business decisions remain active. Local .NET/PostgreSQL/Docker are unavailable; CI supplied execution proof. Spec Kit prerequisites and git diff --check passed; no extension hooks exist.
