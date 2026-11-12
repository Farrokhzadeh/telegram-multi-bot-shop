import concurrent.futures,hashlib,hmac,json,os,signal,subprocess,sys,unittest
from unittest.mock import patch
import cutover_inbox as inbox
import test_cutover_fence as guards
f=inbox.fence
p=f.pair
class InboxTests(unittest.TestCase):
	def setUp(self):
		guards.FenceTests.setUp(self);self.c=inbox.install(self.root)
	def tearDown(self): self.tmp.cleanup()
	def envelope(self,event="1",channel="telegram",**changes): return {"run":self.c["run"],"brand":self.c["brand"],"channel":channel,"origin":self.c["origins"][channel],"actor":self.c["actor"],"event":event,"action":"wallet" if channel=="telegram" else "review",**changes}
	def signed(self,v):
		body=f.canonical(v);return body,hmac.new(bytes.fromhex(p.private(self.root/"inbox-ingress.private").strip()),body,hashlib.sha256).hexdigest()
	def capture(self,**changes): return inbox.capture(self.root,*self.signed(self.envelope(**changes)))["event"]
	def active(self): return f.append(self.root,self.m,"active",owner="target",target_seen=True)["epoch"]
	def test_empty_inbox_and_reinstall_are_fail_closed(self):
		self.assertEqual(inbox.inventory(self.root),{})
		with self.assertRaises(AssertionError): inbox.install(self.root)
	def test_authentication_and_every_binding_precede_capture(self):
		body,signature=self.signed(self.envelope())
		for data,mac in [(body,"0"*64),(body+b" ",signature),(body,signature+" "),(b"x"*4097,signature)]:
			with self.assertRaises(AssertionError): inbox.capture(self.root,data,mac)
		for changes in [{"run":"a"*32},{"brand":"foreign"},{"origin":"foreign"},{"actor":"foreign"},{"action":"review"},{"event":"01"},{"event":"9223372036854775808"}]:
			with self.assertRaises(AssertionError): self.capture(**changes)
		self.assertEqual(inbox.inventory(self.root),{})
	def test_duplicates_serialize_and_changed_payload_is_not_acknowledged(self):
		body,signature=self.signed(self.envelope())
		with concurrent.futures.ThreadPoolExecutor(4) as pool: results=list(pool.map(lambda _:inbox.capture(self.root,body,signature),range(4)))
		self.assertEqual(sum(not x["duplicate"] for x in results),1)
		changed=json.dumps(self.envelope()).encode();mac=hmac.new(bytes.fromhex(p.private(self.root/"inbox-ingress.private").strip()),changed,hashlib.sha256).hexdigest()
		with self.assertRaises(AssertionError): inbox.capture(self.root,changed,mac)
		self.assertEqual(inbox.inventory(self.root),{"captured":1})
	def test_ambiguous_json_and_unsafe_file_inputs_are_rejected(self):
		with self.assertRaises(AssertionError): inbox.decode(b'{"x":"1","x":"2"}')
		event=self.capture();path=self.root/"inbox-events"/event/"00000000.json";path.unlink();os.mkfifo(path,0o600)
		with self.assertRaises(ValueError): inbox.read(self.root,event)
	def test_freeze_window_capture_does_not_admit_stale_or_foreign_owner(self):
		event=self.capture()
		with patch.object(f,"admit") as admit:
			with self.assertRaises(AssertionError): inbox.dispatch(self.root,event,"target",0)
			epoch=self.active()
			for side,e in [("legacy",epoch),("target",epoch-1)]:
				with self.assertRaises(AssertionError): inbox.dispatch(self.root,event,side,e)
			admit.assert_not_called()
		self.assertEqual(inbox.inventory(self.root),{"captured":1})
	def test_payment_is_held_without_inferred_financial_effect(self):
		event=self.capture(channel="payment")
		with patch.object(f,"admit") as admit:
			self.assertEqual(inbox.dispatch(self.root,event,None,None)["phase"],"held");admit.assert_not_called()
		self.assertEqual(inbox.reconcile(self.root,event)["phase"],"held")
	def test_interruption_before_admission_never_blindly_retries(self):
		event=self.capture();epoch=self.active()
		with patch.object(f,"admit",side_effect=RuntimeError):
			with self.assertRaises(RuntimeError): inbox.dispatch(self.root,event,"target",epoch)
		self.assertEqual(inbox.reconcile(self.root,event)["phase"],"uncertain_do_not_retry")
		with self.assertRaises(AssertionError): inbox.dispatch(self.root,event,"target",epoch)
	def test_real_process_sigkill_retains_intent(self):
		event=self.capture();epoch=self.active();code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_inbox as i\nwith patch.object(i.fence,'admit',side_effect=lambda *a:os.kill(os.getpid(),signal.SIGKILL)): i.dispatch(Path(sys.argv[1]),sys.argv[2],'target',int(sys.argv[3]))\n"
		r=subprocess.run([sys.executable,"-c",code,str(self.root),event,str(epoch)],env={**os.environ,"PYTHONPATH":str(__import__('pathlib').Path(inbox.__file__).parent)},capture_output=True,timeout=10)
		self.assertEqual(r.returncode,-signal.SIGKILL);self.assertEqual(inbox.reconcile(self.root,event)["phase"],"uncertain_do_not_retry")
	def test_sigkill_before_capture_acknowledgement_allows_only_duplicate(self):
		body,signature=self.signed(self.envelope());code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_inbox as i\nroot=Path(sys.argv[1]);sync=i.fence.sync\ndef crash(path):\n if path==root/'inbox-events': os.kill(os.getpid(),signal.SIGKILL)\n sync(path)\nwith patch.object(i.fence,'sync',side_effect=crash): i.capture(root,sys.argv[2].encode(),sys.argv[3])\n"
		r=subprocess.run([sys.executable,"-c",code,str(self.root),body.decode(),signature],env={**os.environ,"PYTHONPATH":str(__import__('pathlib').Path(inbox.__file__).parent)},capture_output=True,timeout=10)
		self.assertEqual(r.returncode,-signal.SIGKILL);self.assertTrue(inbox.capture(self.root,body,signature)["duplicate"]);self.assertEqual(inbox.inventory(self.root),{"captured":1})
	def test_signed_admission_and_owned_result_are_required(self):
		event=self.capture();epoch=self.active()
		with patch.object(f,"admit"): inbox.dispatch(self.root,event,"target",epoch)
		path=self.root/"target/results"/(event+".json");p.save(path,{"operation":"wallet","outcome":"Purchased","synthetic":True,"releaseReady":False});os.chown(path,10001,10001)
		with self.assertRaises(FileNotFoundError): inbox.reconcile(self.root,event)
		p.save(self.root/"fence-requests"/(event+".json"),f.sign(self.root,{"epoch":epoch,"side":"target","command":"wallet","request":event}))
		self.assertEqual(inbox.reconcile(self.root,event)["phase"],"result_observed");self.assertEqual(inbox.reconcile(self.root,event)["phase"],"result_observed")
	def test_journal_tamper_and_empty_partial_capture_fail_closed(self):
		event=self.capture();path=self.root/"inbox-events"/event/"00000000.json";v=json.loads(path.read_text());v["envelope"]["action"]="review";p.save(path,v)
		with self.assertRaises(AssertionError): inbox.read(self.root,event)
		(self.root/"inbox-events"/"pending-retained").mkdir(mode=0o700)
		with self.assertRaises(AssertionError): inbox.inventory(self.root)
if __name__=="__main__": unittest.main()
