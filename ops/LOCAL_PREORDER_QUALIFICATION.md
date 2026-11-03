# Local-preorder qualification

## Authority and review

The owner's continuing offline implementation authorization covers T095. The prior blanket classification of all remaining work as evidence-gated was too broad: local FIFO code can proceed against the already approved canonical target. T093 supplier observation/account/cost proof remains required before real adapters. T096 historical partial-order compatibility remains required before related source import or DDL. Neither is asserted by this checkpoint; no schema change, source import or live integration occurs.

Constitution 3.0.0, FR-065/067/069/070/079, schema-contract transaction rule 4, the canonical 28 typed/no-JSON tables and complete-order release remain authoritative. The existing schema already enforces paid acceptance time, immutable priority, whole local allocation, held-head blocking and absence of preorder date fields. Admin stock import, accepted preorder payment and hold/resume/cancellation already enqueue typed shared-product work.

## T095 implementation

`LocalPreorderWorker` delegates to `LocalPreorderStore`. Each job considers the absolute oldest active paid/not-required local preorder across all brands, including held heads. It reads the candidate, locks its membership then the shared product, and rechecks the absolute head. If the candidate changed, it commits a durable continuation instead of locking another membership after the product. This preserves the existing member/product/brand/order lock order and prevents smaller-order bypass. A locked head waits or rolls back on lock timeout; business-head selection has no SKIP LOCKED. Scheduler lease claiming retains SKIP LOCKED.

The trusted registration must match the actual head's brand. Current inactive/bot-mismatched brands, blocked memberships, holds, reviews and preexisting fulfillment evidence prevent allocation; missing registration fails closed. Paid accepted local snapshots remain independent of current supplier selection. There is no supplier call, new purchase, wallet adjustment, paid expiry or invented waiting deadline.

Stock reading reuses one bounded checkout helper, also used by prepared checkout and external settlement. Stock is acquired only if the entire head quantity is available. Existing encrypted acquisition, order revision, one manifest job, bounded next drain job and lease completion commit atomically. Failure rolls all units/jobs/state back. Shortage or hold completes the current observation with zero allocation; transactional restock/payment/resume/cancellation events wake the queue. One order per job bounds transactions; continuations survive restart. Manifest preparation alone redeems the full held coupon and enables protected customer access through the existing delivery pipeline.

## Execution evidence

Source `76ee5820873a1e27b99cd8beb81826158d80bea6`, [CI 41536235392](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41536235392), passes **625 .NET tests: 136 unit, 378 integration, 111 contract**, **20 Python checks** and **five Linux Native AOT executables**, with zero test failures/skips and zero build warnings/errors. Locked restore, formatting, secret scan, Compose and evidence guards pass. The seven new integration cases exercise A-five/B-two across brands with 3/3/1 restock, zero partial allocation, whole coupon redemption/protected manifests, audited hold/resume/cancel, immutable acceptance time, locked-head timeout/retry, concurrent drains, stale completed lease denial, last-unit cipher failure/atomic rollback/retry and missing trusted brand denial.

The next test checkpoint adds an actual native FIFO execution probe, free coupon coverage, older unpaid bank/crypto quotes, settlement and bank approval replay, expired lease/restart, and cancellation/allocation races. These additions are not claimed as executed by the source run above; their exact final-main CI is recorded in the subsequent handoff.

## Remaining scope

T095 is complete; T096 remains unchecked. T096 still requires its full payment/allocation/hold/cancel/competing local-and-mixed matrix, full cancellation/coupon rollback and transport recovery matrix, and protected historical compatibility review. Existing tests in other suites are useful bounded evidence, not automatic completion of every T096 requirement. T089 still needs actual protected legacy activity reconciliation. SC-040 full retention and SC-008 human acceptance remain planned/unexecuted, and releaseReady=false remains.

78/96 complete, 18 unchecked: T057,T063,T064,T066,T068,T075,T076,T079,T080,T081,T085,T089,T090,T091,T092,T093,T094,T096. Their source/provider/environment/human requirements remain distinct from eligible offline implementation. No real credentials/source rows, live services, staging/production mutation, cutover or deployment. Normal hosts remain fail closed; independent AI remains a separate future service. Local .NET/PostgreSQL/Docker remain unavailable, so Actions supplies execution proof.

SQL SHA-256 remains `52a1a3967339adc58248507a94baa90b42bf5079e8c2572fdc3b01da3979fad6`. The reviewer checklist is unchanged at 92/126, SHA-256 `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`. No extension hooks exist; Spec Kit prerequisites and local Python checks pass. Continue task-grouped commits on main and verify exact latest-main CI before reporting completion.
