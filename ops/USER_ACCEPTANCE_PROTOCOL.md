# Catalog-to-checkout user acceptance protocol

T079 defines the SC-008 measurement protocol and result template. The criterion is **at least 95% of preselected participants complete the basic catalog-to-checkout journey without staff assistance**. Automated tests, synthetic buyers and AI-assisted attempts are not human participant evidence. Current result: **NOT RUN**. No participant recruitment, Telegram messages, live payments or staging changes are authorized by this document. Publish a completed result and obtain the required release decision before release.

## Preregister the run

Before exposing the candidate to participants, freeze a run identifier, candidate source/executable digests, fixture image, approved isolated environment identity, brand/language/payment-route matrix, recruitment frame, pseudonymous participant roster, inclusion/exclusion rules, observation window, observer script and denominator. Record the approver and protocol revision. Do not use production customers, wallets, receiving cards, payment identities, stock or credentials in the rehearsal. Environment, security, migration and provider gates must permit the proposed test setup; a fail-closed host or unqualified transport is a blocker, not an invitation to bypass it.

Use a recommended fixed cohort of 60 eligible participants, 20 per brand, with 10 new and 10 returning shop users in each brand. Preselect participants from the declared eligible population before measuring outcomes; use a recorded reproducible random selection within those strata where a recruitment frame exists. Include the enabled customer languages, ordinary non-staff members, supported devices and each intended payment route in the frozen assignment matrix. Record recruitment bias and any unrepresented route/language/device. A convenience sample supports only that recruited cohort, not a claim about the whole customer population. Any different sample design must be fixed and justified before outcomes are observed.

Existing eligibility exclusions, consent declines and replacements may be applied only before a participant first sees the candidate, with aggregate reasons recorded. Staff, developers and observers are excluded from the primary cohort. Freeze the final denominator and replacement cutoff before the first attempt. No post-attempt exclusions, outcome-driven additions or optional stopping are allowed. Abandonment, no-show after enrollment, assistance and technical failures remain unsuccessful outcomes in the frozen cohort. An infrastructure failure may invalidate the run for release review; report it and start a separately identified preregistered run rather than silently shrinking the denominator.

The 60-person size is a protocol choice, not a new product requirement. A smaller authorized pilot may diagnose usability but cannot be presented as this completed acceptance run. Do not combine revisions or count repeated attempts by one person as additional participants.

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
