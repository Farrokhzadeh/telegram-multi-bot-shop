# Spec Kit 001 — line-numbered document index
1-based source lines; headings and Git blob IDs reflect the listed snapshots. Regenerate after any source edit. Use `rg -n` and `sed -n 'START,ENDp'` for relevant sections only. For history see [archived handoff](archive/CONVERSATION-HANDOFF-history.md).

## [../../.specify/memory/constitution.md](../../.specify/memory/constitution.md) · Git blob `9a0aa977d0d28690ed11321846518fecb3301a26`
1: # AccAbad Multi-Brand Commerce Constitution
3: ## Core Principles
5: ### I. Behavioral parity and customer safety (NON-NEGOTIABLE)
8: ### II. Preserve source data, identities, obligations, and encryption (NON-NEGOTIABLE)
30: ### III. Explicit shared-core boundaries and recoverability (NON-NEGOTIABLE)
57: ### IV. Reliable financial and background side effects (NON-NEGOTIABLE)
83: ### V. Security, tenant scope, and privacy
86: ### VI. Evidence-based implementation and efficiency
89: ### VII. Focused verification and traceability
92: ## Development and release gates
100: ## Amendment record
102: ### 3.1.0 — 2026-11-20
111: ### 3.0.0 — 2026-10-31
142: ### 2.0.0 — 2026-10-30
169: ### Approved risk assessment and controls
180: ## Governance

## [spec.md](spec.md) · Git blob `f80451991fb19ea69bb090d425499587816b21de`
1: # Akanti (آکانتی) Multi-Brand Commerce Specification
10: ## Clarifications
12: ### Session 2026-10-30
16: ## User Scenarios & Testing
18: ### User Story 1 — Independent branded customer experience (P1)
32: ### User Story 2 — Wallet purchase of the last stocked item (P1)
46: ### User Story 3 — Confirm external payment when stock is unavailable (P1)
60: ### User Story 4 — Centrally manage three independent brands (P1)
74: ### User Story 5 — Safe supplier fulfillment (P1)
87: ### User Story 6 — Migrate historical data safely, one brand at a time (P1)
102: ### User Story 7 — Independently operated AI support (P2)
108: ### User Story 8 — Expire unpaid external-payment orders after 30 minutes (P1)
121: ### User Story 9 — Paid local-preorder FIFO (P1)
134: ### Edge Cases
148: ## Requirements
150: ### Functional Requirements
152: #### Brand and customer isolation
161: #### Catalog and inventory
169: #### Money, checkout and fulfillment
183: #### Broadcasts and operations
190: #### Migration and release safety
273: ### Key Entities
295: ## Success Criteria
297: ### Measurable Outcomes
375: ## Assumptions
387: ## Resolved Clarifications and Release Gates
408: ## Scope Boundaries
414: ## Owner-approved retained-wallet continuation — 2026-11-06
417: ## Owner-approved supplier administrator review — 2026-11-18
423: ## Owner-approved documentation-only supplier qualification — 2026-11-18

## [plan.md](plan.md) · Git blob `61788d815e42901e016107cdeeab5d7114a42b03`
1: # Akanti (آکانتی) Multi-Brand Commerce Implementation Plan
9: ## Summary
15: ## Technical Context
34: ## Constitution Check
47: ## Staging environment — same production server
56: ## Project Structure
85: ## Design Decisions and Phases
87: ### Phase 0 — Inventory and proof (offline; no traffic)
97: ### Phase 1 — New schema, crypto and identity
108: ### Phase 2 — Atomic commerce core
121: ### Order credential delivery — owner clarification 2026-10-31
125: ### Phase 3 — Supplier, Telegram, human support, campaigns and admin APIs
137: ### Approved source selection and local preorder review (2026-10-29)
144: ### Phase 4 — Migration rehearsal and rollout (requires separate approval)
155: ## Contract / Decision Records
164: ## Deferred / Out of Scope
172: ## Complexity Tracking
176: ## Approved supplier product scope (2026-10-30)
184: ## Preorders have no configured availability date or fulfillment cap (owner correction 2026-10-30)
192: ## Paid-preorder cancellation requests use support (owner approval 2026-10-30)
198: ## Nonfinancial bot activity retention (owner approval 2026-10-30)
206: ## Supplier bonus items — admin disposition (owner approval 2026-10-30)
212: ## Fully coupon-covered supplier checkout — owner approved (2026-10-30)
218: ## Supplier cost increase guard — owner approval (2026-10-30)
224: ## Supplier listings stay visible as out of stock — owner correction (2026-10-30)
230: ## Opt-in one-time restock alerts — owner approval (2026-10-30)
236: ## Mandatory supplier refresh before new order creation — owner clarification (2026-10-30)
242: ## Remaining business clarifications approved (2026-10-30)
254: ## Owner-approved retained-wallet continuation — 2026-11-06

