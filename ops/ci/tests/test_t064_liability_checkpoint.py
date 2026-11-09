import copy
import importlib.util
import json
import pathlib
import unittest

root=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location("t064_checkpoint",root/"ci/t064_liability_checkpoint.py")
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

def fixture():
	read=lambda name:json.loads((root/"evidence"/name).read_text())
	orders=read(module.FILES["orders"])
	coupons=read(module.FILES["coupons"])
	delivery=read(module.FILES["delivery"])
	wallet=read(module.FILES["wallet"])
	review={"matched":True,"release_ready":False,"totals":{"original_orders":4916,"typed_orders":919,"review_orders":3997},"sources":{x["source"]:{"original_orders":x["original_orders"],"typed_orders":x["selected_typed_orders"],"review_orders":x["held_for_review"]} for x in orders["brands"]}}
	history={"matched":True,"release_ready":False,"total_members":12282}
	media={"prod1":(3210,288,491,2),"prod2":(0,0,0,0),"prod3":(691,0,45,1)}
	assets={"release_ready":False,"instances":{s:{"inspection_complete":True,"release_ready":False,"source_authentication":"not_exercised" if s=="prod2" else "passed","receipt_files":{"verified_hash":v,"missing":m,"no_local_reference":r},"product_media_references_unverified":u} for s,(v,m,r,u) in media.items()}}
	return {"orders":orders,"coupons":coupons,"delivery":delivery,"wallet":wallet,"review":review,"history":history,"assets":assets,"asset_date":"2026-11-05"}

class LiabilityCheckpointTests(unittest.TestCase):
	def setUp(self):
		self.data=fixture()
	def test_complete_evidence_keeps_release_blocked(self):
		r=module.verify(**self.data)
		self.assertEqual(3997,r["totals"]["protected_order_exceptions"])
		self.assertEqual(40,r["totals"]["irr_liability_accounts"])
		self.assertEqual(21,r["totals"]["unassigned_coupon_records"])
		self.assertEqual(288,r["totals"]["missing_receipts"])
		self.assertFalse(r["release_ready"])
	def test_order_disposition_drift_rejected(self):
		self.data["orders"]["brands"][0]["original_wallet_irr_terms"]+=1
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_missing_history_evidence_rejected(self):
		self.data["history"]["total_members"]-=1
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_prepaid_ownership_drift_rejected(self):
		self.data["coupons"]["totals"]["unassigned_owner"]-=1
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_delivery_receipt_claim_rejected(self):
		self.data["delivery"]["limits"]["source_sent_marker_is_delivery_acknowledgement"]=True
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_irr_balance_drift_rejected(self):
		self.data["wallet"]["sources"][0]["preserved_nonzero_irr_balances"]-=1
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_missing_media_drift_rejected(self):
		self.data["assets"]["instances"]["prod1"]["receipt_files"]["missing"]-=1
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_future_asset_report_cannot_be_assumed_equivalent(self):
		self.data["asset_date"]="2026-11-09"
		with self.assertRaises(ValueError):module.verify(**self.data)
	def test_unsafe_release_flags_rejected(self):
		self.data["orders"]["qualification"]["release_ready"]=True
		with self.assertRaises(ValueError):module.verify(**self.data)

if __name__=="__main__":
	unittest.main()
