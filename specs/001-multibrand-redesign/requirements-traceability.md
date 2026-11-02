# Requirements and acceptance traceability

**Reviewed baseline:** 2026-10-30 at 350a4d90ebfe6e4082d94820910806bb96c2484e. **Updated:** 2026-10-31 for owner-authorized six-finding remediation and saved-order-message recovery; read-only alignment reanalysis passed with E1 closed for governance only.
**Meaning:** this table retains planned task ownership for 84/84 functional requirements and 53/53 success criteria. It is not execution evidence or a current task-count ledger. Later offline implementation checkpoints in [tasks](tasks.md) supersede historical status notes. The [executable quickstart](quickstart.md), [digested scenario catalog](../../ops/acceptance-scenarios.tsv) and [qualification record](../../ops/ACCEPTANCE_QUALIFICATION.md) distinguish passing synthetic witness subsets from planned/unexecuted full-source, human, environment and release assertions; no blanket SC pass is claimed.

[Specification](spec.md), [tasks](tasks.md), [schema contract](schema-contract.md), [review and evidence gates](consistency-review.md).

## Functional requirements

| Requirement | Topic cue | Acceptance | Planned tasks |
|---|---|---|---|
| FR-001 | The platform MUST support at least three independently branded Telegram storefronts using… | SC-001, SC-006 | T001, T002, T003, T005, T019, T022 |
| FR-002 | All customer-accessible records and actions MUST be scoped to an identified brand;… | SC-001 | T010, T018, T023, T075 |
| FR-003 | The primary customer identity MUST be the pair of brand and Telegram… | SC-014 | T010, T019, T020, T028, T064 |
| FR-004 | Each brand MUST have independent bot configuration, language defaults, branding, support policy,… | SC-006, SC-050 | T019, T021, T044 |
| FR-005 | Bot secrets MUST be protected from ordinary administrative pages, reports, logs, exports… | SC-001, SC-019, SC-020, SC-051 | T011, T012, T014, T016, T044, T075 |
| FR-006 | All authorized administrators MUST be able to manage all three brands through… | SC-006 | T016, T043, T046, T048 |
| FR-007 | The new master catalog MUST be initialized from the designated first production… | SC-005 | T006, T063, T064, T066 |
| FR-008 | Each brand MUST be able to choose product visibility, price, currency, compatible… | SC-006, SC-012 | T028, T032, T036, T045 |
| FR-009 | Product presentation MUST support field-level inheritance, field-specific overrides and intentional empty optional… | SC-006 | T032, T036, T045 |
| FR-010 | Local stock MUST belong to a shared inventory pool, with exactly one… | SC-002, SC-004, SC-034, SC-047 | T027, T033, T035, T095, T096 |
| FR-011 | Purchases MUST retain an immutable snapshot of the brand-facing description/identity relevant to… | SC-012, SC-017, SC-020 | T028, T031, T033, T038, T085 |
| FR-012 | Every brand membership MAY have at most one USD-denominated customer wallet, identified… | SC-015 | T009, T028, T035, T069 |
| FR-013 | Every wallet movement MUST have a durable, auditable ledger entry and MUST… | SC-003, SC-004, SC-015 | T009, T033, T035, T039, T042, T069 |
| FR-014 | An immediate (non-preorder) wallet-funded local-stock checkout MUST atomically validate funds and stock,… | SC-002 | T027, T033, T035 |
| FR-015 | Customer-facing delivery MUST be recoverable independently of money movement, with duplicate-suppression across… | SC-004, SC-036 | T004, T015, T034, T049, T051, T052, T077 |
| FR-016 | Manual bank receipt submission MUST NOT hold stock. When staff confirms a… | SC-013, SC-020 | T040, T042, T085 |
| FR-017 | An accepted manual payment for which stock is unavailable MUST enter a… | SC-028, SC-034, SC-039 | T040, T046, T095, T096 |
| FR-018 | A confirmed crypto payment MUST be recorded exactly once even if stock… | SC-003, SC-019, SC-022 | T037, T039, T041, T042 |
| FR-019 | For an immediate local-only product_purchase order (not a supplier/mixed accepted procurement or… | SC-022, SC-023 | T037, T039, T042 |
| FR-020 | Supplier-fulfilled orders MUST separately track payment settlement, external request outcome and final… | SC-031, SC-032, SC-049 | T053, T055, T056, T058, T091 |
| FR-021 | Every new order MUST use globally unique orders.id as its sole public/admin/support… | SC-052 | T033, T078 |
| FR-022 | Payments, wallet credits, allocations and fulfillment status changes MUST be observable and… | SC-006, SC-049, SC-051 | T041, T046, T047, T060 |
| FR-023 | A central campaign MUST support a selected audience in one, multiple or… | SC-006, SC-010 | T049, T050, T051, T052 |
| FR-024 | Campaign sends MUST honor per-bot reachability, block state, subscription preferences and delivery… | SC-010 | T049, T050, T051, T052 |
| FR-025 | Human support and commerce MUST remain operational when the independent AI service… | SC-007, SC-009 | T025, T026, T071, T072, T073 |
| FR-026 | Background obligations, especially payment reconciliation, broadcasts, internal USD-wallet credits and credential deliveries,… | SC-004, SC-010, SC-032, SC-051 | T015, T051, T052, T077 |
| FR-027 | Historical customer identities, brand wallets, financial ledgers, orders, payment attempts, deposits, delivery… | SC-005, SC-024, SC-025 | T006, T013, T061, T063, T064, T065, T066 |
| FR-028 | Every migrated record that may collide across legacy databases MUST have stable… | SC-005 | T062, T064, T069 |
| FR-029 | Starting active inventory empty MUST be a deliberate, audited cutover decision; it… | SC-005 | T067, T068, T080 |
| FR-030 | Brands MUST cut over one at a time only after protected backup/restore… | SC-005 | T007, T067, T068, T080, T081 |
| FR-031 | Rollback MUST be tested before release. Any non-backward-compatible data change MUST have… | SC-005 | T068, T070, T080 |
| FR-032 | Each brand MUST support independent human-support tickets, customer/staff replies, pending/assigned/closed states, and… | SC-009, SC-039 | T024, T025, T026 |
| FR-033 | Confirmed supplier failure MUST retain payment/accepted entitlement and unfulfilled liability for an… | SC-031, SC-049 | T053, T056, T059, T060 |
| FR-034 | Before implementation of new catalog pricing, the operator MUST approve a retained-versus-removed… | SC-011, SC-012, SC-025, SC-026 | T006, T029, T036 |
| FR-035 | Historical wallet migration MUST establish an authoritative USD opening balance per (brand,telegram_id)… | SC-005, SC-015 | T064, T066, T069 |
| FR-036 | Customer-visible campaign execution MUST materialize eligible (campaign,brand,user_id) recipients, enforce per-bot throttles and… | SC-010 | T049, T050, T051, T052 |
| FR-037 | Every new order MUST contain exactly one product type and a positive… | SC-002, SC-031, SC-047 | T028, T033, T054, T064 |
| FR-038 | Discounts MUST be brand-scoped, automatically chosen for the customer using the existing… | SC-011, SC-025, SC-026 | T030, T031, T036 |
| FR-039 | Preserve the current Nobitex USDT/rial → toman conversion policy, including its hourly… | SC-012 | T029, T030, T036 |
| FR-040 | Every new order MUST store user_id, unit_price_usdt, currency, payment_method, nullable unit_price_toman, exchange_rate_toman… | SC-012, SC-013, SC-017, SC-020 | T028, T031, T038, T082, T085 |
| FR-041 | Newly created unpaid bank/crypto payment orders MUST have payment_due_at exactly 30 minutes… | SC-013 | T038, T082, T083, T084, T085 |
| FR-042 | Receipt submission/verified crypto transaction evidence recorded within the payment validity window MUST… | SC-013, SC-017, SC-027 | T039, T040, T042, T083, T085 |
| FR-043 | To keep orders compact, orders.user_id MAY replace both orders.brand_id and orders.telegram_id, while… | SC-014, SC-015 | T010, T019, T028, T082, T085 |
| FR-044 | Customer Telegram interactions, checkout, support messages, order updates and campaign deliveries MUST… | SC-016, SC-051 | T019, T020, T022, T049, T051, T085 |
| FR-045 | An order's payment_method and currency MUST be immutable from creation. A customer… | SC-017, SC-021 | T038, T083, T084, T085 |
| FR-046 | Customer-initiated wallet top-ups MUST use crypto through NOWPayments only; card-to-card MUST NOT… | SC-018 | T029, T038, T039, T085 |
| FR-047 | Standalone NOWPayments wallet-funding attempts MUST be represented without fictitious product orders. A… | SC-018, SC-019 | T038, T039, T042, T085 |
| FR-048 | All three brands MUST use one shared NOWPayments merchant account with one… | SC-019 | T037, T038, T039, T085 |
| FR-049 | Each Iranian bank/card-enabled brand MUST use its own distinct receiving bank card,… | SC-020 | T038, T040, T042, T044, T085 |
| FR-050 | Automatic discounts on bank/crypto orders MUST acquire a transactionally checked, temporary claim… | SC-021, SC-022, SC-023, SC-042 | T030, T033, T038, T042, T083, T085 |
| FR-051 | A brand membership MUST have at most one order with payment status… | SC-017, SC-021 | T028, T038, T083, T085 |
| FR-052 | Owner-approved option A for paid crypto without stock: when immediate local-only product_purchase… | SC-022 | T039, T042 |
| FR-053 | Owner-approved option A for restored discount expiration: releasing a held coupon use… | SC-023, SC-025, SC-028 | T030, T036, T042, T086 |
| FR-054 | Owner-approved option A for prepaid coupon ownership: newly issued discounts.kind='prepaid_entitlement' codes MUST… | SC-024 | T030, T036, T064 |
| FR-055 | Owner-approved option A for prepaid bulk-code issuance: a new prepaid order purchasing… | SC-025 | T030, T036, T064 |
| FR-056 | Owner-approved mandatory quantity behavior for all automatic percentage discounts: when an otherwise… | SC-026 | T031, T033, T036 |
| FR-057 | Owner-approved late crypto handling: a USDT payment verified as fully settled against… | SC-027 | T039, T042, T085 |
| FR-058 | Owner-directed off-bot administration and unrestricted coupon business edits: the bot MUST NOT… | SC-028, SC-029 | T043, T046, T086 |
| FR-059 | Owner-approved coupon edit history: issuing a coupon and each later owner edit… | SC-029 | T043, T046, T086 |
| FR-060 | Owner-approved meaningful bot activity collection: each brand bot MUST record customer start… | SC-030 | T087, T088, T089 |
| FR-061 | Owner-approved supplier batch cardinality: each external supplier_requests row MUST identify exactly one… | SC-031, SC-032, SC-047 | T054, T058, T090, T092 |
| FR-062 | Owner-approved typed supplier API attempt history: add supplier_request_attempts as table 26, persisting… | SC-032 | T054, T090, T091, T092 |
| FR-063 | Owner-corrected supplier shortfall behavior: when a paid five-unit supplier request yields only… | SC-031, SC-036 | T053, T058, T092 |
| FR-064 | Owner-approved multiple supplier sources with manual selection: one shared shop product MUST… | SC-033, SC-035 | T093, T094 |
| FR-065 | Owner-approved local-preorder FIFO with complete-order allocation: retain fully paid obligations when stock… | SC-034, SC-048 | T095, T096 |
| FR-066 | Owner-approved supplier binding at accepted payment and unavailable-source default: for an order… | SC-035, SC-042, SC-047 | T039, T054, T056, T093, T094 |
| FR-067 | Owner-corrected complete-order delivery, no intentional partial fulfillment: for new target orders, requesting… | SC-036, SC-047 | T034, T058, T092, T095, T096 |
| FR-068 | Owner-approved first-release supplier product scope: automated supplier procurement MUST support only ready-to-deliver… | SC-037 | T055, T057, T094 |
| FR-069 | Owner-corrected preorders with no configured availability date or fulfillment time cap: first-release… | SC-038 | T095, T096 |
| FR-070 | Owner-approved paid-preorder cancellation requests through support: customers requesting cancellation of a paid… | SC-039 | T025, T026, T046, T096 |
| FR-071 | Owner-approved 180-day nonfinancial bot activity retention: retain operational bot_activity_events for a rolling… | SC-040 | T087, T088, T089 |
| FR-072 | Owner-approved supplier bonuses go to administrators if actually supplied: If a supplier… | SC-041 | T053, T054, T056, T058 |
| FR-073 | Owner-approved automatic supplier procurement for valid 100%-off checkout: When a valid automatically… | SC-042, SC-047 | T033, T056, T058, T094 |
| FR-074 | Owner-approved 10% supplier cost-increase allowance: For supplier procurement after order acceptance, allow… | SC-043 | T054, T056, T057, T058, T094 |
| FR-075 | Owner-corrected visible out-of-stock supplier listings: Keep an otherwise active, brand-published supplier product… | SC-044, SC-047 | T021, T023, T032, T094 |
| FR-076 | Owner-approved opt-in one-time restock notifications: Offer explicit one-time restock opt-in for an… | SC-045 | T015, T021, T022, T023, T032, T049, T051, T052 |
| FR-077 | Owner-required fresh supplier product data before order creation: Before creating each new… | SC-046, SC-047 | T031, T033, T057, T094 |
| FR-078 | Owner-approved Local-first mixed fulfillment, complete-order release: Eligible orders may combine local stock,… | SC-047 | T028, T032, T033, T054, T056, T058, T094 |
| FR-079 | Owner-approved Admin pause blocks the paid local-preorder queue: An authenticated admin may… | SC-048 | T028, T046, T095, T096 |
| FR-080 | Owner-approved Supplier-failure admin alert, explicit retry or USD wallet credit: Confirmed supplier… | SC-049 | T046, T054, T056, T058, T059, T060 |
| FR-081 | Owner-approved Verified staff channel exemption and per-brand enforcement bypass: Support a configurable… | SC-050 | T016, T021, T023, T044, T075 |
| FR-082 | Owner-approved Per-bot public support contact and private shared staff-chat notices: Configure one… | SC-051 | T016, T021, T023, T046, T049, T051, T052, T075 |
| FR-083 | Owner-approved Order ID is the sole new public order reference: Use the… | SC-052 | T028, T033, T078, T085 |
| FR-084 | Owner-approved Closed restock subscription records retained 180 days: Retain completed/cancelled product_stock_subscriptions for… | SC-053 | T049, T051, T052 |

## Success criteria and verification owners

| Criterion | Topic cue | Planned test/protocol tasks |
|---|---|---|
| SC-001 | In the cross-brand isolation acceptance suite, zero foreign-brand customer records, media, support… | T018, T023, T075 |
| SC-002 | In 50 simultaneous wallet purchase attempts for one local stock item, exactly… | T027, T035 |
| SC-003 | Replaying the same paid provider event 100 times results in exactly one… | T037, T042 |
| SC-004 | Repeated interruption and restart tests result in zero lost recorded delivery/payment obligations… | T017, T034, T035, T052, T077 |
| SC-005 | Every production cutover brand achieves 100% reconciliation of recorded opening balances and… | T061, T066, T068, T069, T080 |
| SC-006 | An authenticated administrator can configure a product for three brands, inspect all… | T043, T045, T046, T048 |
| SC-007 | Existing purchase, staff support and payment confirmation journeys remain available when AI… | T026, T071, T073 |
| SC-008 | At least 95% of preselected user acceptance participants complete a basic catalog-to-checkout… | T079 |
| SC-009 | With AI disabled, a customer can open a human-support ticket and receive… | T026, T071 |
| SC-010 | A three-brand campaign produces one recipient record per eligible membership, survives interruption/restart,… | T052 |
| SC-011 | If one eligible 100% discount has 10 remaining product-unit uses, requesting 11… | T036 |
| SC-012 | Automatic toman quotes reproduce the legacy Nobitex/5,000-toman ceiling rule and fail closed… | T036 |
| SC-013 | Every unpaid external-payment order displays a 30-minute deadline; timeout changes order state… | T085 |
| SC-014 | Brand-scoped user_id lookup routes to the correct bot/Telegram ID, and attempts to… | T017, T018, T085 |
| SC-015 | wallets has PK user_id and no currency column; wallet_entries has no currency… | T069, T085 |
| SC-016 | Every outbound customer notification uses its membership's users.telegram_id for the private chat… | T023, T085 |
| SC-017 | Attempting to switch an existing unpaid order's payment method is rejected with… | T085 |
| SC-018 | A standalone NOWPayments wallet-funding attempt has no orders row, is tied to… | T037, T042, T085 |
| SC-019 | With one shared NOWPayments account, concurrently created crypto invoices for all three… | T037, T085 |
| SC-020 | Each Iranian brand shows a distinct configured receiving card for a new… | T042, T085 |
| SC-021 | With one remaining coupon use, two customers concurrently creating externally paid orders… | T036, T042, T085 |
| SC-022 | A $10 product discounted 50% and paid with $5 USDT but unavailable… | T042 |
| SC-023 | A 50%-off campaign expires at midnight. If a valid 11:55 PM order… | T042 |
| SC-024 | Issue a prepaid 100%-off future-unit code to Brand A membership user_id=A1, linked… | T036, T064 |
| SC-025 | A verified paid advance purchase of 100 future units issues one prepaid… | T036, T064 |
| SC-026 | With 10 eligible remaining coupon uses, customer entering quantity 11 using an… | T036 |
| SC-027 | A 100%-verified on-network USDT quote arrives after an order has expired or… | T042, T085 |
| SC-028 | A confirmed-paid bank order awaiting restock remains unchanged while staff perform off-bot… | T043, T086 |
| SC-029 | Issue a coupon, redeem 30 units and hold 10; allow the owner… | T086 |
| SC-030 | In each of three brand bots, record meaningful start/menu/product/button/quantity/checkout, real abandonment and… | T089 |
| SC-031 | Request five supplier units and receive three verified encrypted contents. Persist exactly… | T058, T092 |
| SC-032 | A submit call times out after the provider may have spent money,… | T091, T092 |
| SC-033 | Configure one shop product with Akunding and RoboticVN sources, including separate RoboticVN… | T094 |
| SC-034 | At zero stock pay preorder A for five, then B for two… | T096 |
| SC-035 | Create an unpaid supplier order while source A is active, switch the… | T094 |
| SC-036 | Hold five coupon uses on a paid five-unit order. With only three… | T034, T092, T096 |
| SC-037 | Accept verified ready-account/key/code mappings for the exact provider product/variant. Reject a ProdSeller-style… | T057, T094 |
| SC-038 | Create, publish, quote and fully pay a local preorder without any availability/ETA/deadline/duration… | T096 |
| SC-039 | A customer opens an existing support ticket about their paid queued preorder,… | T026, T096 |
| SC-040 | At a fixed trusted UTC time, create activity events just newer than,… | T089 |
| SC-041 | Simulate a five-unit supplier purchase returning five verified customer units and one… | T053, T058 |
| SC-042 | With a valid 100%-off coupon covering five supplier units and a zero-balance… | T058, T094 |
| SC-043 | Freeze a verified same-source supplier-cost baseline of 10.00 at acceptance. Automatic procurement… | T057, T058, T094 |
| SC-044 | With no eligible local coverage, publish a supplier product in all three… | T023, T032, T094 |
| SC-045 | A customer explicitly opts into restock alerts for product P in brand… | T023, T052 |
| SC-046 | Make cached supplier stock/price stale, then create a new wallet, bank, crypto… | T057, T094 |
| SC-047 | Order five identical units with three eligible local units and two from… | T058, T094 |
| SC-048 | Pause the oldest paid local preorder while enough stock exists and newer… | T096 |
| SC-049 | Confirmed supplier failure creates one logical staff case notice. Repeated admin retry… | T058, T060 |
| SC-050 | Verified active staff bypass join checks in each brand without joining its… | T017, T023, T075 |
| SC-051 | Each brand shows only its configured public admin contact. Blocked cases/bonuses create… | T023, T052, T075 |
| SC-052 | A new order has one globally unique ID and no separately stored… | T078, T085 |
| SC-053 | At a fixed trusted UTC time, test closed subscriptions just newer than,… | T052 |

## Architecture and evidence controls

T002, T003, T004, T005, T007, T008, T009, T011, T012, T013, T014, T016, T017, T074, T075, T076, T077, T080, T081 additionally implement/qualify the plan's host boundaries, explicit 28-table baseline, migration runner, exact money, key separation, redaction, Native AOT publish/run, security review and separate release gates. They are not evidence of a completed runtime.

T079 is the SC-008 human acceptance sampling/protocol owner; this is not a fabricated automated test result. T006/T029/T063–070 own source/provider compatibility and retain source-only versus protected-deployment evidence labels.

## Gaps and prerequisites

No task is unaccounted for across functional, acceptance or architecture/evidence ownership.

The latest review determines E1 constitutional alignment; E2–E6 remain unqualified at their documented stages. A row in this matrix is a mapping, not test success, schema ratification, provider verification or permission to execute the task. Validate each test's exact assertions against the full criterion rather than only this abbreviated cue.