## [schema-contract.md](schema-contract.md) · Git blob `9a906b210c9a9bb7eed10565f654d13c5448101e`
1: # Canonical schema contract — 28-table forward design
12: ## Common types and ownership
24: ## Final table contracts
26: ### 1. brands
31: ### 2. brand_channels
34: ### 3. users
38: ### 4. admins
43: ### 5. admin_sessions
46: ### 6. products
51: ### 7. brand_products
55: ### 8. inventory_items
60: ### 9. wallets
63: ### 10. wallet_entries
69: ### 11. orders
77: ### 12. payments
83: ### 13. fulfillments
88: ### 14. supplier_requests
94: ### 15. jobs
98: ### 16. campaigns
102: ### 17. telegram_deliveries
109: ### 18. support_tickets
112: ### 19. support_messages
116: ### 20. legacy_id_map
120: ### 21. audit_events
124: ### 22. discounts
128: ### 23. discount_uses
132: ### 24. discount_revisions
135: ### 25. bot_activity_events
139: ### 26. supplier_request_attempts
143: ### 27. product_supplier_sources
149: ### 28. product_stock_subscriptions
154: ## Cross-table guards and transaction boundaries
168: ## Job-kind origin matrix
191: ## Financial and release state distinctions
200: ## Configuration, migrations and readiness
209: ### T088 verified quantity vocabulary review — 2026-11-02
214: ### T088 actual checkout-state vocabulary review — 2026-11-02
222: ## Registry version 5 — payment and topup state review, 2026-11-02
231: ## Target import and registry version 6 review — 2026-11-02
242: ## Final T088 policy and maintenance permission review — 2026-11-02
254: ## Owner-approved retained-wallet continuation — 2026-11-06
259: ## T089 protected legacy activity reconciliation — 2026-11-15
267: ## T090 canonical supplier-attempt qualification — 2026-11-15

## [contracts/ai-boundary.md](contracts/ai-boundary.md) · Git blob `60cd1b82b52974d504166856baf84207f6d28fe3`
1: # Optional independent AI boundary — version 1
5: ## Context read
18: ## Optional advisory turn

## [contracts/backend-contracts.md](contracts/backend-contracts.md) · Git blob `7a5e76a54c08fab955c376fe3a4b120f1927030e`
1: # Backend Contracts — v0 (No UI)
9: ## Common rules
19: ## Admin surface (private)
52: ## Internal commands and events
75: ## Owner-approved supplier attempt contract (2026-10-29; design only)
82: ## Approved source selection and local preorder contracts (2026-10-29; design only)
95: ## Ready-to-deliver supplier scope (owner approval 2026-10-30)
101: ## No preorder date configuration or paid time cap (owner correction 2026-10-30)
107: ## Paid-preorder cancellation via existing support (owner approval 2026-10-30)
115: ## Approved history contract (2026-10-29; no implementation authorized)
121: ## Bot activity retention contract (owner approval 2026-10-30)
127: ## External AI integration seam
133: ## Provider verification checkpoints
137: ## Pricing and coupon policy freeze
145: ## Approved compact wallet and Telegram customer transport
152: ## Thirty-minute unpaid-payment window
159: ## Immutable payment method per order (confirmed)
165: ## Approved wallet-funding and external checkout routes
174: ## Supplier bonus items — admin disposition (owner approval 2026-10-30)
180: ## Fully coupon-covered supplier checkout — owner approved (2026-10-30)
186: ## Supplier cost increase guard — owner approval (2026-10-30)
192: ## Supplier listings stay visible as out of stock — owner correction (2026-10-30)
198: ## Opt-in one-time restock alerts — owner approval (2026-10-30)
204: ## Mandatory supplier refresh before new order creation — owner clarification (2026-10-30)
208: ## Remaining business clarifications approved (2026-10-30)
220: ## Current command details selected by the consistency review
228: ## T039–T042 offline implementation checkpoint
237: ## T043–T044 offline administration configuration checkpoint
246: ## Phase 6 offline administration and notices checkpoint
256: ## T092 complete recovered supplier review approval

