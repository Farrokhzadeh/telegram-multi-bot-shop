import importlib.util,json,os,tempfile,unittest,uuid
from pathlib import Path
from unittest.mock import patch
import cutover_fence as fence
pair=fence.pair
class FenceTests(unittest.TestCase):
	def setUp(self):
		self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.m={"run":"1"*32,"containers":{}}
		pair.save(self.root/"pair.private.json",self.m);(self.root/"fence-key.private").write_text("2"*64);(self.root/"fence-key.private").chmod(0o600)
		bindings={}
		for side,uid in [("legacy",1000),("target",10001)]:
			(self.root/side).mkdir(mode=0o700)
			for d in ["requests","results","step1-requests"]: (self.root/side/d).mkdir(mode=0o700)
			bindings[side]={"product":1 if side=="legacy" else str(uuid.uuid4()),"unit":1 if side=="legacy" else str(uuid.uuid4()),"cipher":"3"*64,"plain":"4"*64}
			pair.save(self.root/side/"stock-binding.json",bindings[side]);os.chown(self.root/side/"stock-binding.json",uid,uid)
		pair.save(self.root/"fence-header.private.json",{"version":1,"run":self.m["run"],"manifest":fence.digest(self.m),"plain":"4"*64,"bindings":bindings})
		(self.root/"fence-ledger").mkdir(mode=0o700);(self.root/"fence-requests").mkdir(mode=0o700)
		self.snapshot={s:{"rows":1,"used":0,"valid":True} for s in fence.SIDES}
		fence.append(self.root,self.m,"frozen",stock=self.snapshot,initial=True)
	def tearDown(self): self.tmp.cleanup()
	def test_stale_epoch_and_foreign_owner_do_not_publish(self):
		s=fence.append(self.root,self.m,"active",owner="legacy")
		with self.assertRaises(AssertionError): fence.admit(self.root,"legacy",s["epoch"]-1,"wallet")
		with self.assertRaises(AssertionError): fence.admit(self.root,"target",s["epoch"],"wallet")
		self.assertEqual(list((self.root/"fence-requests").iterdir()),[])
	def test_ledger_counts_cannot_replace_fresh_stock_audit(self):
		snapshot={s:dict(v) for s,v in self.snapshot.items()};snapshot["legacy"]["used"]=1
		with patch.object(pair,"validate"),patch.object(fence,"audit",return_value=snapshot),self.assertRaises(AssertionError):
			fence.grant(self.root,"target",0)
		self.assertEqual(fence.chain(self.root,self.m)[-1]["phase"],"frozen")
	def test_old_head_and_changed_signed_stock_are_rejected(self):
		old=(self.root/"authority.private.json").read_text();fence.append(self.root,self.m,"granting",intent="target",target_seen=True)
		(self.root/"authority.private.json").write_text(old)
		with self.assertRaises(AssertionError): fence.chain(self.root,self.m)
		f=self.root/"fence-ledger/00000001.json";x=json.loads(f.read_text());x["target_seen"]=False;f.write_text(json.dumps(x))
		with self.assertRaises(AssertionError): fence.chain(self.root,self.m,head=False)
	def test_recovery_rechecks_stock_and_never_resumes_an_owner(self):
		fence.append(self.root,self.m,"granting",intent="target",target_seen=True)
		pair.save(self.root/"authority.private.json",{"epoch":0,"hash":pair.digest(self.root/"fence-ledger/00000000.json")})
		with patch.object(pair,"stop") as stopped,patch.object(fence,"audit",return_value=self.snapshot) as audited:
			s=fence.recover(self.root);stopped.assert_called_once();audited.assert_called_once()
		self.assertEqual((s["phase"],s["owner"],s["target_seen"]),("frozen",None,True))
		with self.assertRaises(AssertionError): fence.grant(self.root,"legacy",s["epoch"])
	def test_start_permit_is_bound_and_single_use(self):
		s=fence.append(self.root,self.m,"granting",intent="legacy")
		pair.save(self.root/"start-permit.private.json",fence.sign(self.root,{"epoch":s["epoch"],"side":"legacy","manifest":fence.digest(self.m),"run":self.m["run"]}))
		with self.assertRaises(AssertionError): fence.consume_start(self.root,self.m,"target")
		fence.consume_start(self.root,self.m,"legacy")
		with self.assertRaises(FileNotFoundError): fence.consume_start(self.root,self.m,"legacy")
	def test_plain_launcher_start_and_request_are_blocked(self):
		with patch.object(pair,"call") as docker:
			with self.assertRaises(Exception): pair.start(self.root,self.m,"target")
			with self.assertRaises(ValueError): pair.request(self.root,"legacy","wallet")
			docker.assert_not_called()
	def test_unknown_queue_inputs_are_retained_without_reading_them(self):
		f=self.root/"legacy/requests"/(uuid.uuid4().hex+".request");os.mkfifo(f,0o600)
		fence.quarantine(self.root)
		self.assertFalse(f.exists());self.assertEqual(len(list((self.root/"fence-quarantine").iterdir())),1)
	def test_manifest_or_replica_binding_drift_rejects_journal(self):
		f=self.root/"legacy/stock-binding.json";x=json.loads(f.read_text());x["cipher"]="5"*64;f.write_text(json.dumps(x))
		with self.assertRaises(AssertionError): fence.chain(self.root,self.m)
	def test_target_worker_commands_reject_legacy_before_lock_or_publication(self):
		for command in fence.TARGET_COMMANDS:
			with self.subTest(command=command),patch.object(fence,"lock") as locked,self.assertRaises(AssertionError): fence.admit(self.root,"legacy",0,command)
			locked.assert_not_called()
		self.assertEqual(list((self.root/"fence-requests").iterdir()),[])
	def test_target_worker_commands_use_normal_epoch_admission(self):
		for command in fence.TARGET_COMMANDS:
			with self.subTest(command=command),patch.object(fence,"lock",side_effect=RuntimeError("normal admission")) as locked,self.assertRaisesRegex(RuntimeError,"normal admission"): fence.admit(self.root,"target",0,command)
			locked.assert_called_once_with(self.root)
if __name__=="__main__": unittest.main()
