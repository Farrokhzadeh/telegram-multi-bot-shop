import json
import unittest
from pathlib import Path

EVIDENCE = Path(__file__).resolve().parents[2] / "evidence"


def load(name):
    return json.loads((EVIDENCE / name).read_text(encoding="utf-8"))


class T064HistoricalPreservationEvidenceTests(unittest.TestCase):
    def test_qualified_and_reviewed_original_orders_cover_all_history(self):
        history = load("legacy-customer-history-validation-2026-11-09.json")
        exceptions = load("legacy-order-payment-exception-disposition-2026-11-09.json")
        canonical = load("legacy-canonical-shared-import-2026-11-08.json")
        original = {s["source"]: s for s in history["sources"]}
        review = {s["source"]: s for s in exceptions["brands"]}
        qualified = {s["name"]: s for s in canonical["execution"]["brands"]}
        brands = {"prod1": "acc-abad", "prod2": "kuala", "prod3": "zikotech"}
        self.assertEqual(set(brands), set(original) & set(review))
        self.assertEqual(set(brands.values()), set(qualified))
        for source, brand in brands.items():
            with self.subTest(source=source):
                a, b, c = original[source], review[source], qualified[brand]
                self.assertEqual(a["original_orders"], b["original_orders"])
                self.assertEqual(a["memberships"], c["memberships"])
                self.assertEqual(b["selected_typed_orders"], c["orders"])
                self.assertEqual(b["held_for_review"] + c["orders"], a["original_orders"])
                self.assertEqual(b["held_for_review"],
                                 b["missing_checkout_snapshot"] + b["original_wallet_irr_terms"] +
                                 b["nonterminating_original_bank_unit"] + b["settled_payment_predates_order"])
                self.assertEqual(a["private_history_files"], a["memberships"])
                self.assertTrue(a["fresh_process_replay_identical"])
        totals = exceptions["totals"]
        self.assertEqual(sum(v["original_orders"] for v in original.values()), totals["source_original_orders"])
        self.assertEqual(sum(v["held_for_review"] for v in review.values()), totals["protected_exception_orders"])
        self.assertEqual(totals["source_original_orders"], history["totals"]["original_orders"])
        self.assertEqual(totals["canonical_qualified_selected_orders"], canonical["shared_target"]["historical_orders"])
        self.assertEqual(totals["protected_exception_orders"], history["totals"]["protected_read_only_exception_orders"])
        self.assertEqual(totals["canonical_qualified_selected_orders"] + totals["protected_exception_orders"], totals["source_original_orders"])
        self.assertEqual(totals["original_delivery_sent_markers_total"],
                         totals["typed_selected_orders_with_original_sent_marker"] + totals["held_exception_orders_with_original_sent_marker"])
        self.assertEqual(sum(v["memberships"] for v in original.values()), history["totals"]["memberships"])
        self.assertEqual(history["totals"]["private_member_encrypted_files"], history["totals"]["memberships"])

    def test_quarantine_and_release_gates_cannot_be_mistaken_for_success(self):
        history = load("legacy-customer-history-validation-2026-11-09.json")
        exceptions = load("legacy-order-payment-exception-disposition-2026-11-09.json")
        canonical = load("legacy-canonical-shared-import-2026-11-08.json")
        staging = load("legacy-staging-history-qualification-2026-11-09.json")
        self.assertTrue(exceptions["preservation"]["all_3997_cases_locatable_in_owner_private_original_archive_review_set"])
        self.assertFalse(exceptions["qualification"]["original_paid_obligations_or_owner_decisions_resolved"])
        self.assertFalse(exceptions["preservation"]["historical_sent_markers_treated_as_receipt_proof"])
        self.assertFalse(exceptions["preservation"]["original_irr_converted_to_usd"])
        self.assertFalse(exceptions["preservation"]["unknown_fulfillment_or_money_liabilities_waived"])
        self.assertEqual(canonical["remaining_gates"]["unassigned_coupon_original_records"], 21)
        self.assertEqual(canonical["remaining_gates"]["non_usd_irr_liability_accounts_preserved"], 40)
        self.assertEqual(staging["history_source"], "synthetic_brand_member_encrypted_archive_only")
        self.assertFalse(staging["production_data_used"])
        for result in (exceptions["qualification"], history["remaining"], canonical["remaining_gates"]):
            self.assertFalse(result.get("release_ready"))
        self.assertFalse(staging["release_ready"])
        self.assertFalse(exceptions["qualification"]["t064_complete"])


if __name__ == "__main__":
    unittest.main()