## [contracts/bot-activity-v1.md](contracts/bot-activity-v1.md) · Git blob `bf13b5cb865be52d31f6f37f6a320209fcae4477`
1: # Bot activity registry and retention design — versions 1–2
5: ## Stable vocabulary
29: ## Field allowlist and access
35: ## Selected index and privilege design
43: ## Exact 180-day UTC policy
51: ## T087 replay gate before executable retention (historical prerequisite)
59: ## T088 replay prerequisite — technical review, 2026-11-02
69: ## Qualified optional timeline extension
77: ## Registry version 2 — T088 workflow review, 2026-11-02
86: ## Registry version 3 — verified quantity ingress review, 2026-11-02
95: ## Ephemeral quote retirement and actual target backup review — 2026-11-02
104: ## Registry version 4 — actual checkout state review, 2026-11-02
121: ## Registry version 5 — payment and topup state review, 2026-11-02
130: ## Target import and registry version 6 review — 2026-11-02
141: ## Final T088 policy and maintenance permission review — 2026-11-02
149: ## T089 actual legacy preservation review — 2026-11-15

## [contracts/legacy-commerce-policy-matrix.md](contracts/legacy-commerce-policy-matrix.md) · Git blob `dfa65e6b46ea4496db6a4ab9194fd2905a3c38e7`
1: # Legacy commerce policy matrix
27: ## Synthetic calculation anchors
44: ## Pinned file identities

## [contracts/legacy-source-inventory.md](contracts/legacy-source-inventory.md) · Git blob `dfe3315a3faf2b7aa39456b114e0c9f4d1c712db`
1: # Legacy source inventory — offline baseline
7: ## Financial and identity model declarations
54: ## Source contract and risk map
67: ## Encryption representations and reader boundary
77: ## Telegram callback source evidence
83: ## Migration file inventory
155: ## Files read for this inventory
196: ## Supplier snapshot distinction
200: ## Remaining gates

## [contracts/legacy-supplier-attempt-mapping.md](contracts/legacy-supplier-attempt-mapping.md) · Git blob `bf2356f41236d828d19ccefb55478218cd0764cb`
1: # Legacy supplier history mapping — T090
5: ## Entity correspondence
16: ## Attempt fields and semantic restrictions
39: ## Frozen-copy qualification boundary

## [contracts/supplier-failure-policy.md](contracts/supplier-failure-policy.md) · Git blob `d2243debcc6c0701ed2e1187aae7983236c6defb`
1: # Supplier failure and reconciliation policy
3: ## Accepted obligations
7: ## Before purchase
11: ## Uncertain or partial result
19: ## Explicit decisions after confirmed failure
25: ## Qualification boundary

## [contracts/supplier-provider-matrix.md](contracts/supplier-provider-matrix.md) · Git blob `96f778871a802ff708bdda450e546f35f2f29e51`
1: # Supplier capability matrix and local-preorder reconciliation
6: ## Owner-approved behavior
15: ## First-release eligible product scope — owner approved (2026-10-30)
21: ## Source capability matrix
37: ## Owner-approved supplier-source relation — table 27 (final constraints pending)
68: ## Local preorder reconciliation
86: ## Before executable schema or implementation
93: ## Supplier bonus disposition — owner approved (2026-10-30)
99: ## Fully coupon-covered supplier checkout — owner approved (2026-10-30)
105: ## Supplier cost increase guard — owner approval (2026-10-30)
111: ## Supplier listings stay visible as out of stock — owner correction (2026-10-30)
117: ## Opt-in one-time restock alerts — owner approval (2026-10-30)
123: ## Mandatory supplier refresh before new order creation — owner clarification (2026-10-30)
127: ## Remaining business clarifications approved (2026-10-30)
139: ## Canonical persistence selected by consistency review
144: ## Phase 7 provider-neutral offline checkpoint (2026-11-01)
158: ## Owner-supplied documentation references — 2026-11-04
162: ## Current official contract review — 2026-11-04
176: ## Owner-approved option A — 2026-11-04
182: ## Owner-approved supplier administrator review — 2026-11-18
188: ## Documentation-only closeout — 2026-11-18
