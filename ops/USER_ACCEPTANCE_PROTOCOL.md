# Catalog-to-checkout user acceptance protocol

## Owner-approved closeout — 2026-11-18

The owner accepted the [live Telegram wallet smoke](#live-telegram-wallet-smoke--2026-11-17) and the focused accabad-server code qualification, and waived the cohort run for the T079 closeout. The three focused cases passed, including checkout replay across restart. The live staging checkout produced one paid test order, one allocated unit and one test-wallet debit.

**T079 is complete for this scope.** SC-008’s 95% human completion rate remains unmeasured. The protocol below records the design for any future cohort run.

T079 defines the SC-008 measurement protocol and result template. The criterion is **at least 95% of preselected participants complete the basic catalog-to-checkout journey without staff assistance**. Automated tests, synthetic buyers and AI-assisted attempts are not human participant evidence. Current result: **NOT RUN**. No participant recruitment, Telegram messages, live payments or staging changes are authorized by this document. For a cohort run, publish the completed result. The release decision is recorded separately.

## Preregister the run

Before exposing the candidate to participants, freeze a run identifier, candidate source/executable digests, fixture image, approved isolated environment identity, brand/language/payment-route matrix, recruitment frame, pseudonymous participant roster, inclusion/exclusion rules, observation window, observer script and denominator. Record the approver and protocol revision. Do not use production customers, wallets, receiving cards, payment identities, stock or credentials in the rehearsal. Environment, security, migration and provider gates must permit the proposed test setup; a fail-closed host or unqualified transport is a blocker, not an invitation to bypass it.

Use a recommended fixed cohort of 60 eligible participants, 20 per brand, with 10 new and 10 returning shop users in each brand. Preselect participants from the declared eligible population before measuring outcomes; use a recorded reproducible random selection within those strata where a recruitment frame exists. Include the enabled customer languages, ordinary non-staff members, supported devices and each intended payment route in the frozen assignment matrix. Record recruitment bias and any unrepresented route/language/device. A convenience sample supports only that recruited cohort, not a claim about the whole customer population. Any different sample design must be fixed and justified before outcomes are observed.

Existing eligibility exclusions, consent declines and replacements may be applied only before a participant first sees the candidate, with aggregate reasons recorded. Staff, developers and observers are excluded from the primary cohort. Freeze the final denominator and replacement cutoff before the first attempt. No post-attempt exclusions, outcome-driven additions or optional stopping are allowed. Abandonment, no-show after enrollment, assistance and technical failures remain unsuccessful outcomes in the frozen cohort. An infrastructure failure may invalidate the run for release review; report it and start a separately identified preregistered run rather than silently shrinking the denominator.

The 60-person size is a protocol choice, not a new product requirement. A smaller authorized pilot may diagnose usability but cannot be presented as this completed acceptance run. Do not combine revisions or count repeated attempts by one person as additional participants.

## Operator worksheet

Copy this worksheet into the protected run record. Freeze the assignments before opening the candidate to participants.

| Complete before the run | Record |
| --- | --- |
| Candidate and fixture | Run ID, source commit, executable digest, fixture digest, isolated deployment and synthetic transport identities |
| Participant access | Verified test bot and allowlist for each assigned brand, supported routes and languages, staging authorization, private observer access |
| Cohort and assignments | Recruitment frame, consent and eligibility checks, roster digest, replacement cutoff, frozen N, and one brand, language, device and route per participant |
| Task cards | One synthetic product and quantity per assignment, exact quote and expected route endpoint in the observer record |
| Session settings | Observation window and its start event, accommodations, operator, observer and reviewer |
| Operator smoke check | Follow each enabled brand and route through the same customer surface in a disposable fixture. Confirm the endpoint and a fresh starting state before freezing the participant run. Record the checked candidate and fixture. |

Use the same neutral task from the protocol for every route. Give participants their assigned product, quantity and route in their assigned language. Keep the expected quote and endpoint in the observer record.

| Assigned route | What the observer must see |
| --- | --- |
| Wallet with synthetic balance | One accepted order with the correct brand, product, quantity and frozen price/currency. |
| Bank transfer with synthetic instructions | One unpaid order with the correct terms, test instructions and payment deadline. End the attempt before transfer or receipt upload. |
| Hosted crypto with a synthetic invoice | One unpaid order with the correct terms, test checkout instructions and invoice deadline. End the attempt before payment. |
| Zero-due checkout if enabled for the candidate | One accepted order showing zero due, with the correct brand, product and quantity. No external invoice. |

Freeze the route allocation across the three brands using only the intended enabled routes. Record omissions and the reason. Keep one first-attempt row per frozen participant in the protected record.

| Participant code | Brand | New or returning | Language | Device | Assigned route | Elapsed time | Primary outcome | Deviation or incident reference |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| One row per participant | | | | | | | | |

Use one outcome per row: independent success, assisted, wrong or duplicate checkout, abandoned, no-show, or technical failure. If several failure reasons apply, record one primary outcome and the other reasons as secondary observations. Reconcile the outcome counts to the frozen N before calculating the rate. In a 60-participant run, 57 independent successes and three unsuccessful attempts pass the numeric criterion. A run with 56 successes and four unsuccessful attempts fails it.

### Readiness checkpoint — 2026-11-17

Reviewed main at `a4c4ae8`. The retained `shop-staging` API, bot and jobs containers on accabad-server were stopped. The [synthetic staging host](../src/Platform.Api/SyntheticStaging.cs) exposes private qualification callbacks and fake delivery. The [BotHost entry point](../src/Platform.BotHost/Program.cs) exposes ingress qualification and private history modes. The reviewed entry points do not provide a participant-facing catalog-to-checkout session. The [T075 staging evidence](SECURITY_REVIEW.md#deployed-synthetic-staging-qualification--2026-11-15) remains useful for isolation and recipient controls.

Before enrolling participants, stage a reviewed candidate that exposes the customer catalog, quantity, quote and checkout in the test bot. Verify the operator smoke check and freeze the worksheet on the authorized isolated route with synthetic money and fulfillment inputs. The participant run and SC-008 result remain **NOT RUN**.

### Wallet backend pilot — 2026-11-17

An isolated synthetic stage actor now exposes catalog, wallet quote, and wallet confirmation through its private request files. `pilot-catalog` reads the staged product, `pilot-quote:<quantity>` records the displayed quote, and `pilot-buy:<quote-id>` accepts that quote through the brand-bound checkout service. Confirmation rechecks payable amount and currency. Replay returns the original order.

The accabad-server build used 418 verified baseline inputs with two staging/test changes. Locked restore used `linux-x64`. Formatting and the final build passed with zero warnings or errors. All three focused process cases passed. The new case read the catalog, rejected a missing quote, displayed the two-unit price, accepted one order, restarted the actor and replayed the same confirmation. The final fixture contained one order, two allocations and one wallet debit. TRX SHA-256 `8941285c3b0a841ae6803be264f53dbcd270a9cfbd630f0fbcf8db78569f9b84`. The temporary test actor and isolated PostgreSQL were stopped after the run.

The pilot currently uses operator request files. @testadbadbot still runs the legacy bot. The next step is to connect the staged checkout to an allowlisted private Telegram session and smoke-test that customer surface before enrolling participants. SC-008 remains **NOT RUN**.

### Private Telegram wallet pilot — 2026-11-17

The [pilot controller](staging/wallet_pilot.py) connects one private, allowlisted Telegram chat to the synthetic stage actor. It presents the catalog item, quantity choices, exact wallet quote and order confirmation. The [polling entry point](staging/wallet_pilot_telegram.py) verifies the dedicated staging bot and refuses a configured webhook. It takes the bot credential and actor binding on standard input from the protected operator process. It never needs production data or an external payment route.

An isolated accabad-server smoke connected the controller to the real staging checkout actor with simulated Telegram updates and responses. A wrong actor, group chat and stale message were rejected. A two-unit wallet checkout survived a lost confirmation response. The repeat showed the same order with one debit and two allocations. An operator then verified the dedicated staging bot and opened a bounded private pilot window with automatic restoration of the previous staging bot. This is one synthetic wallet route for one allowlisted tester. The full preregistered human cohort and SC-008 measurement remain **NOT RUN**.

### Live Telegram wallet smoke — 2026-11-17

The allowlisted staging tester completed one private Telegram wallet checkout through the pilot. The tester saw the confirmation, and the polling process recorded completion. A read-only query against isolated PostgreSQL verified one paid order, one allocated unit and exactly one 10 USD test-wallet debit. Earlier windows produced no orders. The staging tester binding was corrected before the successful window. The legacy staging bot was restored and the temporary pilot actors and isolated database were stopped.

This guided, one-participant wallet smoke is operational checkout evidence. The preregistered first-attempt cohort measurement remains **NOT RUN**.

## Observe one first attempt

1. Give the participant only this neutral task in their assigned supported language: “Find the assigned product, choose the assigned quantity, review the price and finish checkout using the assigned payment route.” Use synthetic products, balances, stock and configured fake transports only in a separately authorized isolated setup.
2. Start timing when the customer can first use their assigned brand bot. The observer records events without hints, navigation, links to the right menu, remote control or explanation of error messages. Accessibility accommodations and their effect on independence must be specified in advance. Do not introduce an invented latency pass target.
3. The participant navigates catalog, product and quantity, reviews the frozen quote and selects the intended route. Completion requires the correct brand/product/quantity and customer-visible original order identity with the expected frozen currency, price and route. An external-payment route stops at the valid unpaid checkout/instructions and deadline; do not make a real transfer. A synthetic wallet route must reach one accepted order. Record the exact endpoint for each route before the run. Payment settlement and full fulfillment are separate acceptance assertions.
4. Count success only if the participant reaches that route's endpoint on the first attempt within the preregistered observation window without staff or AI help. Asking for or receiving task-specific assistance, wrong-brand/wrong-product/wrong-quantity checkout, a duplicate order, abandonment or failure to reach the endpoint is unsuccessful. Staff may intervene for welfare or environment protection; count the affected attempt unsuccessful and report the incident.
5. After the measured attempt, collect optional usability comments separately. A supported retry can diagnose a defect but cannot replace the primary outcome. Do not store raw chats, personal identifiers, receipt data, credentials, account balances or payment/provider references in the repository or result artifact.

## Calculate and report

Let N be the frozen eligible cohort and S the number of independent first-attempt completions. The descriptive completion rate is S/N. SC-008 passes only when N is positive, all N outcomes are accounted for, protocol deviations are disclosed and **20 × S ≥ 19 × N**. For N=60, at least 57 successes are required. Report exact integer counts and fractions before rounded percentages; rounding must never turn a failure into a pass.

Report counts and completion rates by brand, new/returning membership, supported language, device and assigned payment route, including assistance, abandonment and technical-failure counts. The 95% criterion is the stated cohort-level target; subgroup counts expose gaps and are not invented additional thresholds. Report duration distributions only as measurements. Latency/CPU/RSS comparisons and targets require the representative legacy baseline under T076.

Include a 95% binomial confidence interval (Wilson score, z=1.959963984540054) as uncertainty context and state the sampling limitations. A cohort point estimate of 95% is not proof that at least 95% of all customers will succeed, and is not a 95%-confidence lower-bound claim. For reproducibility, with p=S/N, denominator d=1+z²/N, center=(p+z²/(2N))/d and half-width=z×sqrt(p×(1−p)/N+z²/(4N²))/d; report [max(0,center−half-width),min(1,center+half-width)]. Do not change the product acceptance criterion into a confidence-bound target without a separate approved decision.

An owner/operator reviews deviations, failures, subgroup coverage and all other release gates independently. A passing cohort metric alone never authorizes deployment or closes source/provider/staging/security gates. A failed or invalid run records remediation and a new preregistered candidate/cohort; preserve every earlier result.

## Result template — publish before release

| Field | Current value |
| --- | --- |
| Evidence state | NOT RUN — protocol only |
| Run/protocol revision and observation date | Not observed |
| Candidate source and executable digests | Not observed |
| Approved isolated environment / fixture identity | Not observed |
| Consent/recruitment frame, strata and roster digest | Not observed; identity mapping stays protected outside this repository |
| Frozen eligible denominator N | Not observed |
| Independent first-attempt successes S | Not observed |
| Assisted / abandoned / wrong checkout / technical failures | Not observed |
| Exact S/N and integer criterion 20S ≥ 19N | Not measured |
| Wilson interval and sampling limitations | Not measured |
| Per-brand/language/device/route/new-returning counts | Not observed |
| Duration distributions and representative baseline link | Not observed |
| Deviations, incidents and uncovered strata | Not observed |
| SC-008 result | UNEXECUTED — no human acceptance claim |
| Review decision and remaining release gates | Blocked pending evidence; no release authorization |

Publish aggregates and protected-evidence references only. No real participant data was accessed or created for T079. The automated synthetic acceptance report explicitly uses Scope=synthetic-commerce-only and ReleaseReady=false and must not populate this human result template.
