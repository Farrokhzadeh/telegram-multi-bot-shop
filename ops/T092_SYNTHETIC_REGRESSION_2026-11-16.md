# T092 synthetic partial-acquisition regression — 2026-11-16

The isolated managed integration build described in [T085 network qualification](T085_NETWORK_QUALIFICATION.md) was reused: integration DLL SHA-256 `4e210b8f771ed77546d603f29a084397370b3cc00bcba13ec1db5b43f5201c81`. Its source/test/build-configuration inputs matched main before the two recorded T085 test-only modifications. The copied DLL matched the original hash.

A new isolated PostgreSQL fixture and network-isolated test container ran five cases, **5 passed, 0 failed, 0 skipped**:

- `NativeCompatiblePartialStatusExportRecoveryAndWholeAuditedRelease`: one synthetic submission yields three acquired units; status and export reconstruction retain the five-unit coupon hold and prevent manifest access. Complete recovery, audited approval, five ready units, whole redemption and foreign-brand denial are checked.
- `TimeoutAfterSuccessAndCrashAfterIntentNeverResubmitOriginalPurchase`: both parameterized timeout and crash-intent variants passed.
- `PartialAcquisitionRetainsCouponAndMoneyAndCannotPrepareCustomerManifest` and `ReorderedRecoveryDeduplicatesPartialUnitsAndBonusesWithoutClearingProtectedPartialReview` passed.

TRX SHA-256: `1fe814dd772c2fd82f24ad74799f80be6df7368be0a016764bbe556d9acdadc5`. Private execution evidence: `/var/lib/teleg-cli/t092-synthetic-20261116/`. The test container exited zero without OOM and the owned database was stopped.

These tests use synthetic providers and service reconstruction. They do not establish a fresh operating-system process restart, live provider status/export/callback behavior, actual historical partial-delivery compatibility or full intended-environment qualification. See [supplier qualification](SUPPLIER_QUALIFICATION.md#t092-complete-recovered-partial-review--qualified-2026-11-03). T092 remains unchecked. No live external calls, deployment, production mutation or GitHub Actions were performed.
