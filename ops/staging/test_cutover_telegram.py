import base64,json,os,unittest
from unittest.mock import patch
import test_cutover_inbox as guards
inbox=guards.inbox
f=inbox.fence
p=f.pair
class TelegramTests(unittest.TestCase):
	def setUp(self):
		guards.InboxTests.setUp(self);path=self.root/"target/telegram";path.mkdir(mode=0o700);os.chown(path,10001,10001);p.save(self.root/"target/telegram-key.json",{"run":str(__import__('uuid').UUID(hex=self.m["run"])),"secret":"ab"*32});os.chown(self.root/"target/telegram-key.json",10001,10001);inbox.bind_telegram(self.root)
	def tearDown(self): self.tmp.cleanup()
	def envelope(self,**changes): return {"run":self.m["run"],"brand":"acc-abad","channel":"telegram","origin":self.c["origins"]["telegram"],"actor":"synthetic-member","event":"501","action":"telegram","body":'{"update_id":501,"message":{"text":"/menu"}}',"header":base64.urlsafe_b64encode(bytes.fromhex("ab"*32)).decode().rstrip("="),**changes}
	def capture(self,**changes): return inbox.capture(self.root,*guards.InboxTests.signed(self,self.envelope(**changes)))["event"]
	def test_wrong_webhook_secret_and_update_identity_are_not_captured(self):
		for changes in [{"header":"x"*43},{"event":"502"},{"body":'{"update_id":true}'},{"body":"x"*2049}]:
			with self.assertRaises((AssertionError,json.JSONDecodeError)): self.capture(**changes)
		self.assertEqual(inbox.inventory(self.root),{})
	def test_target_payload_is_private_and_complete_before_admission(self):
		event=self.capture();s=f.append(self.root,self.m,"active",owner="target",target_seen=True)
		def admitted(root,side,epoch,command,request):
			self.assertEqual((side,epoch,command,request),("target",s["epoch"],"telegram",event));self.assertEqual(f.owned(root,"target","telegram/"+event+".json"),inbox.telegram_payload(self.envelope()))
		with patch.object(f,"admit",side_effect=admitted): inbox.dispatch(self.root,event,"target",s["epoch"])
		self.assertEqual(inbox.reconcile(self.root,event)["phase"],"uncertain_do_not_retry")
	def test_legacy_and_changed_key_are_rejected_without_dispatch(self):
		event=self.capture();s=f.append(self.root,self.m,"active",owner="legacy")
		with self.assertRaises(AssertionError): inbox.dispatch(self.root,event,"legacy",s["epoch"])
		with self.assertRaises(AssertionError): f.admit(self.root,"legacy",s["epoch"],"telegram")
		path=self.root/"target/telegram-key.json";v=json.loads(path.read_text());v["secret"]="cd"*32;path.write_text(json.dumps(v))
		with self.assertRaises(AssertionError): self.capture()
		self.assertEqual(inbox.inventory(self.root),{"captured":1})
	def test_reconciliation_binds_the_actual_payload_and_signed_admission(self):
		event=self.capture();s=f.append(self.root,self.m,"active",owner="target",target_seen=True)
		with patch.object(f,"admit"): inbox.dispatch(self.root,event,"target",s["epoch"])
		p.save(self.root/"fence-requests"/(event+".json"),f.sign(self.root,{"side":"target","epoch":s["epoch"],"command":"telegram","request":event}));path=self.root/"target/results"/(event+".json");p.save(path,{"operation":"telegram","outcome":"Menu","synthetic":True,"releaseReady":False});os.chown(path,10001,10001)
		payload=self.root/"target/telegram"/(event+".json");v=json.loads(payload.read_text());v["body"]='{"update_id":502}';payload.write_text(json.dumps(v))
		with self.assertRaises(AssertionError): inbox.reconcile(self.root,event)
		payload.write_text(json.dumps(inbox.telegram_payload(self.envelope())));self.assertEqual(inbox.reconcile(self.root,event)["outcome"],"telegram:Menu")
if __name__=="__main__": unittest.main()
