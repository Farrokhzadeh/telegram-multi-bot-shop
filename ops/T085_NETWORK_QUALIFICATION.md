# T085 synthetic network checks — 2026-11-16

Source base `de2ff0a2af21548f5315dd417706cf8c407d4259`; the two changed test files have Git blob IDs `c07b1d056b8ba17a9205c3cb65beee1ebe6e940c` and `f5fc25dc77a6e9f7a7bc8e68348143dc28717230`. Their server copies matched these IDs. All 409 source/test/build-configuration files checked against the base matched before the changes.

A network-isolated, CPU/memory-limited .NET SDK build passed with zero warnings and errors. The first attempt failed before compilation because restored package metadata expected `/cache/packages` and the cache was mounted elsewhere; the corrected isolated mount was used for the successful build. The failed attempt is retained privately.

Against a newly initialized isolated PostgreSQL fixture, focused VSTest execution passed **11/11, zero failed or skipped**. Three parameterized database constraint cases rejected TRX, BNB and USDTERC20 payment rows. Eight synthetic callback cases, including newly added BNB and USDTERC20 alongside the prior TRX, other-network, value and status cases, retained review without posting; a subsequent valid notice did not clear the review. This does not exercise actual provider invoice HTTP creation or provider-hosted identity, and is not the full T085 matrix.

| Artifact | SHA-256 |
| --- | --- |
| PaymentExpiryTests.cs | `a776e9f29679c35697d7f815a38f83ead3b8d17269068d64ca47b61152c9d724` |
| ExternalPaymentTests.cs | `5fe7cd02771dabea7f8b67ab0448f071881c2f06f2b6febeceabcf846494d55e` |
| Integration test DLL | `4e210b8f771ed77546d603f29a084397370b3cc00bcba13ec1db5b43f5201c81` |
| Private TRX | `b999f92051173455492014a27a583a1687666a0a473fa1d607b1e0d192424f39` |

Private execution evidence and failed attempt: `/var/lib/teleg-cli/t085-payment-20261116/`. The test and build containers exited without OOM; the owned isolated database was stopped. No live external traffic, deployment, production mutation or GitHub Actions was authorized. T085 remains unchecked; see [requirement inventory](T085_REQUIREMENT_INVENTORY.md).

## Current-build payment regression

The same integration DLL was copied to a second isolated synthetic PostgreSQL fixture and verified by SHA-256. VSTest ran the four selected classes and passed **68/68, zero failed or skipped**: 26 `PaymentExpiryTests`, 20 `ExternalPaymentTests`, 15 `PaymentSettlementTests` and seven `PaymentStateMachineTests`. This includes the 11 focused network cases above and must not be added to that count as independent scenarios. The run took 4 minutes 14 seconds; the test process exited zero without OOM and the owned database was stopped.

Private TRX SHA-256: `f9850cd695e9258e623dbde11c1763b2153b7c865d4ecf99caaf6c65352b0a31`. Private run directory: `/var/lib/teleg-cli/t085-regression-20261116/`. This is synthetic managed execution of those classes, not full T085, live invoice/merchant identity, an operating-system restart qualification or release readiness. Remaining matrix items in the inventory stay open.
