import hashlib
import json
import pathlib
import tempfile
import unittest
import uuid
from importlib import util

spec=util.spec_from_file_location("t064_review",pathlib.Path(__file__).resolve().parents[1]/"t064_review_register.py")
module=util.module_from_spec(spec)
spec.loader.exec_module(module)

class T064ReviewRegisterTests(unittest.TestCase):
	def setUp(self):
		self.temp=tempfile.TemporaryDirectory()
		self.addCleanup(self.temp.cleanup)
		self.root=pathlib.Path(self.temp.name)
		self.evidence={"brands":[],"totals":{"source_original_orders":5,"canonical_qualified_selected_orders":2,"protected_exception_orders":3}}
		for source,brand in module.SOURCES.items():
			d=self.root/("teleg-history-archive-"+source)
			d.mkdir(mode=0o700)
			plan=(source+"-binary-plan").encode()
			(d/"archive.plan").write_bytes(plan)
			(d/"archive.plan").chmod(0o600)
			count=3 if source=="prod1" else 1 if source=="prod3" else 1
			typed=1 if source!="prod2" else 0
			reasons=({"incomplete_order_terms":1,"missing_exact_settled_source_payment":1} if source=="prod1" else {"incomplete_order_terms":1} if source=="prod3" else {})
			rows=[{"projection":"OrderRecord","legacy_id":str(i),"target":str(uuid.uuid4()),"reason":reason} for i,reason in enumerate([reason for reason,n in reasons.items() for _ in range(n)])]
			review={"archive_manifest_sha256":hashlib.sha256(plan).hexdigest(),"typed_manifest_sha256":"a"*64,"original_records":5,"typed_orders":typed,"typed_payments":0,"native_zero_proofs":0,"inactive_master_catalog":0,"reviewed_records":len(rows),"review_reason_counts":dict(reasons),"review":rows}
			(d/module.REVIEW).write_text(json.dumps(review))
			(d/module.REVIEW).chmod(0o600)
			self.evidence["brands"].append({"source":source,"original_orders":count,"selected_typed_orders":typed,"held_for_review":count-typed,"missing_checkout_snapshot":1 if source in ("prod1","prod3") else 0,"original_wallet_irr_terms":0,"nonterminating_original_bank_unit":0,"settled_payment_predates_order":1 if source=="prod1" else 0})
		self.raw=json.dumps(self.evidence)
	def test_complete_private_review_accounting(self):
		result=module.verify(self.root,self.raw)
		self.assertTrue(result["matched"])
		self.assertEqual(3,result["totals"]["review_orders"])
		self.assertFalse(result["release_ready"])
	def test_duplicate_review_row_rejected(self):
		p=self.root/"teleg-history-archive-prod1"/module.REVIEW
		d=json.loads(p.read_text())
		d["review"].append(d["review"][0])
		d["reviewed_records"]+=1
		d["review_reason_counts"][d["review"][0]["reason"]]+=1
		p.write_text(json.dumps(d))
		with self.assertRaises(ValueError):module.verify(self.root,self.raw)
	def test_changed_archive_plan_rejected(self):
		p=self.root/"teleg-history-archive-prod1"/"archive.plan"
		p.write_bytes(b"changed-plan")
		with self.assertRaises(ValueError):module.verify(self.root,self.raw)
	def test_missing_original_order_rejected(self):
		p=self.root/"teleg-history-archive-prod1"/module.REVIEW
		d=json.loads(p.read_text())
		d["review"].pop()
		d["reviewed_records"]-=1
		d["review_reason_counts"]["missing_exact_settled_source_payment"]-=1
		p.write_text(json.dumps(d))
		with self.assertRaises(ValueError):module.verify(self.root,self.raw)
	def test_world_readable_review_rejected(self):
		p=self.root/"teleg-history-archive-prod1"/module.REVIEW
		p.chmod(0o644)
		with self.assertRaises(ValueError):module.verify(self.root,self.raw)
if __name__=="__main__":
	unittest.main()
