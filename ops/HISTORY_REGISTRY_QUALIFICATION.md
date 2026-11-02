# Offline coupon history and activity registry qualification

T086–T087 qualified at `fbc5aad8653e1decf6bc9d75b47271938fd64123`, [CI 41464110500](https://github.com/Farrokhzadeh/telegram-multi-bot-shop/actions/runs/41464110500): **593 .NET tests (136 unit, 347 integration, 110 contract)**, **20 Python checks** and all five Linux Native AOT executables. Zero failed/skipped tests, zero build warnings/errors; formatting, secret scan, locked restore and Compose checks passed. Scope: actual typed coupon edit/redemption/history/rollback semantics and the version-1 finite activity registry, checked callback context, privacy/index/permission/retention design. T088/T089 instrumentation, authenticated history read, maintenance, replay/restore and protected legacy reconciliation remain separate unexecuted work.

Coupon tests use the real PricingStore/OperationsStore and complete-manifest redemption: an initial revision, 30 redeemed plus 10 held, unrestricted permitted owner override below used/held capacity, preserved accepted terms/old grants, serialized concurrent redemption/edit context and deferred commit failure with no audit/settings/history gap. Actual redeemed counters stay distinct from owner usage adjustments; ordinary redemption creates no edit revision. No finance or history constraint is relaxed.

Activity probes match all 11 canonical registry actions to the typed 14-column table, enforce membership-linked order/support context, prove scoped 100-replay dedupe, immutable/unexpired rows and unchanged non-activity domain tables. The checked order callback now derives its required product from that exact member's order; it does not invent or reveal a foreign product. The member/time index is added to the fresh canonical schema without changing the 28-table/no-JSON structure. Registry and privacy/permission/180-day design are fixed in contracts/bot-activity-v1.md.

No purge or full activity-history API is activated. Current router dedupe partly uses analytics rows; T088/T089 must first close independent replay safety without hidden raw archives or repurposed fields, then qualify target read/restore/cleanup at the original-time cutoff. SC-040 and human SC-008 stay planned/unexecuted. No actual source data or real keys, live services, staging/production mutation or deployment. Normal hosts fail closed; independent AI remains a separate future service. Constitution 3.0.0 and reviewer checklist read-only status 92/126 remain unchanged.

Local .NET/PostgreSQL/Docker remain unavailable; GitHub Actions supplied execution proof. The fresh SQL change is only the selected activity member/time index; existing staging/production schemas are not migrated. Earlier schema digests describe their qualified historical source snapshots.

The first checkpoint (41462463385) stopped at registry switch-arm whitespace; the next (41462984316) exposed a negative test expecting an internal exception instead of sanitized database InvalidData. Both were corrected and the full rerun passed without weakening replay/financial guards.

**76/96 tasks complete, 20 unchecked. T088 is next.** T088/T089 complete instrumentation, authenticated history, independent replay/restore and maintenance remain unimplemented/unqualified. Router dedupe still partly depends on analytics rows; no activity deletion is activated. SC-040 and SC-008 remain planned/unexecuted. T085, T057/T063/T064/T066/T068 and T075/T076/T079/T080/T081 remain incomplete/gated. Synthetic witnesses do not close those gates.

Canonical typed/no-JSON 28-table fresh SQL digest: `d350b45d8e3c9d41ee1bebdad12b59392a85ad655b28e40cfc0575b4a5177b94`. Previous hashes remain historical; no existing staging/production schema was migrated. Normal hosts fail closed; independent AI remains a separate future service. No real keys/source rows, live services, staging/production mutation, cutover or deployment. Local .NET/PostgreSQL/Docker remain unavailable; CI supplied execution. Spec Kit prerequisites, tooling/evidence checks and git diff --check passed; no extension hooks exist. Reviewer checklist bytes remain unchanged at 92/126; constitution 3.0.0 and approved business decisions remain authoritative. Continue authorized offline work directly on main with task-grouped commits and full checkpoint tests without routine confirmation.

| Executable | SHA-256 |
| --- | --- |
| Api | b592f9f14e3498079e7b3a343b41ac961557402764bb176b98c134a786cf1812 |
| BotHost | 5d742859ba006c92806f4455226a0b598050dd57ff070571210713079efebf6b |
| JobHost | ffc53e4654e88b5c1fc42721d3ee6c2423bc7070ee19b67c6975ed2e1f68d8e8 |
| SchemaRunner | b36726af31b0d2eda3f83d6dac8c59408fb48f2d8634322e89ce9a0666ea1655 |
| AotSmoke | 823ab78f9b8b35f7bc687c6153af4ff57fd0301d540fbdb78595a8516f713138 |

Generated administration contract remains 29 methods, digest `7677e2439a9ead3a27b443f25891ce4f11ed70f20297aa6ead66cff3ea6fff46`. Scenario witnesses remain partial with releaseReady=false. Reviewer checklist digest remains `165ad6199cf2f924d2868ef34659de82422a59aead17ec98e310932d416d10dc`.
