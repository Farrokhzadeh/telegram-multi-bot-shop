# T033 acceptance progress and T078 order references

2026-11-01. Offline implementation continues on main under the owner's instruction to proceed when the existing checks are good. Both the T031/T032 executable run and its documentation-only successor passed before this batch began.

Qualification: **PASS** — 366 tests (129 unit, 178 integration, 59 contract), zero failed/skipped tests, zero build warnings/errors, clean formatting, secret scan, Compose render and five Linux Native AOT executables. Source `3ea93788e717038be2b4eb8399a06ac3de2c95e3`; [CI 41308978678](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41308978678). T078 is complete. T033 remains unchecked; this is a defined acceptance slice, not a completed dispatcher or runtime activation. Local git diff --check passed.

## Implemented acceptance slice

WalletCheckoutService connects the existing opaque channel-access and prepared catalog proofs to a single transaction. Transport checks and direct supplier catalog reads finish outside that transaction. The locked acceptance path revalidates membership, current policy, publication, prices, selected coupon capacity, local/FIFO supply and exact source observation before writes. Positive crypto/bank paths return Unavailable; they cannot debit or fabricate payment. A valid 100-percent coupon may accept without wallet money or an external payment, subject to its requested method's current eligibility.

The accepted product_purchase transaction commits immutable quote inputs and product text, whole coupon hold, exact USD wallet debit when due, brand-encrypted local acquisitions and durable work together. Full local coverage requires no supplier access, including for supplier products. Mixed acceptance binds only a positive selected-source remainder with immutable account/product/variant/revision/cost/quote/time and a typed submission intent. No supplier purchase occurs here. Cost does not reprice retail. Local units remain internal while the remote obligation is pending; coupon units remain held for complete-manifest release. Paid local preorders allocate the whole quantity or wait behind older paid FIFO obligations, with drain work and no invented ETA. Manual goods retain an administrator-review obligation.

User/product/brand/coupon/wallet/order locks and request-ID advisory serialization protect the transaction. Public IDs are UUIDv7 from OrderIds.New; no second public reference is stored. A customer audit commitment binds brand, membership, product, quantity and requested payment method to the order ID. Matching replay returns the original result before any new channel/supplier access, price check or source binding. Foreign, changed-input, audit-only and historical collisions fail closed. A creation race that loses freshness/capacity rechecks whether the same logical request committed before reporting failure. The old prerequisite local-wallet path reuses the same encrypted acquisition helper.

A successful new acceptance may cancel an unobserved awaiting-payment order, release its complete held claim without changing coupon expiry and cancel pending/retry expiry work. Review/confirmation states or recorded payment/receipt evidence block automatic replacement. Balance/supply/quote rejection occurs before replacing the old order. All exceptions abort active acceptance work; a decryption failure after writing the hold and debit rolls everything back.

## Remaining T033 work

T033 is deliberately unchecked. The complete order_kind dispatcher, prepaid source-sale settlement/automatic one-code issuance, positive external-payment order creation and accepted-payment dispatch remain pending, together with full channel/pending-replacement integration for those paths. A new quote currently counts an old pending order's held coupon units; replacing that order with quantity requiring those same restored units is conservatively rejected before mutation. Complete-manifest authorization and coupon redemption belong to T034; this batch does not weaken the canonical release constraints to redeem claims early. T035/T036 broader dispatcher crash, lost-COMMIT, 50-buyer and regional/prepaid redemption coverage remains pending. Existing T027 focused local-wallet race evidence is retained, not relabeled as complete dispatcher coverage.

The approved baseline currently requires created_by_admin_id on coupons and an admin coupon_issue audit for initial discount revisions. Automatic paid-source issuance must reconcile that existing authority model explicitly; this batch does not forge an administrator identity, create an extra table or bypass the guard. Actual external-payment evidence and settlement remain T037 onward. Normal hosts remain fail closed.

## Validation scope

Synthetic/disposable tests exercise 10/50/100-percent atomic local acceptance, actionable excess quantity, zero-wallet free mixed acceptance, frozen remote cost and remainder binding, no-stock/source errors before mutation, local-only outage bypass, paid preorder FIFO, channel denial/revocation, foreign/changed replay, unobserved pending replacement, preserved review/insufficient-balance obligations, cipher-failure rollback and two competing free claims for one unit. T078 stresses 100,000 concurrent UUIDv7 IDs and an existing order-ID collision without new debit, claim or allocation. Native smoke executes quoted mixed wallet acceptance, the full held coupon, source binding and original-result replay with synthetic ports.

Local .NET/PostgreSQL/Docker are unavailable. Managed/native execution is supplied by the identified GitHub Actions run. Local git diff --check is required. No canonical schema change, real data/keys, live provider/Telegram/payment/FX call, staging/production mutation or deployment occurred. Reviewer checklists remain read-only.

## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | bb4ce021e84e1cb20dfeaa94909d3530a828dd10d9839595160d8ce454be77db |
| BotHost | bf2f5d73104ba19a65ebf5f6e6a584e84992a6d631a7020ab43714efcbd96cd2 |
| JobHost | 907bdd55d9e97a4c2245ca3a5920b8ca97e246159b47a809acf10447b0cbd3d7 |
| SchemaRunner | a693550874237947d2d23167fba1de33ed575dfce44e729bd18b461b55cf4e69 |
| AotSmoke | 11aec72f2d58250ab4ff2d4df6cfe9eb637329bf3c4e15e43bbe6945a90660e0 |

The subsequent checkpoint commit changes documentation only; executable evidence is bound to the source/run above.
