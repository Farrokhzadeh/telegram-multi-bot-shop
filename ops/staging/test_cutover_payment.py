import hashlib,json,os,unittest,uuid
from unittest.mock import patch
import cutover_payment as payment
import test_cutover_inbox as guards
inbox=guards.inbox
f=payment.f
p=payment.p
class PaymentTests(unittest.TestCase):
	def setUp(self):
		guards.InboxTests.setUp(self);member=str(uuid.uuid4());invoice=str(uuid.uuid4());p.save(self.root/"target/state.json",{"user":member});os.chown(self.root/"target/state.json",10001,10001)
		p.save(self.root/"target/payment-key.json",{"run":str(uuid.UUID(hex=self.m["run"])),"member":member,"payment":invoice,"origin":"topup:"+invoice,"provider":"123456789","secret":"ab"*32});os.chown(self.root/"target/payment-key.json",10001,10001)
		for name in ["payment","payment-archives","payment-verification"]:
			path=self.root/"target"/name;path.mkdir(mode=0o700);os.chown(path,10001,10001)
		payment.bind(self.root);self.nonce=uuid.uuid4().hex
	def tearDown(self): self.tmp.cleanup()
	def envelope(self,**changes): return {"run":self.m["run"],"brand":"acc-abad","channel":"payment","origin":self.c["origins"]["payment"],"actor":"synthetic-member","event":hashlib.sha256(bytes.fromhex("ab"*64)).hexdigest(),"action":"payment","body":'{"payment_id":123456789}',"signature":"ab"*64,**changes}
	def capture(self,**changes):
		with patch.object(payment,"verify",return_value={"nonce":self.nonce,"history":self.nonce,"result_sha256":"1"*64,"execution_sha256":"2"*64}): return inbox.capture(self.root,*guards.InboxTests.signed(self,self.envelope(**changes)))
	def test_signature_identity_and_channel_precede_gateway_execution(self):
		for changes in [{"event":"1"*64},{"channel":"telegram"},{"signature":"x"*128}]:
			with patch.object(payment,"verify") as verified,self.assertRaises(AssertionError): inbox.capture(self.root,*guards.InboxTests.signed(self,self.envelope(**changes)))
			verified.assert_not_called()
		with patch.object(payment,"verify",side_effect=AssertionError),self.assertRaises(AssertionError): inbox.capture(self.root,*guards.InboxTests.signed(self,self.envelope()))
		self.assertEqual(inbox.inventory(self.root),{})
	def test_canonical_duplicate_keeps_first_and_preserves_raw_variant(self):
		first=self.capture();self.assertFalse(first["duplicate"]);self.assertTrue(self.capture()["duplicate"]);changed=self.capture(body='\n{"payment_id":123456789}\n');self.assertEqual(changed,{"event":first["event"],"duplicate":True});self.assertEqual(inbox.read(self.root,first["event"])[0]["envelope"],self.envelope());self.assertEqual(len(list((self.root/"payment-duplicates"/first["event"]).iterdir())),1)
	def test_target_payload_and_observed_hold_bind_real_admission(self):
		event=self.capture()["event"];s=f.append(self.root,self.m,"active",owner="target",target_seen=True)
		def admitted(root,side,epoch,command,request): self.assertEqual((side,command,request),("target","payment",event));self.assertEqual(f.owned(root,"target","payment/"+event+".json"),payment.payload(self.envelope(),self.nonce))
		with patch.object(f,"admit",side_effect=admitted): inbox.dispatch(self.root,event,"target",s["epoch"])
		p.save(self.root/"fence-requests"/(event+".json"),f.sign(self.root,{"epoch":s["epoch"],"side":"target","command":"payment","request":event}));path=self.root/"target/results"/(event+".json");p.save(path,{"operation":"payment","outcome":"Held","synthetic":True,"releaseReady":False});os.chown(path,10001,10001)
		self.assertEqual(inbox.reconcile(self.root,event)["phase"],"held");self.assertEqual(inbox.inventory(self.root),{"held":1});self.assertEqual(inbox.read(self.root,event)[-1]["reason"],"authentic_unbound_payment")
	def test_legacy_and_changed_invoice_key_remain_fenced(self):
		event=self.capture()["event"];s=f.append(self.root,self.m,"active",owner="legacy")
		with self.assertRaises(AssertionError): inbox.dispatch(self.root,event,"legacy",s["epoch"])
		with self.assertRaises(AssertionError): f.admit(self.root,"legacy",s["epoch"],"payment")
		path=self.root/"target/payment-key.json";value=json.loads(path.read_text());value["member"]=str(uuid.uuid4());path.write_text(json.dumps(value))
		with self.assertRaises(AssertionError): payment.key(self.root)
		self.assertEqual(inbox.inventory(self.root),{"captured":1})
if __name__=="__main__": unittest.main()
