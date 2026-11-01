# T031–T032 immutable quotes and catalog qualification

2026-11-01. Offline continuation on main under the owner's instruction to do the next tasks.

Qualification: **PASS** — 351 tests (129 unit, 163 integration, 59 contract), zero failed/skipped tests, zero build warnings/errors, clean formatting, secret scan, Compose render and five Linux Native AOT executables. Source `50e44b2fa31696e891a160d3dda89b4d50839eed`; [CI 41291975522](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41291975522). T031–T032 are complete. Local git diff --check passed.

## Scope

CheckoutQuote freezes typed unit prices, FX source/fetched-at, calculation version, rounding step, payment/currency, product text and selected coupon revision/percentage/whole quantity. Totals are computed from those inputs. Automatic selection retains source-equivalent price ordering and ordinal code ties. A best eligible coupon with insufficient remaining quantity returns quantity_exceeds_discount_limit and its maximum, including zero after an overcommitted owner cap. There is no partial discount, customer opt-out, downgrade to a weaker code or fallback to full price. Independently priced USD checkout tolerates unavailable FX; bank pricing requires current FX and manual toman prices remain independent.

CatalogStore reads current membership, product and brand listing state under compatible locks. Text inherit/override/clear, manual publication, scoped prices/payment flags, selected shared source and paid FIFO stock protection determine the projection. Published supplier outages remain visible as Out of stock. Local-first complete local coverage skips supplier fetching; mixed supply fetches only the remote remainder. New supplier quotes directly refresh stock/cost/ready-item evidence through an injected port outside all database transactions. Trusted account configuration is shared per provider. Source/account/configuration changes reject the observation before write. Fresh observation updates do not change retail prices, publication, accepted orders or source bindings.

PreparedCheckout is a short-lived, single-use opaque acceptance proof tied to its store, exact brand context, actor/member, quantity, immutable quote, revisions and supplier observation. RequireCurrentAsync rechecks current state, capacity, supply and freshness inside the caller's transaction before any later hold/debit/invoice/zero-due acceptance. A public quote is not acceptance authority and reserves no stock or coupon units. T033 must invoke this gate as part of the complete dispatcher; that task remains partial and unchecked.

Prepaid lookup/display and automatic selection preserve the original paid source member/product even if an owner edits the current assigned membership. Compatible user/product/brand lock ordering prevents new inverse paths with coupon issuance and local checkout. Restock consent saves durable evaluation work, and verified availability recovery coalesces pending evaluation work without suppressing an evaluation already running. This closes the check-to-consent lost-wake window without reserving supply, publishing products or sending notices. Per-consent evaluation and sender behavior remain subsequent T049/T051 work.

## Evidence and limits

Synthetic tests cover 10/50/100 percent whole-quantity caps across wallet/crypto/bank, original-member prepaid isolation, deterministic coupon ties, frozen arithmetic, manual/stale-FX independence, per-member limits, hidden and published outage views, local-first remainder requests, cost/retail separation, source switches during transport, current-cap races, foreign/changed/replayed proofs, paid FIFO protection and restock evaluation wakeups. Tests assert no unintended orders, financial entries, coupon claims, inventory allocation, supplier bindings or delivery effects. Native smoke exercises automatic encrypted-code selection and a local-first remote refresh/acceptance gate with a synthetic supplier port.

The initial CI 41291350093 found a C# local-name collision in RefreshAsync; CI 41291644757 then passed build/format and 129 unit tests but exposed a coupon-query maximumRows argument above the existing DbSession limit. Both implementation errors were corrected without weakening the transaction helper. Subsequent review corrected payable asset typing and added assertions for USD wallet credit, USDT crypto and toman bank amounts. Coupon queries retain the existing 1,000-row fail-closed bound rather than silently truncating candidates.

No HTTP supplier adapter or runtime activation was added. Normal hosts remain fail closed. T033 complete order dispatch, T034 credential delivery and T035/T036 full checkout qualification remain unchecked. No schema change, real credentials/data, live Telegram/payment/supplier/FX calls, staging/production mutation or deployment occurred. The 28-table baseline SQL SHA-256 remains fa57b9c12b395a7a47bed8fc92586dac66ea652baf1e84ca0e8ebb727e3cc27d. Reviewer checklists remain unchanged. Local .NET/PostgreSQL/Docker are unavailable; execution evidence comes from the identified CI run. Local git diff --check is required before completion.

## Native executable SHA-256

| Executable | SHA-256 |
|---|---|
| Api | ed9e37abe1c771076e95213e10acbefbb4707d926d623aa41857fb0df0c65241 |
| BotHost | 86ccd1d3cb0f4c81b5e774c8dda7eb274cedc66ae182ac93c45d006a9958615b |
| JobHost | 1dc98990599c5a8445681e5744d4d4e4bdc72d6fedff80a118a422cb021d3013 |
| SchemaRunner | 23384b4006717640c56695d5bf7b0af07393ce2f6bd2b3bfee6adb1f7b19fde0 |
| AotSmoke | 17ea5a37beb617f970949c459525991d17c2805beaaa9dc8e38f25481a3b577f |

The subsequent completion commit changes documentation only; executable evidence is bound to the source/run above.
